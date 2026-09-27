using System.Text.Json;
using System.Text.RegularExpressions;

namespace ckapi.Utils;

/// <summary>
/// 老师图鉴 laoshi.ink 的演员档案 —— 只取三样：出生日期、别名（日文名/罗马音/曾用名）、头像。
///
/// 站点是纯静态 HTML（Cloudflare 前置），字段既在可见的 .spot-fact-table 里，
/// 也在 &lt;script type="application/ld+json"&gt; 的 Person 节点里；别名与头像走 JSON-LD，
/// 生日只有事实表有，所以两处都要读。
///
/// 档案地址是 actor-001 这种不透明编号，**名字拼不出 URL**，必须先取一次索引页
/// （1 个请求换 686 条 姓名→编号）。索引只在内存里活 30 分钟，不落库：
/// 站方 /terms 明写不得批量抓取，我们把对方整份档案存进本地库是更大的动作。
///
/// 认错人比抓不到严重得多，判定口径和 av-wiki 一致：姓名或曾用名**恰好命中一个**档案才算数。
/// </summary>
public static class LaoshiWiki
{
    public const string Site = "https://laoshi.ink";
    public const string Host = "laoshi.ink";
    public const string IndexUrl = Site + "/actresses/";

    /// <summary>头像按中文名直链寻址，不碰详情页也能拿到</summary>
    public static string PortraitUrl(string name) =>
        $"{Site}/assets/img/celebrities/jav/{Uri.EscapeDataString(name.Trim() + ".jpg")}";

    /// <summary>索引里的一条：档案编号 + 站上写的那个姓名</summary>
    public readonly record struct Hit(string Num, string Display);

    public readonly record struct Profile(
        string Name, string Num, string? BirthdateRaw, List<string> Aliases, string? Portrait);

    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim IndexGate = new(1, 1);
    private static Dictionary<string, Hit> _index = new(StringComparer.Ordinal);
    private static DateTime _indexAt;
    private static readonly TimeSpan IndexTtl = TimeSpan.FromMinutes(30);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", AvWiki.BrowserUa);
        client.DefaultRequestHeaders.Accept.TryParseAdd("text/html,application/json;q=0.9,*/*;q=0.5");
        return client;
    }

    /// <summary>
    /// 姓名 → 档案编号（"001"）。整个批量任务共用这一份内存索引，只问站点一次。
    /// </summary>
    public static async Task<Dictionary<string, Hit>> IndexAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _indexAt < IndexTtl && _index.Count > 0) return _index;

        await IndexGate.WaitAsync(ct);
        try
        {
            if (DateTime.UtcNow - _indexAt < IndexTtl && _index.Count > 0) return _index;

            var html = await GetAsync(IndexUrl, ct);
            var map = new Dictionary<string, Hit>(StringComparer.Ordinal);
            foreach (Match m in Regex.Matches(
                         html, @"/actresses/actor-(\d+)\.html""[^>]*><span>#[^<]*</span><strong>([^<]+)</strong>"))
            {
                var display = Clean(m.Groups[2].Value);
                if (display.Length > 0) map[NoSpace(display)] = new Hit(m.Groups[1].Value, display);
            }

            if (map.Count == 0) throw new InvalidOperationException("索引页没解析出任何档案，站点结构可能变了");

            _index = map;
            _indexAt = DateTime.UtcNow;
            return _index;
        }
        finally
        {
            IndexGate.Release();
        }
    }

    /// <summary>取一份档案。编号形如 "002"；页面缺字段一律返回 null 而不是猜</summary>
    public static async Task<Profile?> ProfileAsync(string num, CancellationToken ct)
    {
        var html = await GetAsync($"{Site}/actresses/actor-{Uri.EscapeDataString(num)}", ct);

        var name = Fact(html, "中文名") ?? FirstGroup(html, @"<h1[^>]*>([^<]+)");
        var birthdate = Fact(html, "出生日期");
        var aliases = new List<string>();

        // JSON-LD 的 alternateName 已经把 日文名/罗马音/曾用名 合成一份，优先用它
        foreach (Match block in Regex.Matches(html,
                     @"<script type=""application/ld\+json"">(.*?)</script>", RegexOptions.Singleline))
        {
            foreach (var alias in PersonAliases(block.Groups[1].Value)) aliases.Add(alias);
        }

        foreach (var key in new[] { "日文名", "罗马音", "别名" })
        {
            var val = Fact(html, key);
            if (val is null) continue;
            // 事实表里的别名一格可能塞好几个，分隔符按顿号/逗号都见过
            foreach (var part in Regex.Split(val, "[、,，/]")) aliases.Add(Clean(part));
        }

        var portrait = FirstGroup(html, @"""image""\s*:\s*""([^""]+)""");
        if (string.IsNullOrWhiteSpace(name)) return null;

        return new Profile(name, num, birthdate,
            aliases.Where(a => a.Length > 0).Distinct(StringComparer.Ordinal).ToList(), portrait);
    }

    /// <summary>
    /// .spot-fact-table 的 &lt;span&gt;标签&lt;/span&gt;&lt;strong&gt;值&lt;/strong&gt; 对。
    /// 站方把缺的字段写成「待补充」「暂未确认」这类占位，一律当没有。
    /// </summary>
    private static string? Fact(string html, string label)
    {
        var m = Regex.Match(html,
            $@"<span>\s*{Regex.Escape(label)}[^<]*</span>\s*<strong>(.*?)</strong>", RegexOptions.Singleline);
        if (!m.Success) return null;

        var val = Clean(Regex.Replace(m.Groups[1].Value, "<[^>]+>", ""));
        return IsPlaceholder(val) ? null : val;
    }

    private static readonly string[] Placeholders =
        { "待补充", "暂未确认", "暂未收录", "待核实", "未公布", "待更新", "无", "-", "—", "/" };

    private static bool IsPlaceholder(string v) =>
        v.Length == 0 || v.Length > 120 ||
        Placeholders.Any(p => v == p || v.StartsWith(p) && p.Length >= 3);

    private static IEnumerable<string> PersonAliases(string json)
    {
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(json).RootElement;
        }
        catch (JsonException)
        {
            yield break;
        }

        foreach (var node in EnumeratePersons(root))
        {
            if (!node.TryGetProperty("alternateName", out var alt)) continue;
            switch (alt.ValueKind)
            {
                case JsonValueKind.String:
                    yield return Clean(alt.GetString() ?? "");
                    break;
                case JsonValueKind.Array:
                    foreach (var item in alt.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.String)
                            yield return Clean(item.GetString() ?? "");
                    break;
            }
        }
    }

    /// <summary>Person 可能直接是根，也可能裹在 @graph 数组里</summary>
    private static IEnumerable<JsonElement> EnumeratePersons(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (Is(root, "Person")) yield return root;
            if (root.TryGetProperty("@graph", out var graph))
            {
                foreach (var e in EnumeratePersons(graph)) yield return e;
            }

            yield break;
        }

        if (root.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in root.EnumerateArray())
        {
            if (Is(item, "Person")) yield return item;
            if (item.TryGetProperty("@graph", out var g))
            {
                foreach (var e in EnumeratePersons(g)) yield return e;
            }
        }
    }

    private static bool Is(JsonElement node, string type) =>
        node.TryGetProperty("@type", out var t) &&
        t.ValueKind == JsonValueKind.String && t.GetString() == type;

    private static async Task<string> GetAsync(string url, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(ct);

        var blocked = WebProbe.Challenge(body);
        if (blocked is not null) throw new InvalidOperationException(blocked);
        return body;
    }

    /// <summary>下载头像字节。类型不认、超过 8 MB 或读不到都返回 null</summary>
    public static async Task<(byte[] Bytes, string Ext)?> DownloadAsync(string url, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return null;

        var ext = (resp.Content.Headers.ContentType?.MediaType ?? "") switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/webp" => ".webp",
            _ => ""
        };
        if (ext.Length == 0) return null;

        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        return bytes.Length is > 0 and < 8 * 1024 * 1024 ? (bytes, ext) : null;
    }

    private static string? FirstGroup(string html, string pattern)
    {
        var m = Regex.Match(html, pattern, RegexOptions.Singleline);
        var v = m.Success ? Clean(m.Groups[1].Value) : "";
        return v.Length == 0 || IsPlaceholder(v) ? null : v;
    }

    private static string Clean(string raw) =>
        System.Net.WebUtility.HtmlDecode(Regex.Replace(raw, @"\s+", " ")).Trim();

    /// <summary>比对用键：去掉所有空白与全角空格。中日文字形不同就是不同的人，不做繁简归一</summary>
    public static string NoSpace(string raw) => Regex.Replace(raw ?? "", @"[\s　]+", "");
}

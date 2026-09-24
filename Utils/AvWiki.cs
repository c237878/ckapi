using System.Text.Json;
using System.Text.RegularExpressions;

namespace ckapi.Utils;

/// <summary>
/// av-wiki.net 的女优档案查询 —— 只为拿一张头像。
///
/// 站点是 WordPress，档案块（AV女優名 / 別名義 / 头像直链 / SNS）挂在 tag 的 description 里，
/// 所以走 /wp-json/wp/v2/tags 这个 JSON 接口，不去解析网页排版。
///
/// 认错人比抓不到严重得多：一张别人的脸会长期挂在演员列表上，而且没人会去核对。
/// 因此判定"确定"的口径写死在这里 —— 命中结果里**恰好一个**档案的名字集合
/// 与我们手里的姓名/曾用名对得上，才算数；零个、多个、没有头像，一律跳过并给出原因。
/// </summary>
public static class AvWiki
{
    public readonly record struct Profile(string Name, string Slug, string? Portrait, HashSet<string> Names, string? X);

    /// <summary>抓取结论：Portrait 非空即成功；否则 Reason 说明为什么不动手</summary>
    public readonly record struct Hit(Profile? Profile, string Reason)
    {
        public bool Ok => Profile?.Portrait is not null;
    }

    private const int PerQuery = 10;
    private const int MinNameLength = 2;

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        // 站点对无 UA 的请求会直接 403
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        client.DefaultRequestHeaders.Accept.TryParseAdd("application/json,image/*");
        return client;
    }

    /// <summary>从 av-wiki 的链接里取 tag slug；不是本站或取不到就返回 null</summary>
    public static string? SlugOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)) return null;
        if (!parsed.Host.EndsWith("av-wiki.net", StringComparison.OrdinalIgnoreCase)) return null;

        var m = Regex.Match(parsed.AbsolutePath, @"/av-actress/([^/]+)");
        if (m.Success) return Uri.UnescapeDataString(m.Groups[1].Value);

        // 也接受 ?tag=<slug> 这种老形式
        var mt = Regex.Match(parsed.Query, @"[?&]tag=([^&]+)");
        return mt.Success ? Uri.UnescapeDataString(mt.Groups[1].Value) : null;
    }

    /// <summary>按名字模糊查 tag。查询词由调用方保证非空</summary>
    public static async Task<List<Profile>> SearchAsync(string query, CancellationToken ct = default)
    {
        var url = $"/wp-json/wp/v2/tags?per_page={PerQuery}&search={Uri.EscapeDataString(query)}";
        return await GetAsync(url, ct);
    }

    /// <summary>按 slug 精确取一个 tag（用于我们自己库里已有的档案链接）</summary>
    public static async Task<List<Profile>> BySlugAsync(string slug, CancellationToken ct = default)
    {
        var url = $"/wp-json/wp/v2/tags?per_page=1&slug={Uri.EscapeDataString(slug)}";
        return await GetAsync(url, ct);
    }

    private static async Task<List<Profile>> GetAsync(string path, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(new Uri("https://av-wiki.net" + path), ct);
        resp.EnsureSuccessStatusCode();
        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (doc.RootElement.ValueKind != JsonValueKind.Array) return new List<Profile>();
        return doc.RootElement.EnumerateArray()
            .Select(Parse)
            .OfType<Profile>()      // 连 name 都没有的条目（错误响应之类）直接丢
            .ToList();
    }

    private static Profile? Parse(JsonElement tag)
    {
        if (tag.TryGetProperty("name", out var nameEl) is false) return null;
        var name = nameEl.GetString() ?? "";
        var slug = tag.TryGetProperty("slug", out var s) ? s.GetString() ?? "" : "";
        var desc = tag.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";

        var names = new HashSet<string>(StringComparer.Ordinal) { Norm(name) };
        string? portrait = null;
        string? x = null;

        // 档案块是 <dt>字段名</dt><dd>值</dd> 的结构，逐对取出来比按整段正则稳
        foreach (Match m in Regex.Matches(desc, @"<dt>(.*?)</dt>\s*<dd>(.*?)</dd>", RegexOptions.Singleline))
        {
            var key = StripTags(m.Groups[1].Value);
            var val = StripTags(m.Groups[2].Value);

            if (key.StartsWith("AV女優名"))
            {
                names.Add(Norm(CutName(val)));
            }
            else if (key.StartsWith("別名義"))
            {
                foreach (var part in Regex.Split(val, "[、,，]"))
                    names.Add(Norm(CutName(part)));
            }
            else if (key.StartsWith("SNS"))
            {
                var mx = Regex.Match(val, @"X[：:]\s*@?([A-Za-z0-9_]+)");
                if (mx.Success) x = mx.Groups[1].Value;
            }
        }

        var img = Regex.Match(desc, @"actress-image[^>]*>.*?<img[^>]*src=""([^""]+)""", RegexOptions.Singleline);
        if (!img.Success) img = Regex.Match(desc, @"<img[^>]*src=""([^""]+)""", RegexOptions.Singleline);
        if (img.Success) portrait = img.Groups[1].Value;

        names.RemoveWhere(n => string.IsNullOrEmpty(n));
        return new Profile(name, slug, portrait, names, x);
    }

    private static string StripTags(string html) =>
        System.Net.WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", "")).Trim();

    /// <summary>「響つかさ（ひびきつかさ）- tsukasa hibiki」只取正文名，罗马音与假名读音丢掉</summary>
    private static string CutName(string raw) => Regex.Split(raw, "[（(\\-–]")[0].Trim();

    /// <summary>比对用：去空白、全角空格，其余保持原样 —— 中日文字形不同就是不同的人，不做繁简归一</summary>
    private static string Norm(string raw) => Regex.Replace(raw, @"[\s　]+", "");

    /// <summary>
    /// 在候选档案里找"确定的那一个"。
    /// 返回 Ok 当且仅当恰好一个档案的名字集合与我们已知的名字有交集（交集项 ≥2 字）。
    /// </summary>
    public static Hit Certify(IEnumerable<Profile> candidates, IEnumerable<string> ourNames)
    {
        var ours = ourNames.Select(Norm).Where(n => n.Length >= MinNameLength).ToHashSet(StringComparer.Ordinal);
        if (ours.Count == 0) return new Hit(null, "库里没有可查的名字");

        var matched = new List<Profile>();
        foreach (var p in candidates)
        {
            var hit = p.Names.Any(n => n.Length >= MinNameLength && ours.Contains(n));
            if (hit) matched.Add(p);
        }

        if (matched.Count == 0) return new Hit(null, "名字对不上，跳过");
        if (matched.Count > 1) return new Hit(null, $"有 {matched.Count} 个档案都匹配，认不出是哪个");

        var one = matched[0];
        return one.Portrait is null
            ? new Hit(null, $"档案「{one.Name}」里没有头像")
            : new Hit(one, "");
    }

    /// <summary>下载头像字节。不是我们认得的图片类型、超过 8 MB 或读不到都返回 null</summary>
    public static async Task<(byte[] Bytes, string Ext)?> DownloadAsync(string url, CancellationToken ct = default)
    {
        using var resp = await Http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();

        // 只按 Content-Type 判类型：这些 URL 常常没有扩展名（DMM 的图床就是如此）
        var ext = (resp.Content.Headers.ContentType?.MediaType ?? "") switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/webp" => ".webp",
            _ => ""
        };
        if (ext.Length == 0) return null;

        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length == 0 || bytes.Length > 8 * 1024 * 1024) return null;

        return (bytes, ext);
    }
}

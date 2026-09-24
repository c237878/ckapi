namespace ckapi.Utils;

/// <summary>演员外链。kind 取值见 <see cref="Links.Kinds"/>。</summary>
public sealed class ActorLink
{
    public string Kind { get; set; } = Links.Other;
    public string Url { get; set; } = "";
}

/// <summary>
/// 演员外链（个人主页 / 资料站 / 社交账号）的统一处理：归类、校验、从旧简介里抽取。
///
/// 这些值最终会变成前端里的 &lt;a href&gt;，所以"只放行 http/https"是必须的，
/// 不是防御性冗余——历史上它们一直躺在 bio 里没人解析过，等于没被信任过。
/// </summary>
public static class Links
{
    public const string Homepage = "homepage";
    public const string Profile = "profile";
    public const string Twitter = "twitter";
    public const string Instagram = "instagram";
    public const string Other = "other";

    /// <summary>kind 白名单；不在表里的值一律按域名重新归类</summary>
    public static readonly string[] Kinds = { Homepage, Profile, Twitter, Instagram, Other };

    public const int MaxPerActor = 10;
    public const int MaxUrlLength = 300;

    private static readonly (string Pattern, string Kind)[] ByHost =
    {
        ("x.com", Twitter), ("twitter.com", Twitter),
        ("instagram.com", Instagram),
        // 女优资料站：这批数据里占绝大多数（javcup 382 条 / av-wiki 126 条）
        ("javcup.com", Profile), ("av-wiki.net", Profile), ("fanza.com", Profile),
    };

    /// <summary>按域名猜类型，猜不到算 other。只用于缺省，用户手选的类型优先</summary>
    public static string Classify(string url)
    {
        if (!TryHost(url, out var host)) return Other;
        foreach (var (pattern, kind) in ByHost)
        {
            if (host == pattern || host.EndsWith("." + pattern, StringComparison.Ordinal)) return kind;
        }
        return Other;
    }

    /// <summary>校验并去重：非法 scheme、无主机名、超长、重复的直接丢掉</summary>
    public static List<ActorLink> Normalize(IEnumerable<ActorLink>? raw)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<ActorLink>();

        foreach (var item in raw ?? Array.Empty<ActorLink>())
        {
            var url = item?.Url?.Trim() ?? "";
            if (url.Length == 0 || url.Length > MaxUrlLength) continue;
            // 手工粘贴时常省略协议，www. 开头的一定是地址；不补就会被下面的校验静默丢掉
            if (url.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;
            if (!TryHost(url, out _)) continue;

            var requested = item?.Kind ?? "";
            var kind = Kinds.Contains(requested, StringComparer.Ordinal) ? requested : Classify(url);
            // 去重只看地址本身（忽略结尾斜杠与大小写）：同一地址挂两个类型是录错了，
            // 不是两种链接
            if (!seen.Add(url.TrimEnd('/').ToLowerInvariant())) continue;

            list.Add(new ActorLink { Kind = kind, Url = url });
            if (list.Count >= MaxPerActor) return list;
        }

        return list;
    }

    /// <summary>
    /// 从旧 bio 里抽出 URL，返回链接列表与剩下的正文。
    /// 实测 382 位演员的 bio 含 URL，其中 379 位抽完就空了——它们本来就不是简介，是被当成链接容器用了。
    /// </summary>
    public static (List<ActorLink> Found, string Remaining) ExtractFromBio(string? bio)
    {
        var found = new List<ActorLink>();
        if (string.IsNullOrWhiteSpace(bio)) return (found, bio?.Trim() ?? "");

        var rest = System.Text.RegularExpressions.Regex.Replace(
            bio,
            @"(?:(?:https?|ftp)://|www\.)[^\s""'<>]+",
            m =>
            {
                var url = m.Value;
                // www. 开头的补上 https，否则前端里的 href 会被当相对路径
                if (url.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;
                var link = new ActorLink { Url = url, Kind = Classify(url) };
                if (Uri.CheckHostName(new Uri(url).Host) != UriHostNameType.Unknown) found.Add(link);
                return " ";
            });

        return (Normalize(found), System.Text.RegularExpressions.Regex.Replace(rest, @"\s+", " ").Trim());
    }

    private static bool TryHost(string url, out string host)
    {
        host = "";
        // 只放行 http/https：javascript:、data: 这类一旦进了 href 就是 XSS 入口
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return false;
        if (string.IsNullOrEmpty(parsed.Host)) return false;
        host = parsed.Host.ToLowerInvariant();
        return true;
    }
}

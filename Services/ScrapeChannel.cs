using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ckapi.Utils;
using Microsoft.Data.Sqlite;

namespace ckapi.Services;

/// <summary>
/// 可视化配置的抓取通道：一条 = 一个站点，把「去哪查 / 怎么抽 / 要多礼貌」三段都做成数据。
///
/// 为什么做成配置而不是代码：通道会换。av-wiki 一上反爬，他要能在界面上填一个新源接着跑，
/// 而不是等我改一版重新部署。代价是要把"能写哪些列"收死（见 <see cref="Targets"/>），
/// 否则配置项就变成了任意 SQL 的入口。
///
/// 三种 fetch_kind：
///   · builtin —— 抽取逻辑在代码里（av-wiki 女优档案那套"唯一命中才算数"的判定不适合降级成配置），
///     这条通道只管开关、限速、配额、熔断；
///   · json —— 取回 JSON，按点路径取值（`0.title.rendered`）；
///   · html —— 取回 HTML，按正则取捕获组。
///     没上 CSS 选择器是因为要引 AngleSharp，一个依赖换一个写法不划算；正则丑但够用，
///     真要选择器再单独议。
/// </summary>
public sealed class ScrapeChannelService
{
    private readonly ILogger<ScrapeChannelService> _logger;
    private readonly SQLiteHelper _db;

    /// <summary>每条通道的"上一次什么时候问过"，进程内计时用；配额与冷却落在表里</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> LastAsk = new();
    private static readonly SemaphoreSlim Serial = new(1, 1);

    public ScrapeChannelService(ILogger<ScrapeChannelService> logger, SQLiteHelper db)
    {
        _logger = logger;
        _db = db;
    }

    // ---------------------------------------------------------------- 模型

    public sealed class Channel
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        /// <summary>actor 女优 / video 影片</summary>
        public string Entity { get; set; } = "video";
        public bool Enabled { get; set; } = true;
        /// <summary>拿实体的哪个字段当查询词：code 番号 · name 姓名 · slug 从已有档案链接里取</summary>
        public string QuerySource { get; set; } = "code";
        /// <summary>请求地址模板，{q} 是 URL 编码后的查询词，{raw} 是不编码的</summary>
        public string FetchUrl { get; set; } = "";
        public string FetchKind { get; set; } = "json";
        public string? Referer { get; set; }
        public string? UserAgent { get; set; }
        /// <summary>抽取规则，JSON 数组，形状见 <see cref="Rule"/></summary>
        public string Rules { get; set; } = "[]";
        /// <summary>
        /// 身份校验：从页面里抽出"这页讲的是谁/哪部片"，要求它包含查询词，否则整条作废。
        /// 空表示不校验。详见 <see cref="VerifyIdentity"/>。
        /// </summary>
        public string? IdentityRegex { get; set; }
        public int MinIntervalMs { get; set; } = 1500;
        public int DailyQuota { get; set; } = 300;
        public int FailLimit { get; set; } = 3;
        public int CooldownMinutes { get; set; } = 120;
        public string? Note { get; set; }
        // 运行态
        public string? UsedDate { get; set; }
        public int UsedToday { get; set; }
        public int ConsecutiveFails { get; set; }
        public string? BlockedUntil { get; set; }
        public string? LastError { get; set; }
        public string? LastOkAt { get; set; }
    }

    public sealed class Rule
    {
        /// <summary>写去哪，必须在 Targets 白名单里</summary>
        public string Target { get; set; } = "";
        /// <summary>json：点路径；html：正则（取第一个捕获组，没有捕获组就取整段匹配）</summary>
        public string Path { get; set; } = "";
        /// <summary>trim · jdate（日式年月日 → ISO）· upper · collapse（压掉连续空白）</summary>
        public string? Transform { get; set; }
        /// <summary>默认 true：只填空着的列，他手写过/手改过的不被覆盖</summary>
        public bool IfEmptyOnly { get; set; } = true;
    }

    /// <summary>
    /// 可写目标白名单。配置能指到哪儿就被限到哪儿——不然 rules 里写 `videos.file_path`
    /// 就能把库里的路径改没，配置界面等于开了个任意 UPDATE 的口子。
    /// </summary>
    public static readonly (string Key, string Column, string Label, string Entity)[] Targets =
    {
        ("videos.original_name", "original_name", "影片·原名", "video"),
        ("videos.name", "name", "影片·译名", "video"),
        ("videos.release_date", "release_date", "影片·发行日期", "video"),
        ("videos.code", "code", "影片·番号", "video"),
        ("actors.birthdate", "birthdate", "演员·出生日期", "actor"),
        ("actors.bio", "bio", "演员·简介", "actor"),
        ("actors.country", "country", "演员·地区", "actor"),
        // 这不是列：片商名会解析/新建 studios 并把影片指向它
        ("videos.studio_name", "", "影片·片商名（自动建/挂）", "video"),
    };

    public static bool IsTargetAllowed(string key, out (string Column, string Entity) target)
    {
        foreach (var t in Targets)
        {
            if (t.Key == key) { target = (t.Column, t.Entity); return true; }
        }
        target = default;
        return false;
    }

    // ---------------------------------------------------------------- 读写

    public List<Channel> List()
    {
        const string sql = "SELECT * FROM scrape_channels ORDER BY name";
        using var conn = _db.GetConnection();
        conn.Open();
        using var cmd = new SqliteCommand(sql, conn);
        using var reader = cmd.ExecuteReader();
        var list = new List<Channel>();
        while (reader.Read()) list.Add(Read(reader));
        return list;
    }

    /// <summary>取某个实体的第一条通道（演员侧现在只有一条：内置的 av-wiki）</summary>
    /// <summary>
    /// 找某个实体类型的通道。给了 host 就按地址里的域名认——
    /// 一个源一条通道，配额与熔断才各算各的；认不到不要退到别的通道上去，
    /// 那等于拿 av-wiki 的额度去问老师图鉴，两边的账都会失真。
    /// </summary>
    public Channel? FirstFor(string entity, string? host = null)
    {
        using var conn = _db.GetConnection();
        conn.Open();
        using var cmd = new SqliteCommand(
            host is null
                ? "SELECT * FROM scrape_channels WHERE entity = @e ORDER BY enabled DESC, name LIMIT 1"
                : @"SELECT * FROM scrape_channels WHERE entity = @e AND fetch_url LIKE '%' || @h || '%'
                     ORDER BY enabled DESC, name LIMIT 1", conn);
        cmd.Parameters.AddWithValue("@e", entity);
        if (host is not null) cmd.Parameters.AddWithValue("@h", host);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public Channel? Get(string id)
    {
        using var conn = _db.GetConnection();
        conn.Open();
        using var cmd = new SqliteCommand("SELECT * FROM scrape_channels WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("@id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    private static Channel Read(SqliteDataReader r) => new()
    {
        Id = r.GetString(r.GetOrdinal("id")),
        Name = r.GetString(r.GetOrdinal("name")),
        Entity = r.GetString(r.GetOrdinal("entity")),
        Enabled = r.GetInt32(r.GetOrdinal("enabled")) == 1,
        QuerySource = r.GetString(r.GetOrdinal("query_source")),
        FetchUrl = r.GetString(r.GetOrdinal("fetch_url")),
        FetchKind = r.GetString(r.GetOrdinal("fetch_kind")),
        Referer = Null(r, "referer"),
        UserAgent = Null(r, "user_agent"),
        Rules = r.GetString(r.GetOrdinal("rules")),
        IdentityRegex = Null(r, "identity_regex"),
        MinIntervalMs = r.GetInt32(r.GetOrdinal("min_interval_ms")),
        DailyQuota = r.GetInt32(r.GetOrdinal("daily_quota")),
        FailLimit = r.GetInt32(r.GetOrdinal("fail_limit")),
        CooldownMinutes = r.GetInt32(r.GetOrdinal("cooldown_minutes")),
        Note = Null(r, "note"),
        UsedDate = Null(r, "used_date"),
        UsedToday = r.GetInt32(r.GetOrdinal("used_today")),
        ConsecutiveFails = r.GetInt32(r.GetOrdinal("consecutive_fails")),
        BlockedUntil = Null(r, "blocked_until"),
        LastError = Null(r, "last_error"),
        LastOkAt = Null(r, "last_ok_at"),
    };

    private static string? Null(SqliteDataReader r, string name) =>
        r.IsDBNull(r.GetOrdinal(name)) ? null : r.GetString(r.GetOrdinal(name));

    /// <summary>校验并保存。返回人话形式的校验结果，不合法时不做任何写入</summary>
    public (bool Ok, string Message) Save(Channel c)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(c.Name)) problems.Add("名字不能空");
        if (c.Entity is not ("actor" or "video")) problems.Add("entity 只能是 actor 或 video");
        if (c.QuerySource is not ("code" or "name" or "slug")) problems.Add("查询词只能是 番号 / 姓名 / 档案 slug");
        if (c.FetchKind is not ("builtin" or "json" or "html")) problems.Add("取回类型只能是 builtin / json / html");
        if (c.FetchKind != "builtin" && string.IsNullOrWhiteSpace(c.FetchUrl)) problems.Add("要真发请求就得填地址模板");
        if (c.FetchUrl.Contains("{") && !c.FetchUrl.Contains("{q}") && !c.FetchUrl.Contains("{raw}") && !c.FetchUrl.Contains("{slug}"))
            problems.Add("地址模板里的占位符只认 {q} / {raw} / {slug}");
        if (!c.FetchUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
            !c.FetchUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && c.FetchKind != "builtin")
            problems.Add("地址必须是 http(s)");
        if (c.MinIntervalMs is < 200 or > 600_000) problems.Add("最小间隔只能在 0.2 秒到 10 分钟之间");
        if (c.DailyQuota is < 1 or > 100_000) problems.Add("每日配额只能在 1 到 100000 之间");
        if (c.CooldownMinutes is < 1 or > 7 * 24 * 60) problems.Add("冷却时长只能在 1 分钟到 7 天之间");

        List<Rule> rules;
        try
        {
            rules = ParseRules(c.Rules);
        }
        catch (JsonException ex)
        {
            return (false, "抽取规则不是合法 JSON：" + ex.Message);
        }
        foreach (var rule in rules)
        {
            if (!IsTargetAllowed(rule.Target, out var t))
                problems.Add($"目标「{rule.Target}」不在可写清单里");
            else if (t.Entity != c.Entity)
                problems.Add($"目标「{rule.Target}」是 {t.Entity} 的字段，这条通道是 {c.Entity} 的");
            if (string.IsNullOrWhiteSpace(rule.Path)) problems.Add($"「{rule.Target}」没写取值路径");
            if (c.FetchKind == "html" && rule.Path.Length > 0 && !IsValidRegex(rule.Path))
                problems.Add($"「{rule.Target}」的正则不合法");
        }
        if (c.FetchKind == "json" && rules.Count > 0 && rules.All(x => x.Path.Length == 0))
            problems.Add("json 通道至少要给一个字段写路径");
        // 身份校验的正则也要在这里过一遍：填错了不该等到批量跑起来才发现一条都没写进去
        if (!string.IsNullOrWhiteSpace(c.IdentityRegex) && !IsValidRegex(c.IdentityRegex))
            problems.Add("身份校验的正则不合法");

        if (problems.Count > 0) return (false, string.Join("；", problems));

        const string sql = @"
            INSERT INTO scrape_channels
                (id, name, entity, enabled, query_source, fetch_url, fetch_kind, referer, user_agent,
                 rules, identity_regex, min_interval_ms, daily_quota, fail_limit, cooldown_minutes, note, ctime, utime)
            VALUES (@id, @name, @entity, @enabled, @qs, @url, @kind, @ref, @ua, @rules, @identity,
                    @interval, @quota, @fail, @cool, @note, @now, @now)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name, entity = excluded.entity, enabled = excluded.enabled,
                query_source = excluded.query_source, fetch_url = excluded.fetch_url,
                fetch_kind = excluded.fetch_kind, referer = excluded.referer, user_agent = excluded.user_agent,
                rules = excluded.rules, identity_regex = excluded.identity_regex,
                min_interval_ms = excluded.min_interval_ms,
                daily_quota = excluded.daily_quota, fail_limit = excluded.fail_limit,
                cooldown_minutes = excluded.cooldown_minutes, note = excluded.note,
                utime = excluded.utime";

        using var conn = _db.GetConnection();
        conn.Open();
        using var cmd = new SqliteCommand(sql, conn);
        if (string.IsNullOrEmpty(c.Id)) c.Id = Guid.NewGuid().ToString("N").ToUpper();
        cmd.Parameters.AddWithValue("@id", c.Id);
        cmd.Parameters.AddWithValue("@name", c.Name.Trim());
        cmd.Parameters.AddWithValue("@entity", c.Entity);
        cmd.Parameters.AddWithValue("@enabled", c.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@qs", c.QuerySource);
        cmd.Parameters.AddWithValue("@url", c.FetchUrl.Trim());
        cmd.Parameters.AddWithValue("@kind", c.FetchKind);
        cmd.Parameters.AddWithValue("@ref", (object?)c.Referer?.Trim() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ua", (object?)c.UserAgent?.Trim() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@rules", JsonSerializer.Serialize(rules, RuleJson));
        cmd.Parameters.AddWithValue("@identity", (object?)c.IdentityRegex?.Trim() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@interval", c.MinIntervalMs);
        cmd.Parameters.AddWithValue("@quota", c.DailyQuota);
        cmd.Parameters.AddWithValue("@fail", c.FailLimit);
        cmd.Parameters.AddWithValue("@cool", c.CooldownMinutes);
        cmd.Parameters.AddWithValue("@note", (object?)c.Note?.Trim() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@now", Now());
        cmd.ExecuteNonQuery();
        return (true, "已保存");
    }

    public bool Delete(string id)
    {
        using var conn = _db.GetConnection();
        conn.Open();
        using var cmd = new SqliteCommand("DELETE FROM scrape_channels WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("@id", id);
        LastAsk.TryRemove(id, out _);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>手动清冷却与当日计数：换了 IP 或确认对方放行时用</summary>
    public void Reset(string id)
    {
        using var conn = _db.GetConnection();
        conn.Open();
        using var cmd = new SqliteCommand(@"
            UPDATE scrape_channels SET consecutive_fails = 0, blocked_until = NULL,
                   used_today = 0, used_date = NULL, last_error = NULL
            WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
        LastAsk.TryRemove(id, out _);
    }

    /// <summary>
    /// 一键开关这条通道，返回翻转后的状态。
    /// 单独一个接口而不是让前端回整份配置：编辑对话框里改一半没保存时，
    /// 开关不该顺手把 rules 也写回去。
    /// </summary>
    public bool? Toggle(string id)
    {
        using var conn = _db.GetConnection();
        conn.Open();
        using var cmd = new SqliteCommand(
            "UPDATE scrape_channels SET enabled = 1 - enabled, utime = @t WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@t", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        if (cmd.ExecuteNonQuery() == 0) return null;

        bool on;
        using (var q = new SqliteCommand("SELECT enabled FROM scrape_channels WHERE id = @id", conn))
        {
            q.Parameters.AddWithValue("@id", id);
            on = Convert.ToInt32(q.ExecuteScalar()) == 1;
        }
        if (!on) LastAsk.TryRemove(id, out _);
        return on;
    }

    // ---------------------------------------------------------------- 礼貌闸门

    public readonly record struct Gate(bool Allowed, string Why);

    /// <summary>
    /// 问一句"现在能不能再问这个站"。三条都在拦：开关、冷却中、今天配额用满。
    /// 最小间隔不在这里判（那是等一会儿就行，不是不能问），由 <see cref="WaitTurnAsync"/> 等出来。
    /// </summary>
    public Gate Check(Channel c)
    {
        if (!c.Enabled) return new Gate(false, "这条通道被关着");

        if (!string.IsNullOrEmpty(c.BlockedUntil) &&
            DateTime.TryParse(c.BlockedUntil, out var until) && until > DateTime.Now)
            return new Gate(false, $"冷却中（{until:HH:mm} 前不再问；上次：{c.LastError}）");

        var today = DateTime.Now.ToString("yyyy-MM-dd");
        if (c.UsedDate == today && c.UsedToday >= c.DailyQuota)
            return new Gate(false, $"今天已问 {c.UsedToday} 次，到配额 {c.DailyQuota} 了");

        return new Gate(true, "");
    }

    /// <summary>串行 + 按通道最小间隔等够再说"可以问"。批量跑时它就是那个刹车。</summary>
    public async Task WaitTurnAsync(Channel c, CancellationToken ct)
    {
        await Serial.WaitAsync(ct);
        try
        {
            if (LastAsk.TryGetValue(c.Id, out var last))
            {
                var wait = c.MinIntervalMs - (int)(DateTime.Now - last).TotalMilliseconds;
                if (wait > 0) await Task.Delay(wait, ct);
            }
            LastAsk[c.Id] = DateTime.Now;
        }
        finally
        {
            Serial.Release();
        }
    }

    /// <summary>一次请求的结局写回运行态：成功清失败计数，失败累计到阈值就进冷却</summary>
    public void Report(string id, bool ok, string? error = null)
    {
        var now = DateTime.Now;
        const string okSql = @"
            UPDATE scrape_channels
            SET used_today = CASE WHEN used_date = @today THEN used_today + 1 ELSE 1 END,
                used_date = @today,
                consecutive_fails = 0, blocked_until = NULL, last_error = NULL, last_ok_at = @now
            WHERE id = @id";
        // 失败也计入配额：被拒的时候最不该做的事就是继续敲
        const string badSql = @"
            UPDATE scrape_channels
            SET used_today = CASE WHEN used_date = @today THEN used_today + 1 ELSE 1 END,
                used_date = @today,
                consecutive_fails = consecutive_fails + 1,
                last_error = @err,
                blocked_until = CASE WHEN consecutive_fails + 1 >= @limit
                                     THEN @until ELSE blocked_until END
            WHERE id = @id";

        using var conn = _db.GetConnection();
        conn.Open();
        using var cmd = new SqliteCommand(ok ? okSql : badSql, conn);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@today", now.ToString("yyyy-MM-dd"));
        cmd.Parameters.AddWithValue("@now", now.ToString("yyyy-MM-dd HH:mm:ss"));
        if (!ok)
        {
            cmd.Parameters.AddWithValue("@err", Truncate(error ?? "未知错误", 300));
            cmd.Parameters.AddWithValue("@limit", Math.Max(1, Get(id)?.FailLimit ?? 3));
            cmd.Parameters.AddWithValue("@until",
                now.AddMinutes(Math.Max(1, Get(id)?.CooldownMinutes ?? 120)).ToString("yyyy-MM-dd HH:mm:ss"));
        }
        cmd.ExecuteNonQuery();
    }

    // ---------------------------------------------------------------- 取与抽

    /// <summary>试抓：只回"抽到了什么"，一个字都不写库。配置界面靠它闭环，不用改一版部署一次</summary>
    public async Task<(bool Ok, string Message, Dictionary<string, string> Found)> FetchAsync(
        Channel c, string queryValue, CancellationToken ct = default)
    {
        var url = BuildUrl(c, queryValue);
        if (url is null) return (false, "地址模板不合法", new Dictionary<string, string>());

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                string.IsNullOrWhiteSpace(c.UserAgent) ? AvWiki.BrowserUa : c.UserAgent);
            if (!string.IsNullOrWhiteSpace(c.Referer))
                http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", c.Referer);
            http.DefaultRequestHeaders.Accept.TryParseAdd("application/json,text/html;q=0.9,*/*;q=0.5");

            var resp = await http.GetAsync(url, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            // 反爬拦截有两种长相：一种是 403 + 一句 "Access denied by Imunify360"，
            // 一种是 200 + 一段 "One moment, please" 的 JS 挑战页。
            // 两种都要报成"被拦"并说清是哪一种——报成 HTTP 403 或"没抽到"，
            // 他就会以为是数据不全，继续按原计划再敲几千次。
            var challenge = DetectChallenge(body);
            if (challenge is not null) return (false, challenge, new Dictionary<string, string>());
            if (!resp.IsSuccessStatusCode)
                return (false, $"HTTP {(int)resp.StatusCode} {(string.IsNullOrEmpty(body) ? resp.ReasonPhrase : "")}".Trim(),
                    new Dictionary<string, string>());

            var found = Extract(c, body);
            if (c.FetchKind == "builtin")
                return (true, $"站点应答正常（{body.Length} 字节）。内置通道的抽取在代码里，这里不解析字段", found);

            // 身份校验放在抽取之前，且"没过"要报成没数据而不是报成失败（见 VerifyIdentity 注释）
            var mismatch = VerifyIdentity(c, body, queryValue);
            if (mismatch is not null) return (true, mismatch, new Dictionary<string, string>());

            return (true, $"抽到 {found.Count} 个字段", found);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return (false, "站点无响应：" + ex.Message, new Dictionary<string, string>());
        }
    }

    private static string? BuildUrl(Channel c, string queryValue)
    {
        var tpl = c.FetchUrl ?? "";
        if (tpl.Length == 0) return null;
        var enc = Uri.EscapeDataString(queryValue ?? "");
        var raw = Uri.EscapeDataString(queryValue ?? "");
        var url = tpl.Replace("{q}", enc).Replace("{raw}", raw).Replace("{slug}", Uri.EscapeDataString(Slug(queryValue ?? "")));
        return Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? url : null;
    }

    private static string Slug(string s) =>
        new string(s.Trim().ToLowerInvariant().Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').ToArray());

    /// <summary>
    /// 这页讲的是不是我们要问的那个人 / 那部片。返回 null 表示通过。
    ///
    /// 为什么必须有：站方对"查无此人"的回答不是 404，而是 200 + 首页或列表页。
    /// 实测 javcup：/actors/二宫和香 会落到首页，而首页正文里正好夹着别人的生年月日与档案行；
    /// 引擎取的是正则的第一个命中，于是那位陌生人的生日就被写进了这位演员的库里。
    /// 这比抓不到危险得多——抓不到看得见，认错人长期没人会去核对。
    ///
    /// 不通过要报成"站上没有"（成功但不给字段），不能报成失败：熔断是留给对方真翻脸的，
    /// 别让"这个名字对不上"累计三次把整条通道关进两小时的冷却里。
    /// 没配 identity_regex 就不校验，所以内置通道与老通道行为不变。
    /// </summary>
    private static string? VerifyIdentity(Channel c, string body, string? queryValue)
    {
        if (string.IsNullOrWhiteSpace(c.IdentityRegex)) return null;
        if (string.IsNullOrWhiteSpace(queryValue)) return "没给查询词，无法做身份校验，未取任何字段";

        Match m;
        try { m = Regex.Match(body, c.IdentityRegex, RegexOptions.IgnoreCase); }
        catch (ArgumentException) { return "身份校验的正则不合法，未取任何字段"; }
        if (!m.Success) return "身份校验没在页面上抽到值（这页大概不是档案页），未取任何字段";

        var subject = (m.Groups.Count > 1 && m.Groups[1].Success ? m.Groups[1].Value : m.Value).Trim();
        if (Normalize(subject).Contains(Normalize(queryValue))) return null;
        return $"页面上是「{Truncate(subject, 40)}」，不是「{Truncate(queryValue, 40)}」，未取任何字段";
    }

    /// <summary>比对用的归一：压掉空白、统一大小写（番号有的存小写，页面上是大写）</summary>
    private static string Normalize(string s) =>
        new string(s.Where(ch => !char.IsWhiteSpace(ch)).ToArray()).ToUpperInvariant();

    /// <summary>挑战页/拒答页的指纹。判定逻辑与内置档案源共用一份，见 <see cref="WebProbe"/></summary>
    public static string? DetectChallenge(string body) => WebProbe.Challenge(body);

    private static Dictionary<string, string> Extract(Channel c, string body)
    {
        var rules = ParseRules(c.Rules);
        var found = new Dictionary<string, string>();
        JsonDocument? doc = null;
        try
        {
            if (c.FetchKind == "json")
            {
                try { doc = JsonDocument.Parse(body); }
                catch (JsonException) { return found; }
            }

            foreach (var rule in rules)
            {
                var value = c.FetchKind switch
                {
                    "json" when doc is not null => JsonPath(doc.RootElement, rule.Path),
                    "html" => RegexGrab(body, rule.Path),
                    _ => null
                };
                value = Transform(value, rule.Transform);
                if (!string.IsNullOrEmpty(value)) found[rule.Target] = value;
            }
        }
        finally
        {
            doc?.Dispose();
        }
        return found;
    }

    /// <summary>点路径取值：`0.title.rendered`；数组用下标，对象用键名</summary>
    private static string? JsonPath(JsonElement node, string path)
    {
        var cur = node;
        foreach (var seg in (path ?? "").Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (cur.ValueKind == JsonValueKind.Array)
            {
                if (!int.TryParse(seg, out var i) || i >= cur.GetArrayLength()) return null;
                cur = cur[i];
            }
            else if (cur.ValueKind == JsonValueKind.Object)
            {
                if (!cur.TryGetProperty(seg, out var next)) return null;
                cur = next;
            }
            else return null;
        }

        return cur.ValueKind switch
        {
            JsonValueKind.String => cur.GetString(),
            JsonValueKind.Number => cur.GetRawText(),
            JsonValueKind.True or JsonValueKind.False => cur.GetRawText(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => cur.GetRawText()
        };
    }

    private static string? RegexGrab(string body, string pattern)
    {
        try
        {
            var m = Regex.Match(body, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!m.Success) return null;
            return m.Groups.Count > 1 ? m.Groups[1].Value : m.Value;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string? Transform(string? value, string? kind)
    {
        if (value is null) return null;
        var v = kind switch
        {
            "jdate" => JapaneseDate(value),
            "upper" => value.ToUpperInvariant(),
            "collapse" => Regex.Replace(value, @"\s+", " ").Trim(),
            _ => value
        };
        return (v ?? "").Trim();
    }

    /// <summary>
    /// 日式/中文日期 → ISO：`1987年5月16日` → `1987-05-16`，只给到年月就存 `1987-05`。
    /// 与演员生日、影片发行日期同一套口径：**不补 01 号**，补出来的是假数据。
    /// </summary>
    public static string? JapaneseDate(string raw)
    {
        var m = Regex.Match(raw ?? "", @"(20\d{2}|19\d{2})\s*[年\-/.]\s*(\d{1,2})\s*(?:[月\-/.]\s*(\d{1,2}))?");
        if (!m.Success) return null;
        var y = m.Groups[1].Value;
        var mo = m.Groups[2].Value.PadLeft(2, '0');
        if (mo is not ("0" or "1" or "2") && int.TryParse(mo, out var mn) && (mn < 1 || mn > 12)) return null;
        return m.Groups[3].Success
            ? $"{y}-{mo}-{m.Groups[3].Value.PadLeft(2, '0')}"
            : $"{y}-{mo}";
    }

    /// <summary>
    /// 规则 JSON 的读法。必须开大小写不敏感：配置界面与手填 JSON 都写 camelCase，
    /// 而 C# 属性是 PascalCase —— 默认反序列化区分大小写，不开就会静默解出一堆空对象，
    /// 报错还会显示成「目标『』不在清单里」，根本看不出是大小写问题。
    /// </summary>
    public static readonly JsonSerializerOptions RuleJson = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static List<Rule> ParseRules(string json)
    {
        try { return JsonSerializer.Deserialize<List<Rule>>(json, RuleJson) ?? new List<Rule>(); }
        catch (JsonException) { return new List<Rule>(); }
    }

    // ---------------------------------------------------------------- 落地

    /// <summary>
    /// 对某一条记录走一遍通道并落库。异步且内部先等够最小间隔：
    /// 单条应用和批量补数据走的是同一条礼貌路线，不然"应用"就成了绕过刹车的后门。
    /// </summary>
    public async Task<(int Written, List<string> Skipped)> ApplyAsync(
        string channelId, string entityId, CancellationToken ct = default)
    {
        var c = Get(channelId);
        if (c is null) throw new InvalidOperationException("通道不存在");
        using var conn = _db.GetConnection();
        conn.Open();
        await WaitTurnAsync(c, ct);
        return await ApplyTo(conn, c, entityId, ct);
    }

    /// <summary>
    /// 哪些记录还缺这条通道要填的字段，用来给批量任务排候选。
    ///
    /// 列名只从 <see cref="Targets"/> 白名单里来（规则存的是字符串，谁能直接改库就能塞别的列名，
    /// 所以这里坚持查表而不是拼用户输入）；一个目标都没有就返回空，宁可不跑也不乱捞。
    /// 顺序是刻意排的：影片按最新的先问（新片在站上的命中率高得多），
    /// 演员按挂着的片数多的先来（这批人才真的值得花请求）。
    /// </summary>
    public List<string> MissingFor(Channel c, int limit)
    {
        var plan = MissingPlan(c);
        if (plan is null) return new List<string>();
        var (table, keyCol, where) = plan.Value;
        var order = c.Entity == "actor"
            ? "(SELECT COUNT(*) FROM video_actors va WHERE va.actor_id = a.id) DESC, a.name"
            : "a.ctime DESC, a.id";

        var ids = new List<string>();
        using var conn = _db.GetConnection();
        conn.Open();
        using var cmd = new SqliteCommand(
            $"SELECT a.[{keyCol}] FROM [{table}] a WHERE {where} ORDER BY {order} LIMIT @n", conn);
        cmd.Parameters.AddWithValue("@n", Math.Clamp(limit, 1, ChannelBatch.HardLimit));
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetString(0));
        return ids;
    }

    /// <summary>这条通道还能补多少条记录。批量按钮上要显示影响条数，不然点了才知道跑多久</summary>
    public int MissingCount(Channel c)
    {
        var plan = MissingPlan(c);
        if (plan is null) return 0;
        var (table, _, where) = plan.Value;

        using var conn = _db.GetConnection();
        conn.Open();
        using var cmd = new SqliteCommand($"SELECT COUNT(*) FROM [{table}] a WHERE {where}", conn);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>
    /// 组装"还缺这条通道字段"的查询。列名只从 <see cref="Targets"/> 白名单里来
    /// （规则存的是字符串，谁能直接改库就能塞别的列名，所以这里坚持查表而不是拼用户输入）；
    /// 一个可用目标都没有就返回 null，宁可不跑也不乱捞。
    /// </summary>
    private static (string Table, string KeyCol, string Where)? MissingPlan(Channel c)
    {
        var (table, keyCol) = c.Entity == "actor" ? ("actors", "id") : ("videos", "id");
        var empty = new List<string>();
        foreach (var rule in ParseRules(c.Rules))
        {
            if (!IsTargetAllowed(rule.Target, out var t)) continue;
            if (t.Entity != c.Entity) continue;
            // 片商不是列：抽来的名字会解析/新建 studios 再挂，"还缺"就是这部片没片商 id
            var column = rule.Target == "videos.studio_name" ? "studioid" : t.Column;
            if (string.IsNullOrEmpty(column)) continue;
            empty.Add($"IFNULL([{column}], '') = ''");
        }
        if (empty.Count == 0) return null;

        // 演员侧只问日本女优：这两个站都只收她们，拿别人的名字去敲等于白花一次请求
        var gate = c.Entity == "actor" ? $" AND {ActorGate.EligibleSql}" : "";
        return (table, keyCol, $"({string.Join(" OR ", empty.Distinct())}){gate}");
    }

    /// <summary>这条通道现在最多还能问几条：配额减今天已用，再看有没有在冷却里</summary>
    public int RemainingQuota(Channel c) =>
        c.UsedDate == DateTime.Now.ToString("yyyy-MM-dd")
            ? Math.Max(0, c.DailyQuota - c.UsedToday)
            : c.DailyQuota;

    /// <summary>
    /// 把抽到的字段写进实体。两条硬规矩：
    ///   · 目标必须在白名单里（Save 已校验，这里再挡一次，防止有人直接调引擎）；
    ///   · ifEmptyOnly 的规则只填空值——手写过一遍的简介不能被机器覆盖。
    /// </summary>
    private async Task<(int Written, List<string> Skipped)> ApplyTo(
        SqliteConnection conn, Channel c, string entityId, CancellationToken ct = default)
    {
        var (table, keyCol) = c.Entity == "actor" ? ("actors", "id") : ("videos", "id");
        var queryValue = QueryValue(conn, c, table, keyCol, entityId);
        if (string.IsNullOrEmpty(queryValue))
            return (0, new List<string> { "这条记录没有可用作查询词的字段值" });

        var (ok, message, found) = await FetchAsync(c, queryValue, ct);
        Report(c.Id, ok, ok ? null : message);
        if (!ok || found.Count == 0) return (0, new List<string> { ok ? "什么都没抽到" : message });

        var written = 0;
        var skipped = new List<string>();
        foreach (var rule in ParseRules(c.Rules))
        {
            if (!found.TryGetValue(rule.Target, out var value)) continue;
            if (!IsTargetAllowed(rule.Target, out var t)) { skipped.Add($"{rule.Target}：不在白名单"); continue; }

            switch (rule.Target)
            {
                case "videos.studio_name":
                    // 归一优先：抽来的名字先按正名/别名对上已有的片商，对不上才新建
                    var (sid, _) = VideoMeta.ResolveStudio(conn, null, value);
                    VideoMeta.SetStudio(conn, entityId, sid);
                    written++;
                    break;

                default:
                    using (var read = new SqliteCommand($"SELECT [{t.Column}] FROM [{table}] WHERE [{keyCol}] = @id", conn))
                    {
                        read.Parameters.AddWithValue("@id", entityId);
                        var current = read.ExecuteScalar() as string;
                        if (rule.IfEmptyOnly && !string.IsNullOrWhiteSpace(current))
                        {
                            skipped.Add($"{rule.Target}：已有值，按「只填空值」跳过");
                            break;
                        }
                    }
                    using (var upd = new SqliteCommand($"UPDATE [{table}] SET [{t.Column}] = @v WHERE [{keyCol}] = @id", conn))
                    {
                        upd.Parameters.AddWithValue("@v", value);
                        upd.Parameters.AddWithValue("@id", entityId);
                        written += upd.ExecuteNonQuery();
                    }
                    break;
            }
        }
        return (written, skipped);
    }

    /// <summary>界面上"用这条记录试抓"要看到的查询词，与真跑时同一个口径</summary>
    public string? QueryValueFor(Channel c, string entityId)
    {
        var (table, keyCol) = c.Entity == "actor" ? ("actors", "id") : ("videos", "id");
        using var conn = _db.GetConnection();
        conn.Open();
        return QueryValue(conn, c, table, keyCol, entityId);
    }

    /// <summary>取查询词：番号 / 姓名 / 从已有档案链接里抠 slug</summary>
    private static string? QueryValue(SqliteConnection conn, Channel c, string table, string keyCol, string id)
    {
        var sql = c.QuerySource switch
        {
            "name" => $"SELECT name FROM [{table}] WHERE [{keyCol}] = @id",
            "code" => $"SELECT IFNULL(code, '') FROM [{table}] WHERE [{keyCol}] = @id",
            "slug" => c.Entity == "actor"
                ? "SELECT url FROM actor_links WHERE actor_id = @id AND url LIKE '%av-wiki.net%' LIMIT 1"
                : "SELECT url FROM video_links WHERE video_id = @id AND url LIKE '%av-wiki.net%' LIMIT 1",
            _ => null
        };
        if (sql is null) return null;

        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", id);
        var raw = cmd.ExecuteScalar() as string;
        return c.QuerySource == "slug" ? AvWiki.SlugOf(raw) : raw;
    }

    private static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private static bool IsValidRegex(string pattern)
    {
        try { _ = new Regex(pattern); return true; }
        catch (ArgumentException) { return false; }
    }
}

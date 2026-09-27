using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ckapi.Services;

/// <summary>
/// MCP（Model Context Protocol）服务端：给本机大模型读写这套媒体库的口子。
///
/// 手写 JSON-RPC 2.0 而不引 SDK：面只有 initialize / tools/list / tools/call 三个方法，
/// 加依赖换来的是版本漂移和一层不受控的 HTTP 栈。形状与 logapi-finance 保持一致——
/// 同一个 /mcp 端点 + X-API-Key 头。
///
/// 全站仍是局域网免鉴权，只有这里收一道：整站接口是"人看片"用的，AI 能改库这件事得另配一把钥匙。
/// 密钥存在 system_settings.mcpApiKey，由设置页生成；那行是空的就等于没开门。
///
/// 口子收窄在接口而不是提示词里：AI 永远建不出新标签，新词只能进 tag_suggestions 待审。
/// 提示词会被改，接口不会。
/// </summary>
public sealed class McpService
{
    /// <summary>密钥所在的设置行；空值等于 MCP 未启用</summary>
    public const string KeySetting = "mcpApiKey";

    /// <summary>AI 打标的口径，界面可改，tag_propose 的工具描述会带上它</summary>
    public const string RuleSetting = "aiTagRule";

    public const string DefaultRule =
        "从中文片名提炼题材标签。允许推导，不要求片名里出现原词：「爆乳」可以归到已有的「巨乳」，" +
        "「儿子的同学」这类关系可以推成「友人母」。\n" +
        "1. 先读 tag_list，按**意思**而不是字面去对：片名表达的特征词表里已经有了，就用那个词的正名，" +
        "不要再提一个近义新词（巨乳/爆乳/大胸各管一摊，按标签浏览就废了）；\n" +
        "2. 词表里确实没有对应概念的，照常提交，tag_propose 会放进待审队列由人批准或驳回；" +
        "响应里带 similar 字段时，说明词表里有近义项，优先考虑改用它；\n" +
        "3. 标签数量不限，片名里有几个特征就提几个，只有一个就提一个；片名只是番号或泛称、推不出具体特征时，" +
        "这部片就不提，跳过即可；\n" +
        "4. 词要有特色、站得住：按 AV 标签的通常写法和规范取（身份 / 场景 / 行为 / 关系，如「人妻」「痴汉」「颜射」），" +
        "并且要能囊括一批影片——只覆盖这一部的孤立词、以及「好看」「经典」「推荐」这种既烂大街又与 AV 无关的词，都不要提；\n" +
        "5. 演员名、片商名、番号、系列名都不算题材标签；\n" +
        "6. 拿不准就不提。错词要人工清理，漏词下次还能补。";

    private const string ServerName = "ckapi-media";
    private const string ServerVersion = "1.0";
    private const string Protocol = "2025-03-26";

    /// <summary>客户端报上来这几个协议版本都认；不在表里就回自己的，由客户端决定退不退</summary>
    private static readonly string[] KnownProtocol = { "2025-06-18", "2025-03-26", "2024-11-05", "2024-10-07" };

    private const int MaxLimit = 100;
    private const int DefaultLimit = 20;

    private static readonly JsonElement NoArgs = JsonDocument.Parse("{}").RootElement.Clone();

    private static readonly JsonSerializerOptions Out = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly Utils.SQLiteHelper _db;
    private readonly ILogger<McpService> _logger;

    public McpService(Utils.SQLiteHelper db, ILogger<McpService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>一次调用的结果。Body 为 null 表示这是通知，不必回应。</summary>
    public sealed record Outcome(int Status, object? Body);

    // ---------------------------------------------------------------- 入口

    /// <summary>处理一条 JSON-RPC 消息。批量数组不支持——协议 2.0 起已经取消了 batch。</summary>
    public Outcome Handle(string raw)
    {
        JsonElement req;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            req = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Error(null, -32700, "解析不了这段 JSON");
        }

        if (req.ValueKind != JsonValueKind.Object)
            return Error(null, -32600, "请求体必须是一个 JSON-RPC 对象");

        var id = Id(req);
        var method = Text(req, "method");
        if (string.IsNullOrEmpty(method))
            return Error(id, -32600, "缺少 method");

        // 通知不带 id，也不该有回应体——回了反而让客户端把连接当坏掉
        if (method!.StartsWith("notifications/")) return new Outcome(202, null);

        var args = req.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object ? p : NoArgs;

        try
        {
            switch (method)
            {
                case "initialize":
                    return new Outcome(200, Reply(id, Initialize(args)));
                case "ping":
                    return new Outcome(200, Reply(id, new Dictionary<string, object?>()));
                case "tools/list":
                    return new Outcome(200, Reply(id, Tools()));
                case "tools/call":
                    return Call(id, args);
                default:
                    return Error(id, -32601, $"不支持的方法：{method}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MCP {Method} failed", method);
            return Error(id, -32603, Utils.Api.InternalErrorMessage);
        }
    }

    /// <summary>密钥没配就不开门：让"AI 能改库"成为一个要显式动作的开关</summary>
    public bool Authorized(string? provided)
    {
        var want = ApiKey();
        return !string.IsNullOrEmpty(want) && string.Equals(want, provided?.Trim(), StringComparison.Ordinal);
    }

    public string? ApiKey()
    {
        using var conn = Open();
        return Setting(conn, KeySetting);
    }

    /// <summary>当前打标口径；库里没存过就给默认文案（不代写回，用户清空是有意为之）</summary>
    public string Rule()
    {
        using var conn = Open();
        return Setting(conn, RuleSetting) ?? DefaultRule;
    }

    /// <summary>生成并保存新密钥，返回那串十六进制。旧的当场作废，客户端要一起换。</summary>
    public string RotateKey()
    {
        var key = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        using var conn = Open();
        SaveSetting(conn, KeySetting, key);
        return key;
    }

    /// <summary>
    /// upsert 一条设置。system_settings.name 上没有唯一约束，ON CONFLICT 直接报错，
    /// 只能按 /api/systemsetting 那套"先查在不在，再决定 UPDATE 还是 INSERT"。
    /// </summary>
    private static void SaveSetting(SqliteConnection conn, string name, string content)
    {
        var now = VideoMeta.Now();
        using (var exists = new SqliteCommand("SELECT COUNT(*) FROM system_settings WHERE name = @n", conn))
        {
            exists.Parameters.AddWithValue("@n", name);
            if (Convert.ToInt32(exists.ExecuteScalar()) > 0)
            {
                using var upd = new SqliteCommand("UPDATE system_settings SET content = @c, utime = @t WHERE name = @n", conn);
                upd.Parameters.AddWithValue("@c", content);
                upd.Parameters.AddWithValue("@t", now);
                upd.Parameters.AddWithValue("@n", name);
                upd.ExecuteNonQuery();
                return;
            }
        }

        using var ins = new SqliteCommand(
            "INSERT INTO system_settings (id, name, content, ctime, utime) VALUES (@id, @n, @c, @t, @t)", conn);
        ins.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N").ToUpper());
        ins.Parameters.AddWithValue("@n", name);
        ins.Parameters.AddWithValue("@c", content);
        ins.Parameters.AddWithValue("@t", now);
        ins.ExecuteNonQuery();
    }

    /// <summary>设置页要列工具清单，直接复用同一份定义，免得两边文案漂移</summary>
    public List<Dictionary<string, object?>> ToolNames()
    {
        var tools = (List<object>)((Dictionary<string, object?>)Tools())["tools"]!;
        return tools.Cast<Dictionary<string, object?>>()
            .Select(t => new Dictionary<string, object?> { ["name"] = t["name"], ["description"] = t["description"] })
            .ToList();
    }

    // ---------------------------------------------------------------- 协议面

    private object Initialize(JsonElement args)
    {
        var asked = args.TryGetProperty("protocolVersion", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return new Dictionary<string, object?>
        {
            ["protocolVersion"] = asked is not null && KnownProtocol.Contains(asked) ? asked : Protocol,
            ["capabilities"] = new Dictionary<string, object?>
            {
                ["tools"] = new Dictionary<string, object?> { ["listChanged"] = false }
            },
            ["serverInfo"] = new Dictionary<string, object?> { ["name"] = ServerName, ["version"] = ServerVersion }
        };
    }

    private object Tools()
    {
        var rule = Rule();
        return new Dictionary<string, object?>
        {
            ["tools"] = new List<object>
            {
                Tool("video_list",
                    "按条件找影片，返回卡片级信息（番号、中文片名、原名、发行日期、片商、已有标签、演员、片源状态）。" +
                    "noTags=true 用来捞还没打过标的片。",
                    Props(
                        ("keyword", S("在片名、原名、番号里模糊匹配")),
                        ("country", S("地区精确匹配，如 日本")),
                        ("category", S("分类精确匹配，如 av")),
                        ("tag", S("按标签浏览：标签正名、别名或 id 都认")),
                        ("studio", S("按片商浏览：片商正名、别名或 id 都认")),
                        ("subtitle", Enum("字幕状态", Utils.SourceStates.Subtitle)),
                        ("watermark", Enum("广告水印", Utils.SourceStates.Watermark)),
                        ("resolution", Enum("分辨率档", Utils.SourceStates.Resolutions.Keys.ToArray())),
                        ("noTags", B("true 时只要一个标签都没挂的片")),
                        ("limit", N($"每页条数，默认 {DefaultLimit}，最多 {MaxLimit}")),
                        ("offset", N("跳过条数，配合 limit 翻页"))),
                    required: []),

                Tool("video_get",
                    "看一部片的完整档案：档案字段、片商（含角色）、标签（含谁打的）、演员（含出生日期）、合辑与外链。",
                    Props(
                        ("id", S("影片 id")),
                        ("code", S("番号，如 ABC-123；与 id 二选一"))),
                    required: []),

                Tool("tag_list",
                    "题材标签词表，按挂载影片数倒序。打标前先读这份清单，按意思去对——" +
                    "片名说的特征只要有对应概念就用它的正名，不要求字面相同。",
                    Props(
                        ("keyword", S("在标签正名与别名里模糊匹配")),
                        ("limit", N($"每页条数，默认 {DefaultLimit}，最多 {MaxLimit}")),
                        ("offset", N("跳过条数"))),
                    required: []),

                Tool("tag_propose",
                    "给一部片提交标签，数量不限（推不出特征就一个都不提）。词表里已有的（正名或别名都算）直接挂上；" +
                    "没收录的词进待审队列等人工批准——这个接口建不出新标签，所以放心提。" +
                    "响应里每条带 similar：词表里与这个词最像的几个正名，非空时先考虑改用它，别放个近义词进队列。" +
                    "note 要写清依据，审核的人要看。" +
                    $"\n当前口径：{rule}",
                    Props(
                        ("videoId", S("影片 id（video_list / video_get 里的那个 id）")),
                        ("names", StrArray("标签词，一次最多 20 个")),
                        ("note", S("为什么提这些词，例如「取自中文片名」"))),
                    required: ["videoId", "names"]),

                Tool("studio_list",
                    "片商词表，含别名、国家、官网与名下影片数。",
                    Props(
                        ("keyword", S("在片商正名与别名里模糊匹配")),
                        ("limit", N($"每页条数，默认 {DefaultLimit}，最多 {MaxLimit}")),
                        ("offset", N("跳过条数"))),
                    required: []),

                Tool("vocab_counts",
                    "库里各表的规模与待办：影片总数、还没标签的有几部、词表多大、待审候选堆了多少。开工前先看一眼。",
                    Props(),
                    required: [])
            }
        };
    }

    private Outcome Call(JsonElement? id, JsonElement args)
    {
        var name = Text(args, "name");
        if (string.IsNullOrEmpty(name)) return Error(id, -32602, "tools/call 缺少 name");

        var input = args.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object ? a : NoArgs;

        object? data;
        try
        {
            data = name switch
            {
                "video_list" => VideoList(input),
                "video_get" => VideoGet(input),
                "tag_list" => TagList(input),
                "tag_propose" => TagPropose(input),
                "studio_list" => StudioList(input),
                "vocab_counts" => VocabCounts(),
                _ => throw new UnknownTool(name!)
            };
        }
        catch (UnknownTool ex)
        {
            return Error(id, -32602, $"没有这个工具：{ex.Name}");
        }
        catch (ToolException ex)
        {
            // 参数不对、片子不存在这类"调用方能自救"的失败按协议当工具内错误回，
            // 不报 JSON-RPC 错误——SDK 会把 JSON-RPC 错误当成服务端故障
            return new Outcome(200, Reply(id, ToolResult(new Dictionary<string, object?> { ["error"] = ex.Message }, true)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MCP tools/call {Name} failed", name);
            return new Outcome(200, Reply(id, ToolResult(new Dictionary<string, object?> { ["error"] = Utils.Api.InternalErrorMessage }, true)));
        }

        return new Outcome(200, Reply(id, ToolResult(data, false)));
    }

    private sealed class UnknownTool(string name) : Exception(name)
    {
        public string Name { get; } = name;
    }

    /// <summary>工具内部的失败：变成 isError=true 的结果，让模型读得懂并自己改参数重试</summary>
    private sealed class ToolException(string message) : Exception(message);

    private static object ToolResult(object data, bool isError) => new Dictionary<string, object?>
    {
        ["content"] = new List<object>
        {
            new Dictionary<string, object?> { ["type"] = "text", ["text"] = JsonSerializer.Serialize(data, Out) }
        },
        ["isError"] = isError
    };

    // ---------------------------------------------------------------- 工具实现

    private object VocabCounts()
    {
        using var conn = Open();
        return new Dictionary<string, object?>
        {
            ["videos"] = Scalar(conn, "SELECT COUNT(*) FROM videos"),
            ["videosWithoutTag"] = Scalar(conn, "SELECT COUNT(*) FROM videos v WHERE NOT EXISTS (SELECT 1 FROM video_tags vt WHERE vt.video_id = v.id)"),
            ["videosJapanAv"] = Scalar(conn, "SELECT COUNT(*) FROM videos WHERE country = '日本' AND category = 'av'"),
            ["tags"] = Scalar(conn, "SELECT COUNT(*) FROM tags"),
            ["studios"] = Scalar(conn, "SELECT COUNT(*) FROM studios"),
            ["pendingSuggestions"] = Scalar(conn, "SELECT COUNT(*) FROM tag_suggestions WHERE status = 'pending'")
        };
    }

    private object VideoList(JsonElement args)
    {
        var where = "WHERE 1=1";
        var ps = new List<SqliteParameter>();

        var keyword = Arg(args, "keyword");
        if (keyword is not null)
        {
            where += " AND (v.name LIKE @kw OR v.code LIKE @kw OR IFNULL(v.original_name,'') LIKE @kw)";
            ps.Add(new SqliteParameter("@kw", $"%{keyword}%"));
        }

        var country = Arg(args, "country");
        if (country is not null)
        {
            where += " AND v.country = @country";
            ps.Add(new SqliteParameter("@country", country));
        }

        var category = Arg(args, "category");
        if (category is not null)
        {
            where += " AND v.category = @category";
            ps.Add(new SqliteParameter("@category", category));
        }

        if (Flag(args, "noTags") == true)
            where += " AND NOT EXISTS (SELECT 1 FROM video_tags x WHERE x.video_id = v.id)";

        var (limit, offset) = Paging(args);

        using var conn = Open();

        // 标签与片商允许用名字筛：模型手上只有名字，硬要它先查一轮 id 只是白费往返
        var tagId = Resolve(conn, "tag", Arg(args, "tag"));
        var studioId = Resolve(conn, "studio", Arg(args, "studio"));

        VideoCardQuery.AppendCommonFilters(ref where, ps, new VideoCardQuery.SourceFilter
        {
            Subtitle = Arg(args, "subtitle"),
            Watermark = Arg(args, "watermark"),
            Resolution = Arg(args, "resolution"),
            TagId = tagId,
            StudioId = studioId
        });

        var total = Scalar(conn, $"SELECT COUNT(*) FROM videos v {where}", ps);

        var sql = $@"
            SELECT {VideoCardQuery.ColumnsWithSeries}, v.original_name, v.release_date
            FROM videos v
            LEFT JOIN video_series s ON v.seriesid = s.id
            {where}
            ORDER BY v.ctime DESC, v.id ASC
            LIMIT @ps OFFSET @off";

        var rows = new List<Dictionary<string, object?>>();
        using (var cmd = new SqliteCommand(sql, conn))
        {
            foreach (var p in ps) cmd.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
            cmd.Parameters.AddWithValue("@ps", limit);
            cmd.Parameters.AddWithValue("@off", offset);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) rows.Add(VideoCardQuery.Map(reader));
        }

        Attach(conn, rows, withActorDetail: false);
        return Page(total, limit, offset, rows);
    }

    private object VideoGet(JsonElement args)
    {
        var id = Arg(args, "id");
        var code = Arg(args, "code");
        if (id is null && code is null) throw new ToolException("id 与 code 至少要给一个");

        using var conn = Open();
        if (id is null)
        {
            using var find = new SqliteCommand("SELECT id FROM videos WHERE UPPER(code) = UPPER(@c) LIMIT 1", conn);
            find.Parameters.AddWithValue("@c", code!);
            id = find.ExecuteScalar() as string ?? throw new ToolException($"词表里没有番号「{code}」");
        }

        var video = new Dictionary<string, object?>();
        using (var cmd = new SqliteCommand(
            $@"SELECT {VideoCardQuery.ColumnsWithSeries}, v.original_name, v.release_date, v.scan_time
               FROM videos v LEFT JOIN video_series s ON v.seriesid = s.id WHERE v.id = @id", conn))
        {
            cmd.Parameters.AddWithValue("@id", id);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) throw new ToolException($"影片不存在：{code ?? id}");
            video = VideoCardQuery.Map(reader);
        }

        Attach(conn, [video], withActorDetail: true);
        video["groups"] = VideoMeta.Groups(conn, id);
        video["links"] = VideoMeta.Links(conn, id);

        return video;
    }

    private object TagList(JsonElement args)
    {
        var (limit, offset) = Paging(args);
        var where = "WHERE 1=1";
        var ps = new List<SqliteParameter>();
        var keyword = Arg(args, "keyword");
        if (keyword is not null)
        {
            where += " AND (t.name LIKE @kw OR EXISTS (SELECT 1 FROM tag_aliases a WHERE a.tag_id = t.id AND a.alias LIKE @kw))";
            ps.Add(new SqliteParameter("@kw", $"%{keyword}%"));
        }

        const string sql = @"
            SELECT t.id, t.name,
                   IFNULL((SELECT COUNT(*) FROM video_tags vt WHERE vt.tag_id = t.id), 0) AS video_count,
                   IFNULL((SELECT GROUP_CONCAT(a.alias, char(31)) FROM tag_aliases a WHERE a.tag_id = t.id), '') AS alias_blob
            FROM tags t";

        using var conn = Open();
        var total = Scalar(conn, $"SELECT COUNT(*) FROM tags t {where}", ps);

        var list = new List<object>();
        using (var cmd = new SqliteCommand($"{sql} {where} ORDER BY video_count DESC, t.name ASC LIMIT @ps OFFSET @off", conn))
        {
            foreach (var p in ps) cmd.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
            cmd.Parameters.AddWithValue("@ps", limit);
            cmd.Parameters.AddWithValue("@off", offset);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                list.Add(new Dictionary<string, object?>
                {
                    ["id"] = reader.GetString(0),
                    ["name"] = reader.GetString(1),
                    ["videoCount"] = reader.GetInt32(2),
                    ["aliases"] = Blob(reader.GetString(3))
                });
        }

        return new Dictionary<string, object?>
        {
            ["total"] = total,
            ["limit"] = limit,
            ["offset"] = offset,
            ["note"] = "只有这里没出现过的词才需要走 tag_propose 的待审路径",
            ["items"] = list
        };
    }

    private object StudioList(JsonElement args)
    {
        var (limit, offset) = Paging(args);
        var where = "WHERE 1=1";
        var ps = new List<SqliteParameter>();
        var keyword = Arg(args, "keyword");
        if (keyword is not null)
        {
            where += " AND (s.name LIKE @kw OR EXISTS (SELECT 1 FROM studio_aliases a WHERE a.studio_id = s.id AND a.alias LIKE @kw))";
            ps.Add(new SqliteParameter("@kw", $"%{keyword}%"));
        }

        const string sql = @"
            SELECT s.id, s.name, s.country, s.link,
                   IFNULL((SELECT COUNT(*) FROM video_studios vs WHERE vs.studio_id = s.id), 0) AS video_count,
                   IFNULL((SELECT GROUP_CONCAT(a.alias, char(31)) FROM studio_aliases a WHERE a.studio_id = s.id), '') AS alias_blob
            FROM studios s";

        using var conn = Open();
        var total = Scalar(conn, $"SELECT COUNT(*) FROM studios s {where}", ps);

        var list = new List<object>();
        using (var cmd = new SqliteCommand($"{sql} {where} ORDER BY video_count DESC, s.name ASC LIMIT @ps OFFSET @off", conn))
        {
            foreach (var p in ps) cmd.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
            cmd.Parameters.AddWithValue("@ps", limit);
            cmd.Parameters.AddWithValue("@off", offset);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                list.Add(new Dictionary<string, object?>
                {
                    ["id"] = reader.GetString(0),
                    ["name"] = reader.GetString(1),
                    ["country"] = reader.IsDBNull(2) ? null : reader.GetString(2),
                    ["link"] = reader.IsDBNull(3) ? null : reader.GetString(3),
                    ["videoCount"] = reader.GetInt32(4),
                    ["aliases"] = Blob(reader.GetString(5))
                });
        }

        return Page(total, limit, offset, list);
    }

    /// <summary>
    /// AI 打标唯一的写入口。词表里有的直接挂，没有的进待审——
    /// 这里刻意不写 tags 表的 INSERT，所以再怎么调用都扩不了词表。
    /// </summary>
    private object TagPropose(JsonElement args)
    {
        var videoId = Arg(args, "videoId") ?? throw new ToolException("videoId 不能为空");
        if (!args.TryGetProperty("names", out var names) || names.ValueKind != JsonValueKind.Array)
            throw new ToolException("names 必须是字符串数组");

        var words = names.EnumerateArray()
            .Where(n => n.ValueKind == JsonValueKind.String)
            .Select(n => n.GetString()!.Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (words.Count == 0) throw new ToolException("names 里一个词都没有");
        if (words.Count > 20) throw new ToolException("一次最多提 20 个词");

        var note = Arg(args, "note") ?? "AI 提交";

        using var conn = Open();
        string title;
        using (var check = new SqliteCommand("SELECT name FROM videos WHERE id = @id", conn))
        {
            check.Parameters.AddWithValue("@id", videoId);
            title = check.ExecuteScalar() as string ?? throw new ToolException($"影片不存在：{videoId}");
        }

        var vocab = LoadVocab(conn);

        return new Dictionary<string, object?>
        {
            ["videoId"] = videoId,
            ["videoName"] = title,
            ["results"] = words.Select(w => ProposeOne(conn, videoId, w, note, vocab)).ToList()
        };
    }

    /// <summary>
    /// 词表索引：一条 = (报出来的正名, 用来比对的字符集)。别名也进索引，但报的仍是它所属标签的正名——
    /// 提示模型归一，而不是又添一个词。词表就几百条，全量拉进内存比每个候选词问一次库便宜。
    /// </summary>
    private static List<(string Name, HashSet<char> Chars)> LoadVocab(SqliteConnection conn)
    {
        var list = new List<(string, HashSet<char>)>();

        using (var cmd = new SqliteCommand("SELECT name FROM tags", conn))
        {
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.GetString(0);
                list.Add((name, name.ToHashSet()));
            }
        }

        using (var cmd = new SqliteCommand(
            @"SELECT t.name, a.alias FROM tag_aliases a JOIN tags t ON t.id = a.tag_id", conn))
        {
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var alias = reader.GetString(1);
                list.Add((reader.GetString(0), alias.ToHashSet()));
            }
        }

        return list;
    }

    /// <summary>
    /// 词表里与候选词最像的几个：按共享字占比（重叠系数）排，阈值 0.5。
    /// 只当提示，不自动挂——"爆乳算不算巨乳"是语义判断，接口不替人拍板。
    /// </summary>
    private static List<string> Similar(string word, List<(string Name, HashSet<char> Chars)> vocab)
    {
        var chars = word.ToHashSet();
        if (chars.Count == 0) return [];

        return vocab
            .Where(v => v.Chars.Count > 0)
            .Select(v => (v.Name, Score: (double)chars.Count(v.Chars.Contains) / Math.Min(chars.Count, v.Chars.Count)))
            .Where(x => x.Score >= 0.5)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Name.Length)
            .Select(x => x.Name)
            .Distinct(StringComparer.Ordinal)
            .Take(3)
            .ToList();
    }

    private Dictionary<string, object?> ProposeOne(
        SqliteConnection conn, string videoId, string word, string note, List<(string Name, HashSet<char> Chars)> vocab)
    {
        var known = VideoMeta.FindTag(conn, word);
        if (known is not null)
        {
            using var attach = new SqliteCommand(
                @"INSERT OR IGNORE INTO video_tags (video_id, tag_id, source, ctime) VALUES (@v, @t, 'ai', @time); SELECT changes();", conn);
            attach.Parameters.AddWithValue("@v", videoId);
            attach.Parameters.AddWithValue("@t", known);
            attach.Parameters.AddWithValue("@time", VideoMeta.Now());
            var changed = Convert.ToInt32(attach.ExecuteScalar());
            return Verdict(word, changed == 0 ? "already" : "attached", known, "词表里已经有，直接挂上了");
        }

        // 认不出不等于该新建：把词表里的近义项回给模型，让它自己改用它，
        // 而不是把「爆乳」再塞进队列等人工合并
        var near = Similar(word, vocab);

        using (var dup = new SqliteCommand(
            @"SELECT id FROM tag_suggestions WHERE video_id = @v AND name = @n AND status = 'pending' LIMIT 1", conn))
        {
            dup.Parameters.AddWithValue("@v", videoId);
            dup.Parameters.AddWithValue("@n", word);
            if (dup.ExecuteScalar() is not null)
                return Verdict(word, "queued", null, "这条候选已经在待审队列里", near);
        }

        using (var ins = new SqliteCommand(
            @"INSERT INTO tag_suggestions (video_id, tag_id, name, note, status, created_at)
              VALUES (@v, NULL, @n, @note, 'pending', @t); SELECT last_insert_rowid();", conn))
        {
            ins.Parameters.AddWithValue("@v", videoId);
            ins.Parameters.AddWithValue("@n", word);
            ins.Parameters.AddWithValue("@note", note);
            ins.Parameters.AddWithValue("@t", VideoMeta.Now());
            var id = Convert.ToInt32(ins.ExecuteScalar());
            return Verdict(word, "queued", null,
                near.Count > 0
                    ? $"新词，已进待审队列 #{id}。词表里有近义项，若说的是一回事请改用正名重新提交"
                    : $"新词，已进待审队列 #{id}，等人批准或并入",
                near);
        }
    }

    private static Dictionary<string, object?> Verdict(
        string word, string status, string? tagId, string message, List<string>? similar = null) => new()
    {
        ["name"] = word,
        ["status"] = status,
        ["tagId"] = tagId,
        ["message"] = message,
        ["similar"] = similar ?? []
    };

    // ---------------------------------------------------------------- 批量取关联

    /// <summary>
    /// 给一页影片补标签 / 片商 / 演员：整页三次批量查询，而不是每行三次单查——
    /// 模型一次要 50 部，N+1 会变成一百五十个来回。
    /// </summary>
    private static void Attach(SqliteConnection conn, List<Dictionary<string, object?>> rows, bool withActorDetail)
    {
        foreach (var r in rows)
        {
            r["tags"] = new List<object>();
            r["studios"] = new List<object>();
            r["actors"] = new List<object>();
            // 分辨率按短边报，与前端的 resolutionText 同一个口径，省得模型自己换算
            var w = Convert.ToInt32(r.GetValueOrDefault("resW") ?? 0);
            var h = Convert.ToInt32(r.GetValueOrDefault("resH") ?? 0);
            r["resLabel"] = h > 0 ? $"{(w > 0 ? Math.Min(w, h) : h)}p" : null;
        }

        var ids = rows.Select(r => r.GetValueOrDefault("id") as string).Where(id => id is not null).Cast<string>().ToList();
        if (ids.Count == 0) return;

        var byId = rows.Where(r => r.GetValueOrDefault("id") is string).ToDictionary(r => (string)r["id"]!, r => r);
        var inList = string.Join(",", ids.Select((_, i) => $"@v{i}"));

        Batch(conn, $@"SELECT vt.video_id, t.name, vt.source FROM video_tags vt JOIN tags t ON t.id = vt.tag_id
                       WHERE vt.video_id IN ({inList})", ids, r => Push(byId, r.GetString(0), "tags",
                           new Dictionary<string, object?> { ["name"] = r.GetString(1), ["source"] = r.IsDBNull(2) ? "manual" : r.GetString(2) }));

        Batch(conn, $@"SELECT vs.video_id, s.name, vs.role FROM video_studios vs JOIN studios s ON s.id = vs.studio_id
                       WHERE vs.video_id IN ({inList})", ids, r => Push(byId, r.GetString(0), "studios",
                           new Dictionary<string, object?> { ["name"] = r.GetString(1), ["role"] = r.IsDBNull(2) ? "" : r.GetString(2) }));

        // 列表页不需要演员 id 和生日，用两个空列把两套 SELECT 对齐成同一个 reader
        var cols = withActorDetail ? "a.id, a.birthdate" : "'' , ''";
        Batch(conn, $@"SELECT va.video_id, a.name, {cols} FROM actors a JOIN video_actors va ON a.id = va.actor_id
                       WHERE va.video_id IN ({inList}) ORDER BY a.name", ids, r => Push(byId, r.GetString(0), "actors",
                           new Dictionary<string, object?>
                           {
                               ["name"] = r.GetString(1),
                               // 库里九成演员还没生日，NULL 直接 GetString 会炸
                               ["id"] = Cell(r, 2),
                               ["birthdate"] = Cell(r, 3)
                           }));
    }

    private static void Push(Dictionary<string, Dictionary<string, object?>> byId, string videoId, string key, object item)
    {
        if (byId[videoId][key] as List<object> is { } list) list.Add(item);
    }

    private static void Batch(SqliteConnection conn, string sql, List<string> ids, Action<SqliteDataReader> read)
    {
        using var cmd = new SqliteCommand(sql, conn);
        for (var i = 0; i < ids.Count; i++) cmd.Parameters.AddWithValue($"@v{i}", ids[i]);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) read(reader);
    }

    // ---------------------------------------------------------------- 取数小工具

    private SqliteConnection Open()
    {
        var conn = _db.GetConnection();
        conn.Open();
        return conn;
    }

    private static string? Setting(SqliteConnection conn, string name)
    {
        using var cmd = new SqliteCommand("SELECT content FROM system_settings WHERE name = @n", conn);
        cmd.Parameters.AddWithValue("@n", name);
        var raw = cmd.ExecuteScalar() as string;
        return string.IsNullOrWhiteSpace(raw) ? null : raw;
    }

    private static int Scalar(SqliteConnection conn, string sql, List<SqliteParameter>? ps = null)
    {
        using var cmd = new SqliteCommand(sql, conn);
        if (ps is not null)
            foreach (var p in ps) cmd.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>
    /// 把"名字或 id"解析成 id：id、正名、别名三种写法都认。认不出就直接报错——
    /// 模型拼错词表名时，一条写清楚的错误比"筛出 0 条"有用得多。
    /// </summary>
    private static string? Resolve(SqliteConnection conn, string kind, string? value)
    {
        if (value is null) return null;

        var (table, aliasTable, aliasColumn) = kind switch
        {
            "tag" => ("tags", "tag_aliases", "tag_id"),
            _ => ("studios", "studio_aliases", "studio_id")
        };
        var label = kind == "tag" ? "标签" : "片商";

        var probes = new[]
        {
            $"SELECT id FROM {table} WHERE id = @v",
            $"SELECT id FROM {table} WHERE name = @v",
            $"SELECT {aliasColumn} FROM {aliasTable} WHERE alias = @v"
        };

        foreach (var sql in probes)
        {
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@v", value);
            if (cmd.ExecuteScalar() is string hit) return hit;
        }

        throw new ToolException($"词表里没有这个{label}「{value}」，先查一次词表再用正名");
    }

    private static Dictionary<string, object?> Page(int total, int limit, int offset, object items) => new()
    {
        ["total"] = total,
        ["limit"] = limit,
        ["offset"] = offset,
        ["items"] = items
    };

    // ---------------------------------------------------------------- 参数取值

    /// <summary>空白字符串一律当"没传"，省得每个筛选条件多判一次 IsNullOrWhiteSpace</summary>
    private static string? Arg(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!.Trim()
            : null;

    private static bool? Flag(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(v.GetString(), out var b) && b,
            _ => null
        };
    }

    private static (int Limit, int Offset) Paging(JsonElement e)
    {
        var limit = Num(e, "limit") ?? DefaultLimit;
        var offset = Num(e, "offset") ?? 0;
        return (Math.Clamp(limit, 1, MaxLimit), Math.Max(offset, 0));
    }

    private static int? Num(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    /// <summary>可空的文本列：NULL 和空串都归成 null，别拿 GetString 去撞 NULL</summary>
    private static string? Cell(SqliteDataReader r, int ordinal)
        => r.IsDBNull(ordinal) ? null : (r.GetString(ordinal) is { Length: > 0 } s ? s : null);

    private static List<string> Blob(string raw) => string.IsNullOrEmpty(raw)
        ? []
        : raw.Split((char)31, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    // ---------------------------------------------------------------- JSON-RPC 信封

    private static string? Text(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement? Id(JsonElement req)
        => req.TryGetProperty("id", out var id) && id.ValueKind is JsonValueKind.Number or JsonValueKind.String ? id : null;

    private static Dictionary<string, object?> Reply(JsonElement? id, object result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id is null ? null : Unwrap(id.Value),
        ["result"] = result
    };

    private static Outcome Error(JsonElement? id, int code, string message) => new(200, new Dictionary<string, object?>
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id is null ? null : Unwrap(id.Value),
        ["error"] = new Dictionary<string, object?> { ["code"] = code, ["message"] = message }
    });

    /// <summary>id 是 number 或 string 两种，原样回传，别统一成字符串</summary>
    private static object? Unwrap(JsonElement id) => id.ValueKind switch
    {
        JsonValueKind.Number => id.TryGetInt64(out var l) ? l : id.GetDouble(),
        JsonValueKind.String => id.GetString(),
        _ => null
    };

    // ---------------------------------------------------------------- schema 速记

    private static Dictionary<string, object?> Tool(
        string name, string description, Dictionary<string, object?> properties, string[] required) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required.ToList()
        }
    };

    private static Dictionary<string, object?> Props(params (string Name, Dictionary<string, object?> Schema)[] items)
    {
        var d = new Dictionary<string, object?>();
        foreach (var (name, schema) in items) d[name] = schema;
        return d;
    }

    private static Dictionary<string, object?> S(string description) => new() { ["type"] = "string", ["description"] = description };

    private static Dictionary<string, object?> N(string description) => new() { ["type"] = "integer", ["description"] = description };

    private static Dictionary<string, object?> B(string description) => new() { ["type"] = "boolean", ["description"] = description };

    private static Dictionary<string, object?> Enum(string description, string[] values) => new()
    {
        ["type"] = "string",
        ["description"] = description,
        ["enum"] = values.ToList()
    };

    private static Dictionary<string, object?> StrArray(string itemDescription) => new()
    {
        ["type"] = "array",
        ["description"] = "字符串数组",
        ["maxItems"] = 20,
        ["items"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = itemDescription }
    };
}

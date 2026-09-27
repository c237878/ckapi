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
/// 全站仍是局域网免鉴权，只有这里收一道：整站接口是"人看片"用的，让外部程序读库得另配一把钥匙。
/// 密钥存在 system_settings.mcpApiKey，由设置页生成；那行是空的就等于没开门。
///
/// 只给读与查：这个面上没有任何"改一部片"的写入口，AI 看得懂全库、也改不动全库。
/// </summary>
public sealed class McpService
{
    /// <summary>密钥所在的设置行；空值等于 MCP 未启用</summary>
    public const string KeySetting = "mcpApiKey";



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
        return new Dictionary<string, object?>
        {
            ["tools"] = new List<object>
            {
                Tool("video_list",
                    "按条件找影片，返回卡片级信息（番号、中文片名、原名、发行日期、片商、演员、片源状态）。" +
                    "缺什么字段就用它自己筛：noStudio 捞没填片商的，unscanned 捞还没量过分辨率的。",
                    Props(
                        ("keyword", S("在片名、原名、番号里模糊匹配")),
                        ("country", S("地区精确匹配，如 日本")),
                        ("category", S("分类精确匹配，如 av")),
                        ("studio", S("按片商浏览：片商正名、别名或 id 都认")),
                        ("subtitle", Enum("字幕状态", Utils.SourceStates.Subtitle)),
                        ("watermark", Enum("广告水印", Utils.SourceStates.Watermark)),
                        ("resolution", Enum("分辨率档", Utils.SourceStates.Resolutions.Keys.ToArray())),
                        ("noStudio", B("true 时只要没填片商的片")),
                        ("unscanned", B("true 时只要还没量过分辨率的片")),
                        ("unrated", B("true 时只要字幕与广告水印两维都还没标过的片")),
                        ("limit", N($"每页条数，默认 {DefaultLimit}，最多 {MaxLimit}")),
                        ("offset", N("跳过条数，配合 limit 翻页"))),
                    required: []),

                Tool("video_get",
                    "看一部片的完整档案：档案字段、片商、演员（含出生日期）、外部档案链接。",
                    Props(
                        ("id", S("影片 id")),
                        ("code", S("番号，如 ABC-123；与 id 二选一"))),
                    required: []),

                Tool("studio_list",
                    "片商词表，含别名、国家、官网与名下影片数。",
                    Props(
                        ("keyword", S("在片商正名与别名里模糊匹配")),
                        ("limit", N($"每页条数，默认 {DefaultLimit}，最多 {MaxLimit}")),
                        ("offset", N("跳过条数"))),
                    required: []),

                Tool("vocab_counts",
                    "库里各表的规模与待办：影片总数、没填片商的有几部、两维没标的有几部、没量过分辨率的有几部。开工前先看一眼。",
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
            ["videosJapanAv"] = Scalar(conn, "SELECT COUNT(*) FROM videos WHERE country = '日本' AND category = 'av'"),
            ["withoutStudio"] = Scalar(conn, "SELECT COUNT(*) FROM videos WHERE studioid IS NULL OR studioid = ''"),
            ["unscanned"] = Scalar(conn, "SELECT COUNT(*) FROM videos WHERE res_h IS NULL OR res_h = 0"),
            ["unratedSource"] = Scalar(conn, $"SELECT COUNT(*) FROM videos v WHERE {Utils.SourceStates.Unrated}"),
            ["withoutReleaseDate"] = Scalar(conn, "SELECT COUNT(*) FROM videos WHERE release_date IS NULL OR release_date = ''"),
            ["withoutActors"] = Scalar(conn, "SELECT COUNT(*) FROM videos v WHERE NOT EXISTS (SELECT 1 FROM video_actors va WHERE va.video_id = v.id)"),
            ["studios"] = Scalar(conn, "SELECT COUNT(*) FROM studios"),
            ["actors"] = Scalar(conn, "SELECT COUNT(*) FROM actors"),
            ["actorsWithoutBirthdate"] = Scalar(conn, "SELECT COUNT(*) FROM actors WHERE birthdate IS NULL OR birthdate = ''")
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

        if (Flag(args, "noStudio") == true)
            where += " AND (v.studioid IS NULL OR v.studioid = '')";

        if (Flag(args, "unscanned") == true)
            where += " AND (v.res_h IS NULL OR v.res_h = 0)";

        // "两维都没标"就是"还没看过"的口径，与今日推荐用的是同一个谓词
        if (Flag(args, "unrated") == true)
            where += $" AND {Utils.SourceStates.Unrated}";

        var (limit, offset) = Paging(args);

        using var conn = Open();

        // 片商允许用名字筛：模型手上只有名字，硬要它先查一轮 id 只是白费往返
        var studioId = Resolve(conn, "studio", Arg(args, "studio"));

        VideoCardQuery.AppendCommonFilters(ref where, ps, new VideoCardQuery.SourceFilter
        {
            Subtitle = Arg(args, "subtitle"),
            Watermark = Arg(args, "watermark"),
            Resolution = Arg(args, "resolution"),
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
        video["links"] = VideoMeta.Links(conn, id);

        return video;
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
                   (SELECT COUNT(*) FROM videos v WHERE v.studioid = s.id) AS video_count,
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
    /// 给一页影片补演员：整页一次批量查询，而不是每行单查——
    /// 模型一次要 50 部，N+1 会变成一百五十个来回。
    /// 片商与原名/发行不用补，它们是 videos 上的列，已经随那一条 SELECT 出来了。
    /// </summary>
    private static void Attach(SqliteConnection conn, List<Dictionary<string, object?>> rows, bool withActorDetail)
    {
        foreach (var r in rows)
        {
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

        // 列表页不需要演员 id 和生日，用两个空列把两套 SELECT 对齐成同一个 reader
        var cols = withActorDetail ? "a.id, a.birthdate" : "'', ''";
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

        var (table, aliasTable, aliasColumn) = ("studios", "studio_aliases", "studio_id");
        const string label = "片商";

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

}

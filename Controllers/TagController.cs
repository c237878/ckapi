using ckapi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using System.Text.Json.Serialization;

namespace ckapi.Controllers;

/// <summary>
/// 题材标签词表 + AI 候选队列。
///
/// 整套设计只服务一件事：**词表不能失控**。标签一多就出现"巨乳/爆乳/大胸"各管一摊，
/// 按标签浏览当场废掉，而这不可逆——合并永远比拆开省事。所以：
///   · 人工路径（界面上打字）允许直接建标签，他是词表的唯一权威；
///   · AI 路径只能从现有词表里选，归不到的词进 tag_suggestions 待审队列，
///     由他点「批准」（转正式标签）、「并入」（挂别名到已有标签）或「驳回」。
/// 口子收在库里而不是靠提示词自觉：提示词会被改，接口不会。
/// </summary>
[ApiController]
[Route("api/tag")]
public class TagController : ControllerBase
{
    private readonly ILogger<TagController> _logger;
    private readonly Utils.SQLiteHelper _db;

    public TagController(ILogger<TagController> logger, Utils.SQLiteHelper db)
    {
        _logger = logger;
        _db = db;
    }

    /// <summary>标签列表：带影片数与其中 AI 打的数，管理页与挑选器共用</summary>
    [HttpGet]
    public IActionResult GetTags([FromQuery] int page = 1, [FromQuery] int pageSize = 50,
        [FromQuery] string? keyword = null, [FromQuery] string? sortBy = null)
    {
        try
        {
            page = Utils.Paging.ClampPage(page);
            pageSize = Utils.Paging.ClampSize(pageSize);

            var where = "WHERE 1=1";
            var parameters = new List<SqliteParameter>();
            if (!string.IsNullOrWhiteSpace(keyword))
            {
                where += " AND (t.name LIKE @kw OR EXISTS (SELECT 1 FROM tag_aliases a WHERE a.tag_id = t.id AND a.alias LIKE @kw))";
                parameters.Add(new SqliteParameter("@kw", $"%{keyword.Trim()}%"));
            }

            var orderBy = sortBy?.ToLower() switch
            {
                "name" => "t.name ASC",
                _ => "video_count DESC, t.name ASC"
            };

            using var conn = _db.GetConnection();
            conn.Open();

            var total = Convert.ToInt32(Scalar(conn, $"SELECT COUNT(*) FROM tags t {where}", parameters));

            const string sql = @"
                SELECT t.id, t.name, t.ctime,
                       IFNULL((SELECT COUNT(*) FROM video_tags vt WHERE vt.tag_id = t.id), 0) AS video_count,
                       IFNULL((SELECT COUNT(*) FROM video_tags vt WHERE vt.tag_id = t.id AND vt.source = 'ai'), 0) AS ai_count,
                       IFNULL((SELECT GROUP_CONCAT(a.alias, char(31)) FROM tag_aliases a WHERE a.tag_id = t.id), '') AS alias_blob
                FROM tags t";
            var list = new List<object>();
            using (var cmd = new SqliteCommand($"{sql} {where} ORDER BY {orderBy} LIMIT @ps OFFSET @off", conn))
            {
                foreach (var p in parameters) cmd.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
                cmd.Parameters.AddWithValue("@ps", pageSize);
                cmd.Parameters.AddWithValue("@off", (page - 1) * pageSize);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    list.Add(new
                    {
                        id = reader.GetString(0),
                        name = reader.GetString(1),
                        ctime = reader.IsDBNull(2) ? null : reader.GetString(2),
                        videoCount = reader.GetInt32(3),
                        aiCount = reader.GetInt32(4),
                        aliases = Split(reader.GetString(5))
                    });
                }
            }

            return Ok(new { success = true, data = list, total, page, pageSize });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetTags failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>某个标签名下的影片（筛选与分页都在服务端做完再取页）</summary>
    [HttpGet("{id}/videos")]
    public IActionResult GetTagVideos(string id, [FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        [FromQuery] string? subtitle = null, [FromQuery] string? watermark = null,
        [FromQuery] string? resolution = null, [FromQuery] bool? hasFile = null)
    {
        try
        {
            page = Utils.Paging.ClampPage(page);
            pageSize = Utils.Paging.ClampSize(pageSize);
            using var conn = _db.GetConnection();
            conn.Open();

            var where = "WHERE vt.tag_id = @tagId";
            var parameters = new List<SqliteParameter> { new("@tagId", id) };
            VideoCardQuery.AppendCommonFilters(ref where, parameters, new VideoCardQuery.SourceFilter
            {
                Subtitle = subtitle, Watermark = watermark, Resolution = resolution, HasFile = hasFile
                // 这里不再传 TagId：本页的 WHERE 已经按标签收窄
            });

            var total = Convert.ToInt32(Scalar(conn,
                $"SELECT COUNT(*) FROM videos v JOIN video_tags vt ON vt.video_id = v.id {where}", parameters));

            var sql = $@"
                SELECT {VideoCardQuery.ColumnsWithSeries}
                FROM videos v
                JOIN video_tags vt ON vt.video_id = v.id
                LEFT JOIN video_series s ON v.seriesid = s.id
                {where}
                ORDER BY v.ctime DESC, v.id ASC
                LIMIT @ps OFFSET @off";

            var list = new List<Dictionary<string, object?>>();
            using (var cmd = new SqliteCommand(sql, conn))
            {
                foreach (var p in parameters) cmd.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
                cmd.Parameters.AddWithValue("@ps", pageSize);
                cmd.Parameters.AddWithValue("@off", (page - 1) * pageSize);
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) list.Add(VideoCardQuery.Map(reader));
            }
            return Ok(new { success = true, data = list, total, page, pageSize });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetTagVideos failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>
    /// 待审队列。默认只看 pending；带 videoId 时只看某部片的候选，
    /// 便于"这部片 AI 提了什么"就地处理。
    /// </summary>
    [HttpGet("suggestions")]
    public IActionResult GetSuggestions([FromQuery] string status = "pending", [FromQuery] string? videoId = null)
    {
        try
        {
            var state = status is "pending" or "approved" or "merged" or "rejected" ? status : "pending";
            var sql = @"
                SELECT sg.id, sg.video_id, IFNULL(sg.tag_id, ''), sg.name, IFNULL(sg.note, ''), sg.status,
                       IFNULL(sg.created_at, ''), IFNULL(v.code, ''), IFNULL(v.name, '')
                FROM tag_suggestions sg LEFT JOIN videos v ON v.id = sg.video_id";
            var where = "WHERE sg.status = @s";
            var parameters = new List<SqliteParameter> { new("@s", state) };
            if (!string.IsNullOrWhiteSpace(videoId))
            {
                where += " AND sg.video_id = @v";
                parameters.Add(new SqliteParameter("@v", videoId));
            }

            using var conn = _db.GetConnection();
            conn.Open();
            var list = new List<object>();
            using (var cmd = new SqliteCommand($"{sql} {where} ORDER BY sg.id DESC LIMIT 500", conn))
            {
                foreach (var p in parameters) cmd.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    list.Add(new
                    {
                        id = reader.GetInt32(0),
                        videoId = reader.GetString(1),
                        tagId = reader.GetString(2),
                        name = reader.GetString(3),
                        note = reader.GetString(4),
                        status = reader.GetString(5),
                        createdAt = reader.GetString(6),
                        videoCode = reader.GetString(7),
                        videoName = reader.GetString(8)
                    });
                }
            }
            return Ok(new { success = true, data = list });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetSuggestions failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>
    /// 提候选词（AI 唯一的建词路径）。同一部片同一个词只留一条待审，重复提交直接回已有那条。
    /// </summary>
    [HttpPost("suggestions")]
    public IActionResult Propose([FromBody] SuggestRequest req)
    {
        var name = (req.Name ?? "").Trim();
        if (name.Length == 0 || string.IsNullOrWhiteSpace(req.VideoId))
            return Ok(new { success = false, message = "videoId 与 name 都不能空" });

        try
        {
            using var conn = _db.GetConnection();
            conn.Open();

            using (var check = new SqliteCommand("SELECT name FROM videos WHERE id = @id", conn))
            {
                check.Parameters.AddWithValue("@id", req.VideoId);
                if (check.ExecuteScalar() is null) return NotFound(new { success = false, message = "影片不存在" });
            }

            // 词表里已经有这个正名或别名：那不是候选，直接挂上就行，由调用方改走打标签接口
            var known = VideoMeta.FindTag(conn, name);
            if (known is not null)
                return Ok(new { success = false, message = $"「{name}」已在词表里，直接用它打标即可", data = new { tagId = known } });

            var existing = new SqliteCommand(@"
                SELECT id FROM tag_suggestions WHERE video_id = @v AND name = @n AND status = 'pending' LIMIT 1", conn);
            existing.Parameters.AddWithValue("@v", req.VideoId);
            existing.Parameters.AddWithValue("@n", name);
            // SQLite 的 INTEGER 取回来是 long，用 is int 判会永远不成立，
            // 然后 INSERT 撞唯一索引变成 500——重复提交是常事，必须走这条返回。
            var dup = existing.ExecuteScalar();
            if (dup is not null)
                return Ok(new { success = true, message = "这条候选已经在队列里", data = new { id = Convert.ToInt32(dup) } });

            var cmd = new SqliteCommand(@"
                INSERT INTO tag_suggestions (video_id, tag_id, name, note, status, created_at)
                VALUES (@v, NULL, @n, @note, 'pending', @t); SELECT last_insert_rowid();", conn);
            cmd.Parameters.AddWithValue("@v", req.VideoId);
            cmd.Parameters.AddWithValue("@n", name);
            cmd.Parameters.AddWithValue("@note", (object?)(req.Note ?? "").Trim() ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@t", VideoMeta.Now());
            var id = Convert.ToInt32(cmd.ExecuteScalar());
            return Ok(new { success = true, message = "候选已入待审队列", data = new { id } });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Propose failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>
    /// 按候选词一次处理完：同一个词常常是几部片各提一条，逐条批准要点好几次，
    /// 而人的判断其实是"这个词要不要进词表"，跟它来自哪部片无关。
    ///
    /// action = approve（转正；没给 tagId 就用候选词新建）· merge（并入已有标签，候选词记成它的别名）
    /// · reject（全部驳回）。批准与并入会把**每一部**提过这个词的片都打上——
    /// 只挂第一部会留下"词表里有了、别的片还挂在待审"的半截状态。
    /// </summary>
    [HttpPost("suggestions/by-name")]
    public IActionResult ResolveByName([FromBody] ResolveRequest req)
    {
        var name = (req.Name ?? "").Trim();
        var action = (req.Action ?? "").Trim().ToLowerInvariant();
        if (name.Length == 0 || action is not ("approve" or "merge" or "reject"))
            return Ok(new { success = false, message = "name 与 action（approve / merge / reject）都要给" });
        if (action == "merge" && string.IsNullOrWhiteSpace(req.TagId))
            return Ok(new { success = false, message = "并入要指定并到哪个标签" });

        try
        {
            using var conn = _db.GetConnection();
            conn.Open();

            var videoIds = new List<string>();
            using (var cmd = new SqliteCommand(
                "SELECT video_id FROM tag_suggestions WHERE name = @n AND status = 'pending'", conn))
            {
                cmd.Parameters.AddWithValue("@n", name);
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) videoIds.Add(reader.GetString(0));
            }
            if (videoIds.Count == 0)
                return Ok(new { success = false, message = $"「{name}」已经不在待审队列里了" });

            if (action == "reject")
            {
                using var rej = new SqliteCommand(
                    "UPDATE tag_suggestions SET status = 'rejected' WHERE name = @n AND status = 'pending'", conn);
                rej.Parameters.AddWithValue("@n", name);
                var n = rej.ExecuteNonQuery();
                return Ok(new { success = true, message = $"已驳回「{name}」（{n} 条）", data = new { affected = n } });
            }

            string tagId;
            string tagName;
            if (string.IsNullOrWhiteSpace(req.TagId))
            {
                tagId = VideoMeta.EnsureTag(conn, null, name);
                tagName = name;
            }
            else
            {
                using var find = new SqliteCommand("SELECT name FROM tags WHERE id = @id", conn);
                find.Parameters.AddWithValue("@id", req.TagId);
                var found = find.ExecuteScalar() as string;
                if (found is null) return Ok(new { success = false, message = "目标标签不存在" });
                tagName = found;
                tagId = req.TagId!;
            }

            var merged = action == "merge";
            using (var tx = conn.BeginTransaction())
            {
                // 并入时候选词进别名表，下次 AI 再提这个词就直接归一到正名；
                // 但别把正名自己塞进自己的别名（演员那边同一规矩）
                if (merged && !string.Equals(tagName, name, StringComparison.OrdinalIgnoreCase))
                {
                    using var alias = new SqliteCommand(
                        "INSERT OR IGNORE INTO tag_aliases (tag_id, alias) VALUES (@t, @a)", conn, tx);
                    alias.Parameters.AddWithValue("@t", tagId);
                    alias.Parameters.AddWithValue("@a", name);
                    alias.ExecuteNonQuery();
                }

                using (var ins = new SqliteCommand(
                    @"INSERT OR IGNORE INTO video_tags (video_id, tag_id, source, ctime) VALUES (@v, @t, 'ai', @time)", conn, tx))
                {
                    // source 记 ai：这样"AI 打过还没人工复核"仍然筛得出来
                    foreach (var videoId in videoIds)
                    {
                        ins.Parameters.Clear();
                        ins.Parameters.AddWithValue("@v", videoId);
                        ins.Parameters.AddWithValue("@t", tagId);
                        ins.Parameters.AddWithValue("@time", VideoMeta.Now());
                        ins.ExecuteNonQuery();
                    }
                }

                using var upd = new SqliteCommand(
                    "UPDATE tag_suggestions SET status = @s, tag_id = @t WHERE name = @n AND status = 'pending'", conn, tx);
                upd.Parameters.AddWithValue("@s", merged ? "merged" : "approved");
                upd.Parameters.AddWithValue("@t", tagId);
                upd.Parameters.AddWithValue("@n", name);
                var done = upd.ExecuteNonQuery();
                tx.Commit();

                return Ok(new
                {
                    success = true,
                    message = merged
                        ? $"已并入「{tagName}」，{done} 部片都打上了；「{name}」记为它的别名"
                        : $"已批准「{tagName}」，{done} 部片都打上了",
                    data = new { tagId, affected = done }
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ResolveByName failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    [HttpPost("suggestions/{id}/reject")]
    public IActionResult Reject(int id)
    {
        try
        {
            using var conn = _db.GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand("UPDATE tag_suggestions SET status = 'rejected' WHERE id = @id", conn);
            cmd.Parameters.AddWithValue("@id", id);
            return cmd.ExecuteNonQuery() > 0
                ? Ok(new { success = true, message = "已驳回" })
                : Ok(new { success = false, message = "这条候选不在待审队列里了" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reject failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    [HttpPost]
    public IActionResult CreateTag([FromBody] TagRequest req)
    {
        var name = (req.Name ?? "").Trim();
        if (name.Length == 0) return Ok(new { success = false, message = "标签名不能为空" });

        try
        {
            using var conn = _db.GetConnection();
            conn.Open();
            var existing = VideoMeta.FindTag(conn, name);
            if (existing is not null)
                return Ok(new { success = false, message = $"「{name}」已经存在", data = new { id = existing } });

            var id = VideoMeta.EnsureTag(conn, null, name);
            WriteAliases(conn, id, req.Aliases, name);
            return Ok(new { success = true, message = "标签已创建", data = new { id, name } });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CreateTag failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    [HttpPut("{id}")]
    public IActionResult UpdateTag(string id, [FromBody] TagRequest req)
    {
        var name = (req.Name ?? "").Trim();
        if (name.Length == 0) return Ok(new { success = false, message = "标签名不能为空" });

        try
        {
            using var conn = _db.GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand("UPDATE tags SET name = @n WHERE id = @id", conn);
            cmd.Parameters.AddWithValue("@n", name);
            cmd.Parameters.AddWithValue("@id", id);
            if (cmd.ExecuteNonQuery() == 0) return NotFound(new { success = false, message = "标签不存在" });
            // 没带 aliases 就等于"这轮只改名"：WriteAliases 是整组替换，传空会连别名一起清掉
            if (req.Aliases is not null) WriteAliases(conn, id, req.Aliases, name);
            return Ok(new { success = true, message = "标签已更新", data = new { id, name } });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UpdateTag failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>删标签：挂接、别名、还没处理的候选词一起清（外键没开，漏一个就是幽灵行）</summary>
    [HttpDelete("{id}")]
    public IActionResult DeleteTag(string id)
    {
        try
        {
            using var conn = _db.GetConnection();
            conn.Open();
            using (var tx = conn.BeginTransaction())
            {
                VideoMeta.PurgeTag(conn, id, tx);
                using var del = new SqliteCommand("DELETE FROM tags WHERE id = @id", conn, tx);
                del.Parameters.AddWithValue("@id", id);
                var n = del.ExecuteNonQuery();
                tx.Commit();
                if (n == 0) return NotFound(new { success = false, message = "标签不存在" });
            }
            return Ok(new { success = true, message = "标签已删除（影片上的挂接一并摘掉）" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DeleteTag failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>两个标签合并：把 from 挂到 to 上，from 连同别名消失</summary>
    [HttpPost("{id}/merge")]
    public IActionResult Merge(string id, [FromBody] MergeRequest req)
    {
        if (string.IsNullOrWhiteSpace(req?.Into)) return Ok(new { success = false, message = "要说合并进哪个标签" });
        if (req.Into == id) return Ok(new { success = false, message = "不能和自己合并" });

        try
        {
            using var conn = _db.GetConnection();
            conn.Open();
            // 两个都得存在才敢动：IN 只查一行会让"其中一个不存在"也通过，然后静默合并半个
            using var check = new SqliteCommand("SELECT COUNT(*) FROM tags WHERE id IN (@a, @b)", conn);
            check.Parameters.AddWithValue("@a", id);
            check.Parameters.AddWithValue("@b", req.Into);
            if (Convert.ToInt32(check.ExecuteScalar()) < 2)
                return Ok(new { success = false, message = "两个标签里有一个不存在" });

            VideoMeta.MergeTag(conn, id, req.Into);
            return Ok(new { success = true, message = "已合并（挂接与别名都并过去了）" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Merge failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    // ---------------------------------------------------------------- 内部

    private static void WriteAliases(SqliteConnection conn, string tagId, List<string>? raw, string selfName)
    {
        using var tx = conn.BeginTransaction();
        using (var del = new SqliteCommand("DELETE FROM tag_aliases WHERE tag_id = @id", conn, tx))
        {
            del.Parameters.AddWithValue("@id", tagId);
            del.ExecuteNonQuery();
        }
        using var ins = new SqliteCommand(
            "INSERT OR IGNORE INTO tag_aliases (tag_id, alias) VALUES (@id, @alias)", conn, tx);
        foreach (var alias in Utils.Aliases.Normalize(raw, selfName))
        {
            ins.Parameters.Clear();
            ins.Parameters.AddWithValue("@id", tagId);
            ins.Parameters.AddWithValue("@alias", alias);
            ins.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static object? Scalar(SqliteConnection conn, string sql, List<SqliteParameter> parameters)
    {
        using var cmd = new SqliteCommand(sql, conn);
        foreach (var p in parameters) cmd.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
        return cmd.ExecuteScalar();
    }

    private static List<string> Split(string blob) =>
        string.IsNullOrEmpty(blob)
            ? new List<string>()
            : blob.Split((char)31, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public sealed class TagRequest
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("aliases")] public List<string>? Aliases { get; set; }
    }

    public sealed class SuggestRequest
    {
        [JsonPropertyName("videoId")] public string? VideoId { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("note")] public string? Note { get; set; }
    }

    public sealed class ResolveRequest
    {
        /// <summary>要处理的候选词（同名的一起处理）</summary>
        [JsonPropertyName("name")] public string? Name { get; set; }

        /// <summary>approve / merge / reject</summary>
        [JsonPropertyName("action")] public string? Action { get; set; }

        /// <summary>留空表示用候选词新建标签；给了就是挂到（或并入）这个已有标签</summary>
        [JsonPropertyName("tagId")] public string? TagId { get; set; }
    }

    public sealed class MergeRequest
    {
        [JsonPropertyName("into")] public string? Into { get; set; }
    }
}

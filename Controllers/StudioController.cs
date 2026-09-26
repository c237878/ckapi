using ckapi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using System.Text.Json.Serialization;

namespace ckapi.Controllers;

/// <summary>
/// 片商词表：一家片商是一个实体，有自己的别名与名下影片。
/// 之所以不存成 videos 上的一列文本——"麦当娜 / Madonna / マドンナ"是同一家，
/// 字符串列会让"按片商浏览"退化成模糊匹配（演员曾用名当年就是这个坑）。
/// </summary>
[ApiController]
[Route("api/studio")]
public class StudioController : ControllerBase
{
    private readonly ILogger<StudioController> _logger;
    private readonly Utils.SQLiteHelper _db;

    public StudioController(ILogger<StudioController> logger, Utils.SQLiteHelper db)
    {
        _logger = logger;
        _db = db;
    }

    /// <summary>片商列表：带名下影片数，管理页与详情页的挑选器共用</summary>
    [HttpGet]
    public IActionResult GetStudios([FromQuery] int page = 1, [FromQuery] int pageSize = 20,
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
                // 别名也算命中：输入"麦当娜"应该能找到正名"マドンナ"那家
                where += " AND (s.name LIKE @kw OR EXISTS (SELECT 1 FROM studio_aliases a WHERE a.studio_id = s.id AND a.alias LIKE @kw))";
                parameters.Add(new SqliteParameter("@kw", $"%{keyword.Trim()}%"));
            }

            var orderBy = sortBy?.ToLower() switch
            {
                "name" => "s.name ASC",
                _ => "video_count DESC, s.name ASC"
            };

            using var conn = _db.GetConnection();
            conn.Open();

            var total = Convert.ToInt32(Scalar(conn, $"SELECT COUNT(*) FROM studios s {where}", parameters));

            const string sql = @"
                SELECT s.id, s.name, s.country, s.link, s.ctime,
                       IFNULL((SELECT COUNT(*) FROM video_studios vs WHERE vs.studio_id = s.id), 0) AS video_count,
                       IFNULL((SELECT GROUP_CONCAT(a.alias, char(31)) FROM studio_aliases a WHERE a.studio_id = s.id), '') AS alias_blob
                FROM studios s";
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
                        country = reader.IsDBNull(2) ? null : reader.GetString(2),
                        link = reader.IsDBNull(3) ? null : reader.GetString(3),
                        ctime = reader.IsDBNull(4) ? null : reader.GetString(4),
                        videoCount = reader.GetInt32(5),
                        aliases = Split(reader.GetString(6))
                    });
                }
            }

            return Ok(new { success = true, data = list, total, page, pageSize });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetStudios failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>片商详情 + 名下影片</summary>
    [HttpGet("{id}")]
    public IActionResult GetStudio(string id)
    {
        try
        {
            using var conn = _db.GetConnection();
            conn.Open();

            string? name, country, link;
            using (var cmd = new SqliteCommand("SELECT name, country, link FROM studios WHERE id = @id", conn))
            {
                cmd.Parameters.AddWithValue("@id", id);
                using var reader = cmd.ExecuteReader();
                if (!reader.Read()) return NotFound(new { success = false, message = "片商不存在" });
                name = reader.GetString(0);
                country = reader.IsDBNull(1) ? null : reader.GetString(1);
                link = reader.IsDBNull(2) ? null : reader.GetString(2);
            }

            return Ok(new
            {
                success = true,
                data = new
                {
                    id,
                    name,
                    country,
                    link,
                    aliases = ReadAliases(conn, id)
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetStudio failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>某家片商名下的影片：筛选与分页都在服务端做完再取页</summary>
    [HttpGet("{id}/videos")]
    public IActionResult GetStudioVideos(string id, [FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        [FromQuery] string? subtitle = null, [FromQuery] string? watermark = null,
        [FromQuery] string? resolution = null, [FromQuery] bool? hasFile = null)
    {
        try
        {
            page = Utils.Paging.ClampPage(page);
            pageSize = Utils.Paging.ClampSize(pageSize);
            using var conn = _db.GetConnection();
            conn.Open();

            var where = "WHERE vs.studio_id = @studioId";
            var parameters = new List<SqliteParameter> { new("@studioId", id) };
            VideoCardQuery.AppendCommonFilters(ref where, parameters, new VideoCardQuery.SourceFilter
            {
                Subtitle = subtitle, Watermark = watermark, Resolution = resolution, HasFile = hasFile
                // 这里不再传 StudioId：本页的 WHERE 已经按片商收窄，重复绑定同名参数会直接报错
            });

            var total = Convert.ToInt32(Scalar(conn,
                $"SELECT COUNT(*) FROM videos v JOIN video_studios vs ON vs.video_id = v.id {where}", parameters));

            var sql = $@"
                SELECT {VideoCardQuery.ColumnsWithSeries}
                FROM videos v
                JOIN video_studios vs ON vs.video_id = v.id
                LEFT JOIN video_series s ON v.seriesid = s.id
                {where}
                ORDER BY v.ctime DESC, v.id ASC
                LIMIT @ps OFFSET @off";
            var list = ReadCards(conn, sql, parameters, pageSize, (page - 1) * pageSize);
            return Ok(new { success = true, data = list, total, page, pageSize });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetStudioVideos failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    [HttpPost]
    public IActionResult CreateStudio([FromBody] StudioRequest req)
    {
        var name = (req.Name ?? "").Trim();
        if (name.Length == 0) return Ok(new { success = false, message = "片商名不能为空" });

        try
        {
            using var conn = _db.GetConnection();
            conn.Open();

            var existing = VideoMeta.FindStudio(conn, name);
            if (existing is not null)
                return Ok(new { success = false, message = $"「{name}」已经存在（可能就是别名撞上了）", data = new { id = existing } });

            var now = VideoMeta.Now();
            var id = Guid.NewGuid().ToString("N").ToUpper();
            using (var cmd = new SqliteCommand(
                "INSERT INTO studios (id, name, country, link, ctime, utime) VALUES (@id, @n, @c, @l, @t, @t)", conn))
            {
                cmd.Parameters.AddWithValue("@id", id);
                cmd.Parameters.AddWithValue("@n", name);
                cmd.Parameters.AddWithValue("@c", (object?)req.Country ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@l", (object?)req.Link ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@t", now);
                cmd.ExecuteNonQuery();
            }
            WriteAliases(conn, id, req.Aliases, name);
            return Ok(new { success = true, message = "片商已创建", data = new { id, name } });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CreateStudio failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    [HttpPut("{id}")]
    public IActionResult UpdateStudio(string id, [FromBody] StudioRequest req)
    {
        var name = (req.Name ?? "").Trim();
        if (name.Length == 0) return Ok(new { success = false, message = "片商名不能为空" });

        try
        {
            using var conn = _db.GetConnection();
            conn.Open();

            using (var cmd = new SqliteCommand(
                @"UPDATE studios SET name = @n, country = @c, link = @l, utime = @t WHERE id = @id", conn))
            {
                cmd.Parameters.AddWithValue("@n", name);
                cmd.Parameters.AddWithValue("@c", (object?)req.Country ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@l", (object?)req.Link ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@t", VideoMeta.Now());
                cmd.Parameters.AddWithValue("@id", id);
                if (cmd.ExecuteNonQuery() == 0) return NotFound(new { success = false, message = "片商不存在" });
            }
            WriteAliases(conn, id, req.Aliases, name);
            return Ok(new { success = true, message = "片商已更新", data = new { id, name } });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UpdateStudio failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    [HttpDelete("{id}")]
    public IActionResult DeleteStudio(string id)
    {
        try
        {
            using var conn = _db.GetConnection();
            conn.Open();

            using (var tx = conn.BeginTransaction())
            {
                // 外键没开，挂接关系必须一起清，否则影片详情会列出点不动的空白片商
                VideoMeta.PurgeStudio(conn, id, tx);
                using var del = new SqliteCommand("DELETE FROM studios WHERE id = @id", conn, tx);
                del.Parameters.AddWithValue("@id", id);
                var n = del.ExecuteNonQuery();
                tx.Commit();
                if (n == 0) return NotFound(new { success = false, message = "片商不存在" });
            }
            return Ok(new { success = true, message = "片商已删除（影片上的挂接一并摘掉）" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DeleteStudio failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    // ---------------------------------------------------------------- 内部

    private static void WriteAliases(SqliteConnection conn, string studioId, List<string>? raw, string selfName)
    {
        using var tx = conn.BeginTransaction();
        using (var del = new SqliteCommand("DELETE FROM studio_aliases WHERE studio_id = @id", conn, tx))
        {
            del.Parameters.AddWithValue("@id", studioId);
            del.ExecuteNonQuery();
        }
        using var ins = new SqliteCommand(
            "INSERT OR IGNORE INTO studio_aliases (studio_id, alias) VALUES (@id, @alias)", conn, tx);
        // 清洗规则与演员曾用名共用 Utils/Aliases：去空去重、丢掉与正名相同的项、单项 ≤60 字
        foreach (var alias in Utils.Aliases.Normalize(raw, selfName))
        {
            ins.Parameters.Clear();
            ins.Parameters.AddWithValue("@id", studioId);
            ins.Parameters.AddWithValue("@alias", alias);
            ins.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static List<string> ReadAliases(SqliteConnection conn, string studioId)
    {
        var list = new List<string>();
        using var cmd = new SqliteCommand("SELECT alias FROM studio_aliases WHERE studio_id = @id ORDER BY alias", conn);
        cmd.Parameters.AddWithValue("@id", studioId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(reader.GetString(0));
        return list;
    }

    private static List<Dictionary<string, object?>> ReadCards(
        SqliteConnection conn, string sql, List<SqliteParameter> parameters, int pageSize, int offset)
    {
        var list = new List<Dictionary<string, object?>>();
        using var cmd = new SqliteCommand(sql, conn);
        foreach (var p in parameters) cmd.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
        cmd.Parameters.AddWithValue("@ps", pageSize);
        cmd.Parameters.AddWithValue("@off", offset);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(VideoCardQuery.Map(reader));
        return list;
    }

    private static object? Scalar(SqliteConnection conn, string sql, List<SqliteParameter> parameters)
    {
        using var cmd = new SqliteCommand(sql, conn);
        foreach (var p in parameters) cmd.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
        return cmd.ExecuteScalar();
    }

    private static List<string> Split(string blob) =>
        string.IsNullOrEmpty(blob) ? new List<string>() : blob.Split((char)31, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public sealed class StudioRequest
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("aliases")] public List<string>? Aliases { get; set; }
        [JsonPropertyName("country")] public string? Country { get; set; }
        [JsonPropertyName("link")] public string? Link { get; set; }
    }
}

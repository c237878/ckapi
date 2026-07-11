using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using System.Text.Json.Serialization;

namespace ckapi.Controllers;

/// <summary>
/// 演员相关接口
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class ActorController : ControllerBase
{
    private readonly ILogger<ActorController> _logger;
    private readonly IConfiguration _config;

    public ActorController(ILogger<ActorController> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    private SqliteConnection GetConnection()
    {
        return new SqliteConnection(_config.GetConnectionString("DefaultConnection"));
    }

    /// <summary>
    /// 获取演员列表
    /// </summary>
    [HttpGet]
    public IActionResult GetActors([FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] string? keyword = null, [FromQuery] string? country = null,
        [FromQuery] string? sortBy = null)
    {
        try
        {
            var offset = (page - 1) * pageSize;
            var whereClause = "WHERE 1=1";
            var parameters = new List<SqliteParameter>();

            // 排序
            var orderBy = sortBy?.ToLower() switch
            {
                "name" => "a.name ASC",
                "likecount" => "like_count DESC",
                "videocount" => "video_count DESC",
                _ => "like_count DESC"
            };

            if (!string.IsNullOrEmpty(keyword))
            {
                whereClause += " AND (name LIKE @keyword OR alias LIKE @keyword)";
                parameters.Add(new SqliteParameter("@keyword", $"%{keyword}%"));
            }

            if (!string.IsNullOrEmpty(country))
            {
                whereClause += " AND country = @country";
                parameters.Add(new SqliteParameter("@country", country));
            }

            using var conn = GetConnection();
            conn.Open();

            // 总数
            var countSql = $"SELECT COUNT(*) FROM actors {whereClause}";
            using (var countCmd = new SqliteCommand(countSql, conn))
            {
                foreach (var p in parameters) countCmd.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
                var total = Convert.ToInt32(countCmd.ExecuteScalar());

                // 列表 + video_count 子查询
                var sql = $@"
                    SELECT a.*, 
                        (SELECT COUNT(*) FROM video_actors va WHERE va.actor_id = a.id) as video_count,
                        (SELECT COUNT(*) FROM video_actors va2 
                         JOIN videos v ON va2.video_id = v.id 
                         JOIN video_likes vl ON v.id = vl.video_id 
                         WHERE va2.actor_id = a.id) as like_count
                    FROM actors a
                    {whereClause}
                    ORDER BY " + orderBy + @"
                    LIMIT @pageSize OFFSET @offset";

                using var cmd = new SqliteCommand(sql, conn);
                foreach (var p in parameters) cmd.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
                cmd.Parameters.Add(new SqliteParameter("@pageSize", pageSize));
                cmd.Parameters.Add(new SqliteParameter("@offset", offset));

                using var reader = cmd.ExecuteReader();
                var actors = new List<object>();
                while (reader.Read())
                {
                    actors.Add(new
                    {
                        id = reader["id"].ToString(),
                        name = reader["name"].ToString(),
                        alias = reader["alias"] == DBNull.Value ? null : reader["alias"].ToString(),
                        country = reader["country"] == DBNull.Value ? null : reader["country"].ToString(),
                        avatarPath = reader["avatar_path"] == DBNull.Value ? null : reader["avatar_path"].ToString(),
                        bio = reader["bio"] == DBNull.Value ? null : reader["bio"].ToString(),
                        videoCount = reader["video_count"] == DBNull.Value ? 0 : Convert.ToInt32(reader["video_count"]),
                        likeCount = reader["like_count"] == DBNull.Value ? 0 : Convert.ToInt32(reader["like_count"]),
                        addedAt = reader["ctime"]?.ToString()
                    });
                }

                return Ok(new { success = true, data = actors, total, page, pageSize });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取演员列表失败");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 获取演员详情
    /// </summary>
    [HttpGet("{id}")]
    public IActionResult GetActor(string id)
    {
        try
        {
            using var conn = GetConnection();
            conn.Open();

            var sql = @"SELECT a.*, 
                        (SELECT COUNT(*) FROM video_likes vl 
                         JOIN video_actors va ON vl.video_id = va.video_id 
                         WHERE va.actor_id = a.id) as like_count
                        FROM actors a WHERE a.id = @id";
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.Add(new SqliteParameter("@id", id));
            using var reader = cmd.ExecuteReader();

            if (!reader.Read())
                return NotFound(new { success = false, message = "演员不存在" });

            var actor = new
            {
                id = reader["id"].ToString(),
                name = reader["name"].ToString(),
                alias = reader["alias"] == DBNull.Value ? null : reader["alias"].ToString(),
                country = reader["country"] == DBNull.Value ? null : reader["country"].ToString(),
                avatarPath = reader["avatar_path"] == DBNull.Value ? null : reader["avatar_path"].ToString(),
                bio = reader["bio"] == DBNull.Value ? null : reader["bio"].ToString(),
                addedAt = reader["ctime"]?.ToString(),
                likeCount = reader["like_count"] == DBNull.Value ? 0 : Convert.ToInt32(reader["like_count"])
            };

            return Ok(new { success = true, data = actor });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取演员详情失败");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 添加演员
    /// </summary>
    [HttpPost]
    public IActionResult AddActor([FromBody] AddActorRequest request)
    {
        try
        {
            if (string.IsNullOrEmpty(request.Name))
                return Ok(new { success = false, message = "演员姓名不能为空" });

            var id = Guid.NewGuid().ToString("N").ToUpper();
            var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            using var conn = GetConnection();
            conn.Open();

            // 检查重名
            using (var checkCmd = new SqliteCommand("SELECT COUNT(*) FROM actors WHERE name = @name", conn))
            {
                checkCmd.Parameters.Add(new SqliteParameter("@name", request.Name));
                if (Convert.ToInt32(checkCmd.ExecuteScalar()) > 0)
                    return Ok(new { success = false, message = "演员已存在" });
            }

            var sql = @"INSERT INTO actors (id, name, alias, country, avatar_path, bio, ctime) VALUES (@id, @name, @alias, @country, @avatarPath, @bio, @addedAt)";
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.Add(new SqliteParameter("@id", id));
            cmd.Parameters.Add(new SqliteParameter("@name", request.Name));
            cmd.Parameters.Add(new SqliteParameter("@alias", (object?)request.Alias ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@country", (object?)request.Country ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@avatarPath", (object?)request.AvatarPath ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@bio", (object?)request.Bio ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@addedAt", now));
            cmd.ExecuteNonQuery();

            return Ok(new { success = true, data = new { id, name = request.Name }, message = "添加成功" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "添加演员失败");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 更新演员
    /// </summary>
    [HttpPut("{id}")]
    public IActionResult UpdateActor(string id, [FromBody] UpdateActorRequest request)
    {
        try
        {
            using var conn = GetConnection();
            conn.Open();

            var sql = @"UPDATE actors SET name = @name, alias = @alias, country = @country, avatar_path = @avatarPath, bio = @bio WHERE id = @id";
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.Add(new SqliteParameter("@id", id));
            cmd.Parameters.Add(new SqliteParameter("@name", request.Name ?? ""));
            cmd.Parameters.Add(new SqliteParameter("@alias", (object?)request.Alias ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@country", (object?)request.Country ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@avatarPath", (object?)request.AvatarPath ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@bio", (object?)request.Bio ?? DBNull.Value));

            if (cmd.ExecuteNonQuery() > 0)
                return Ok(new { success = true, message = "更新成功" });
            else
                return Ok(new { success = false, message = "演员不存在" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新演员失败");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 删除演员
    /// </summary>
    [HttpDelete("{id}")]
    public IActionResult DeleteActor(string id)
    {
        try
        {
            using var conn = GetConnection();
            conn.Open();

            // 删除关联
            using (var relCmd = new SqliteCommand("DELETE FROM video_actors WHERE actor_id = @actorId", conn))
            {
                relCmd.Parameters.Add(new SqliteParameter("@actorId", id));
                relCmd.ExecuteNonQuery();
            }

            // 删除演员
            using (var cmd = new SqliteCommand("DELETE FROM actors WHERE id = @id", conn))
            {
                cmd.Parameters.Add(new SqliteParameter("@id", id));
                if (cmd.ExecuteNonQuery() > 0)
                    return Ok(new { success = true, message = "删除成功" });
                else
                    return Ok(new { success = false, message = "演员不存在" });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除演员失败");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 获取演员的影片列表
    /// </summary>
    [HttpGet("{id}/videos")]
    public IActionResult GetActorVideos(string id, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        try
        {
            var offset = (page - 1) * pageSize;
            using var conn = GetConnection();
            conn.Open();

            var countSql = @"SELECT COUNT(*) FROM videos v INNER JOIN video_actors va ON v.id = va.video_id WHERE va.actor_id = @actorId";
            using (var countCmd = new SqliteCommand(countSql, conn))
            {
                countCmd.Parameters.Add(new SqliteParameter("@actorId", id));
                var total = Convert.ToInt32(countCmd.ExecuteScalar());

                var sql = @"
                    SELECT v.id, v.code, v.name, v.category, v.country, v.cover_path, v.file_path, v.file_size, v.seriesid, v.ctime,
                        (SELECT COUNT(*) FROM video_likes vl WHERE vl.video_id = v.id) as like_count,
                        s.name as series_name,
                        (SELECT GROUP_CONCAT(a.id || '|' || a.name) FROM actors a 
                         INNER JOIN video_actors va ON a.id = va.actor_id 
                         WHERE va.video_id = v.id) as actor_names
                    FROM videos v
                    INNER JOIN video_actors va ON v.id = va.video_id
                    LEFT JOIN video_series s ON v.seriesid = s.id
                    WHERE va.actor_id = @actorId
                    ORDER BY v.name ASC, v.code ASC
                    LIMIT @pageSize OFFSET @offset";
                using var cmd = new SqliteCommand(sql, conn);
                cmd.Parameters.Add(new SqliteParameter("@actorId", id));
                cmd.Parameters.Add(new SqliteParameter("@pageSize", pageSize));
                cmd.Parameters.Add(new SqliteParameter("@offset", offset));

                using var reader = cmd.ExecuteReader();
                var videos = new List<object>();
                while (reader.Read())
                {
                    videos.Add(new
                    {
                        id = reader["id"].ToString(),
                        code = reader["code"]?.ToString(),
                        name = reader["name"].ToString(),
                        category = reader["category"]?.ToString(),
                        country = reader["country"] == DBNull.Value ? "" : reader["country"].ToString(),
                        filePath = reader["file_path"]?.ToString(),
                        fileSize = reader["file_size"] == DBNull.Value ? 0 : Convert.ToInt64(reader["file_size"]),
                        coverPath = reader["cover_path"] == DBNull.Value ? null : reader["cover_path"].ToString(),
                        seriesId = reader["seriesid"]?.ToString(),
                        seriesName = reader["series_name"]?.ToString(),
                        likeCount = reader["like_count"] == DBNull.Value ? 0 : Convert.ToInt32(reader["like_count"]),
                        actorNames = reader["actor_names"]?.ToString(),
                        addedAt = reader["ctime"]?.ToString()
                    });
                }

                return Ok(new { success = true, data = videos, total, page, pageSize });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取演员影片失败");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }
    /// <summary>
    /// 获取演员海报列表
    /// </summary>
    [HttpGet("{id}/posters")]
    public IActionResult GetPosters(string id)
    {
        try
        {
            using var conn = GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand("SELECT content FROM system_settings WHERE name = 'posterDir'", conn);
            var posterDir = cmd.ExecuteScalar()?.ToString();

            if (string.IsNullOrEmpty(posterDir))
            {
                return Ok(new { success = true, data = new string[0], message = "未配置海报墙目录" });
            }

            var actorDir = Path.Combine(posterDir, id);
            if (!Directory.Exists(actorDir))
            {
                return Ok(new { success = true, data = new string[0], message = "该演员无海报" });
            }

            var files = Directory.GetFiles(actorDir, "*.*")
                .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
                .Select(f => Path.GetFileName(f))
                .ToList();

            return Ok(new { success = true, data = files });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取演员海报失败");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 获取演员海报图片
    /// </summary>
    [HttpGet("{id}/poster/{fileName}")]
    public IActionResult GetPoster(string id, string fileName)
    {
        try
        {
            using var conn = GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand("SELECT content FROM system_settings WHERE name = 'posterDir'", conn);
            var posterDir = cmd.ExecuteScalar()?.ToString();

            if (string.IsNullOrEmpty(posterDir))
            {
                return NotFound();
            }

            var filePath = Path.Combine(posterDir, id, fileName);
            if (!System.IO.File.Exists(filePath))
            {
                return NotFound();
            }

            var ext = Path.GetExtension(fileName).ToLower();
            var contentType = ext switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".webp" => "image/webp",
                _ => "application/octet-stream"
            };

            return PhysicalFile(filePath, contentType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取海报图片失败");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }
}

public class AddActorRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
    [JsonPropertyName("alias")]
    public string? Alias { get; set; }
    [JsonPropertyName("country")]
    public string? Country { get; set; }
    [JsonPropertyName("avatarPath")]
    public string? AvatarPath { get; set; }
    [JsonPropertyName("bio")]
    public string? Bio { get; set; }
}

public class UpdateActorRequest
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
    [JsonPropertyName("name")]
    public string? Name { get; set; }
    [JsonPropertyName("alias")]
    public string? Alias { get; set; }
    [JsonPropertyName("country")]
    public string? Country { get; set; }
    [JsonPropertyName("avatarPath")]
    public string? AvatarPath { get; set; }
    [JsonPropertyName("bio")]
    public string? Bio { get; set; }
}

using ckapi.Services;
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
            page = Utils.Paging.ClampPage(page);
            pageSize = Utils.Paging.ClampSize(pageSize);
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
            // 并列行按 id 收尾，保证翻页结果稳定
            orderBy += ", a.id ASC";

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
                         JOIN video_likes vl ON v.id = vl.video_id AND vl.target_type = 'video'
                         WHERE va2.actor_id = a.id) as like_count,
                        (SELECT COUNT(*) FROM video_actors va3 
                         JOIN videos v2 ON va3.video_id = v2.id 
                         WHERE va3.actor_id = a.id AND (v2.file_size IS NULL OR v2.file_size = 0)) as unloaded_count
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
                        bio = reader["bio"] == DBNull.Value ? null : reader["bio"].ToString(),
                        videoCount = reader["video_count"] == DBNull.Value ? 0 : Convert.ToInt32(reader["video_count"]),
                        likeCount = reader["like_count"] == DBNull.Value ? 0 : Convert.ToInt32(reader["like_count"]),
                        unloadedCount = reader["unloaded_count"] == DBNull.Value ? 0 : Convert.ToInt32(reader["unloaded_count"])
                    });
                }

                return Ok(new { success = true, data = actors, total, page, pageSize });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取演员列表失败");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
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
                bio = reader["bio"] == DBNull.Value ? null : reader["bio"].ToString(),
                likeCount = reader["like_count"] == DBNull.Value ? 0 : Convert.ToInt32(reader["like_count"])
            };

            return Ok(new { success = true, data = actor });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取演员详情失败");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
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

            var sql = @"INSERT INTO actors (id, name, alias, country, bio, ctime) VALUES (@id, @name, @alias, @country, @bio, @addedAt)";
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.Add(new SqliteParameter("@id", id));
            cmd.Parameters.Add(new SqliteParameter("@name", request.Name));
            cmd.Parameters.Add(new SqliteParameter("@alias", (object?)request.Alias ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@country", (object?)request.Country ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@bio", (object?)request.Bio ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@addedAt", now));
            cmd.ExecuteNonQuery();

            return Ok(new { success = true, data = new { id, name = request.Name }, message = "添加成功" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "添加演员失败");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
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

            var sql = @"UPDATE actors SET name = @name, alias = @alias, country = @country, bio = @bio WHERE id = @id";
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.Add(new SqliteParameter("@id", id));
            cmd.Parameters.Add(new SqliteParameter("@name", request.Name ?? ""));
            cmd.Parameters.Add(new SqliteParameter("@alias", (object?)request.Alias ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@country", (object?)request.Country ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@bio", (object?)request.Bio ?? DBNull.Value));

            if (cmd.ExecuteNonQuery() > 0)
                return Ok(new { success = true, message = "更新成功" });
            else
                return Ok(new { success = false, message = "演员不存在" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新演员失败");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
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
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>
    /// 获取所有现有地区列表（去重）
    /// </summary>
    [HttpGet("countries")]
    public IActionResult GetCountries()
    {
        try
        {
            using var conn = GetConnection();
            conn.Open();
            var sql = "SELECT DISTINCT country FROM actors WHERE country IS NOT NULL AND country != '' ORDER BY country";
            using var cmd = new SqliteCommand(sql, conn);
            var countries = new List<string>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                countries.Add(reader.GetString(0));
            }
            return Ok(new { success = true, data = countries });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取地区列表失败");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>
    /// 获取演员的影片列表
    /// </summary>
    [HttpGet("{id}/videos")]
    public IActionResult GetActorVideos(string id, [FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        [FromQuery] int? mediaAttrFlags = null, [FromQuery] bool? hasFile = null)
    {
        try
        {
            page = Utils.Paging.ClampPage(page);
            pageSize = Utils.Paging.ClampSize(pageSize);
            var offset = (page - 1) * pageSize;
            using var conn = GetConnection();
            conn.Open();

            // 片源/下载状态筛选下沉到 SQL：原先前端在已分页的结果里再过滤一次，
            // 只能筛到当前页，页数不同结果就不同。
            var where = "WHERE va.actor_id = @actorId";
            var parameters = new List<SqliteParameter> { new("@actorId", id) };
            VideoCardQuery.AppendCommonFilters(ref where, parameters, mediaAttrFlags, hasFile);

            var countSql = $@"SELECT COUNT(*) FROM videos v INNER JOIN video_actors va ON v.id = va.video_id {where}";
            using (var countCmd = new SqliteCommand(countSql, conn))
            {
                foreach (var p in parameters) countCmd.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
                var total = Convert.ToInt32(countCmd.ExecuteScalar());

                var sql = $@"
                    SELECT {VideoCardQuery.ColumnsWithSeries}
                    FROM videos v
                    INNER JOIN video_actors va ON v.id = va.video_id
                    LEFT JOIN video_series s ON v.seriesid = s.id
                    {where}
                    ORDER BY v.code ASC, v.id ASC
                    LIMIT @pageSize OFFSET @offset";
                using var cmd = new SqliteCommand(sql, conn);
                foreach (var p in parameters) cmd.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
                cmd.Parameters.Add(new SqliteParameter("@pageSize", pageSize));
                cmd.Parameters.Add(new SqliteParameter("@offset", offset));

                using var reader = cmd.ExecuteReader();
                var videos = new List<object>();
                while (reader.Read())
                {
                    videos.Add(VideoCardQuery.Map(reader));
                }

                return Ok(new { success = true, data = videos, total, page, pageSize });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取演员影片失败");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
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
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
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
            var safeFileName = Utils.SafePath.AsFileName(fileName);
            var safeId = Utils.SafePath.AsFileName(id);
            if (safeFileName is null || safeId is null || !Utils.SafePath.IsImageFile(safeFileName))
                return NotFound();

            using var conn = GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand("SELECT content FROM system_settings WHERE name = 'posterDir'", conn);
            var posterDir = cmd.ExecuteScalar()?.ToString();

            if (string.IsNullOrEmpty(posterDir))
            {
                return NotFound();
            }

            var filePath = Path.Combine(posterDir, safeId, safeFileName);
            if (!Utils.SafePath.IsInside(filePath, posterDir))
                return NotFound();

            var result = Utils.CachedFile.TryServe(this, filePath);
            if (result is not null) return result;

            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取海报图片失败");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
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
    [JsonPropertyName("bio")]
    public string? Bio { get; set; }
}

public class UpdateActorRequest
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
    [JsonPropertyName("alias")]
    public string? Alias { get; set; }
    [JsonPropertyName("country")]
    public string? Country { get; set; }
    [JsonPropertyName("bio")]
    public string? Bio { get; set; }
}

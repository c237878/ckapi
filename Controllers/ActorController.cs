using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

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
    public IActionResult GetActors([FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] string? keyword = null)
    {
        try
        {
            var offset = (page - 1) * pageSize;
            var whereClause = "WHERE 1=1";
            var parameters = new List<SqliteParameter>();

            if (!string.IsNullOrEmpty(keyword))
            {
                whereClause += " AND name LIKE @keyword";
                parameters.Add(new SqliteParameter("@keyword", $"%{keyword}%"));
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
                        (SELECT COUNT(*) FROM video_actors va WHERE va.actor_id = a.id) as video_count
                    FROM actors a
                    {whereClause}
                    ORDER BY a.name ASC
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
                        avatarPath = reader["avatar_path"] == DBNull.Value ? null : reader["avatar_path"].ToString(),
                        bio = reader["bio"] == DBNull.Value ? null : reader["bio"].ToString(),
                        videoCount = reader["video_count"] == DBNull.Value ? 0 : Convert.ToInt32(reader["video_count"]),
                        addedAt = reader["added_at"]?.ToString()
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

            var sql = "SELECT * FROM actors WHERE id = @id";
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.Add(new SqliteParameter("@id", id));
            using var reader = cmd.ExecuteReader();

            if (!reader.Read())
                return NotFound(new { success = false, message = "演员不存在" });

            var actor = new
            {
                id = reader["id"].ToString(),
                name = reader["name"].ToString(),
                avatarPath = reader["avatar_path"] == DBNull.Value ? null : reader["avatar_path"].ToString(),
                bio = reader["bio"] == DBNull.Value ? null : reader["bio"].ToString(),
                addedAt = reader["added_at"]?.ToString()
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

            var id = Guid.NewGuid().ToString();
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

            var sql = @"INSERT INTO actors (id, name, avatar_path, bio, added_at) VALUES (@id, @name, @avatarPath, @bio, @addedAt)";
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.Add(new SqliteParameter("@id", id));
            cmd.Parameters.Add(new SqliteParameter("@name", request.Name));
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

            var sql = @"UPDATE actors SET name = @name, avatar_path = @avatarPath, bio = @bio WHERE id = @id";
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.Add(new SqliteParameter("@id", id));
            cmd.Parameters.Add(new SqliteParameter("@name", request.Name ?? ""));
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
                    SELECT v.* FROM videos v
                    INNER JOIN video_actors va ON v.id = va.video_id
                    WHERE va.actor_id = @actorId
                    ORDER BY v.added_at DESC
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
                        name = reader["title"].ToString(),
                        code = reader["code"] == DBNull.Value ? null : reader["code"].ToString(),
                        category = reader["category"]?.ToString(),
                        year = reader["year"] == DBNull.Value ? (int?)null : Convert.ToInt32(reader["year"]),
                        filePath = reader["file_path"]?.ToString(),
                        fileSize = reader["file_size"] == DBNull.Value ? 0 : Convert.ToInt64(reader["file_size"]),
                        coverPath = reader["cover_path"] == DBNull.Value ? null : reader["cover_path"].ToString(),
                        addedAt = reader["added_at"]?.ToString()
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
}

public class AddActorRequest
{
    public string Name { get; set; } = "";
    public string? AvatarPath { get; set; }
    public string? Bio { get; set; }
}

public class UpdateActorRequest
{
    public string? Name { get; set; }
    public string? AvatarPath { get; set; }
    public string? Bio { get; set; }
}

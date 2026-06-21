using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace ckapi.Controllers;

/// <summary>
/// 视频类型管理控制器
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class VideoTypeController : ControllerBase
{
    private readonly IConfiguration _config;
    private readonly ILogger<VideoTypeController> _logger;

    public VideoTypeController(IConfiguration config, ILogger<VideoTypeController> logger)
    {
        _config = config;
        _logger = logger;
    }

    private SqliteConnection GetConnection()
    {
        return new SqliteConnection(_config.GetConnectionString("DefaultConnection"));
    }

    /// <summary>
    /// 获取所有视频类型
    /// </summary>
    [HttpGet]
    public IActionResult GetList()
    {
        try
        {
            using var conn = GetConnection();
            conn.Open();

            var sql = @"SELECT * FROM video_types ORDER BY sort_order, name";
            using var cmd = new SqliteCommand(sql, conn);
            using var reader = cmd.ExecuteReader();

            var list = new List<object>();
            while (reader.Read())
            {
                list.Add(ReadType(reader));
            }

            return Ok(new { success = true, data = list });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetList failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 获取单个视频类型
    /// </summary>
    [HttpGet("{id}")]
    public IActionResult GetById(string id)
    {
        try
        {
            using var conn = GetConnection();
            conn.Open();

            var sql = @"SELECT * FROM video_types WHERE id = @id";
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.Add(new SqliteParameter("@id", id));
            using var reader = cmd.ExecuteReader();

            if (!reader.Read())
                return NotFound(new { success = false, message = "视频类型不存在" });

            return Ok(new { success = true, data = ReadType(reader) });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetById failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 添加视频类型
    /// </summary>
    [HttpPost]
    public IActionResult AddType([FromBody] VideoTypeRequest req)
    {
        try
        {
            if (string.IsNullOrEmpty(req.Name))
                return Ok(new { success = false, message = "名称不能为空" });

            using var conn = GetConnection();
            conn.Open();

            var id = Guid.NewGuid().ToString("N").ToUpper();
            var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var sql = @"
                INSERT INTO video_types (id, name, extensions, sort_order, created_at, updated_at)
                VALUES (@id, @name, @extensions, @sortOrder, @createdAt, @updatedAt)";

            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.Add(new SqliteParameter("@id", id));
            cmd.Parameters.Add(new SqliteParameter("@name", req.Name));
            cmd.Parameters.Add(new SqliteParameter("@extensions", req.Extensions ?? ""));
            cmd.Parameters.Add(new SqliteParameter("@sortOrder", req.SortOrder));
            cmd.Parameters.Add(new SqliteParameter("@createdAt", now));
            cmd.Parameters.Add(new SqliteParameter("@updatedAt", now));
            cmd.ExecuteNonQuery();

            return Ok(new { success = true, data = new { id } });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AddType failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 更新视频类型
    /// </summary>
    [HttpPut("{id}")]
    public IActionResult UpdateType(string id, [FromBody] VideoTypeRequest req)
    {
        try
        {
            if (string.IsNullOrEmpty(req.Name))
                return Ok(new { success = false, message = "名称不能为空" });

            using var conn = GetConnection();
            conn.Open();

            var sql = @"
                UPDATE video_types SET
                    name = @name,
                    extensions = @extensions,
                    sort_order = @sortOrder,
                    updated_at = @updatedAt
                WHERE id = @id";

            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.Add(new SqliteParameter("@id", id));
            cmd.Parameters.Add(new SqliteParameter("@name", req.Name));
            cmd.Parameters.Add(new SqliteParameter("@extensions", req.Extensions ?? ""));
            cmd.Parameters.Add(new SqliteParameter("@sortOrder", req.SortOrder));
            cmd.Parameters.Add(new SqliteParameter("@updatedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));

            if (cmd.ExecuteNonQuery() > 0)
                return Ok(new { success = true, message = "更新成功" });
            else
                return Ok(new { success = false, message = "视频类型不存在" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UpdateType failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 删除视频类型
    /// </summary>
    [HttpDelete("{id}")]
    public IActionResult DeleteType(string id)
    {
        try
        {
            using var conn = GetConnection();
            conn.Open();

            using var cmd = new SqliteCommand("DELETE FROM video_types WHERE id = @id", conn);
            cmd.Parameters.Add(new SqliteParameter("@id", id));
            if (cmd.ExecuteNonQuery() > 0)
                return Ok(new { success = true, message = "删除成功" });
            else
                return Ok(new { success = false, message = "视频类型不存在" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DeleteType failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    private object ReadType(SqliteDataReader reader)
    {
        return new
        {
            id = reader["id"].ToString(),
            name = reader["name"].ToString(),
            extensions = reader["extensions"].ToString(),
            sortOrder = reader["sort_order"] == DBNull.Value ? 0 : Convert.ToInt32(reader["sort_order"]),
            createdAt = reader["created_at"]?.ToString(),
            updatedAt = reader["updated_at"]?.ToString()
        };
    }
}

public class VideoTypeRequest
{
    public string Name { get; set; } = "";
    public string? Extensions { get; set; } = "";
    public int SortOrder { get; set; } = 0;
}

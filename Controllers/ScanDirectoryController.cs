using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace ckapi.Controllers;

/// <summary>
/// 扫描目录管理控制器
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class ScanDirectoryController : ControllerBase
{
    private readonly IConfiguration _config;
    private readonly ILogger<ScanDirectoryController> _logger;

    public ScanDirectoryController(IConfiguration config, ILogger<ScanDirectoryController> logger)
    {
        _config = config;
        _logger = logger;
    }

    private SqliteConnection GetConnection()
    {
        return new SqliteConnection(_config.GetConnectionString("DefaultConnection"));
    }

    /// <summary>
    /// 获取所有扫描目录
    /// </summary>
    [HttpGet]
    public IActionResult GetList()
    {
        try
        {
            using var conn = GetConnection();
            conn.Open();

            var sql = @"SELECT * FROM scan_directories ORDER BY created_at DESC";
            using var cmd = new SqliteCommand(sql, conn);
            using var reader = cmd.ExecuteReader();

            var list = new List<object>();
            while (reader.Read())
            {
                list.Add(ReadDirectory(reader));
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
    /// 检查目录是否存在
    /// </summary>
    [HttpPost("check")]
    public IActionResult CheckDirectory([FromBody] DirectoryCheckRequest req)
    {
        try
        {
            var exists = Directory.Exists(req.Path);
            _logger.LogInformation("检查目录路径: {Path}, 存在: {Exists}", req.Path, exists);
            return Ok(new { success = true, exists });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CheckDirectory failed");
            return Ok(new { success = false, exists = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 获取单个扫描目录
    /// </summary>
    [HttpGet("{id}")]
    public IActionResult GetById(string id)
    {
        try
        {
            using var conn = GetConnection();
            conn.Open();

            var sql = @"SELECT * FROM scan_directories WHERE id = @id";
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.Add(new SqliteParameter("@id", id));
            using var reader = cmd.ExecuteReader();

            if (!reader.Read())
                return NotFound(new { success = false, message = "扫描目录不存在" });

            return Ok(new { success = true, data = ReadDirectory(reader) });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetById failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 添加扫描目录
    /// </summary>
    [HttpPost]
    public IActionResult AddDirectory([FromBody] ScanDirectoryRequest req)
    {
        try
        {
            if (string.IsNullOrEmpty(req.Path))
                return Ok(new { success = false, message = "路径不能为空" });

            if (!Directory.Exists(req.Path))
                return Ok(new { success = false, message = "路径不存在" });

            using var conn = GetConnection();
            conn.Open();

            // 检查同路径是否已存在
            using (var checkCmd = new SqliteCommand("SELECT COUNT(*) FROM scan_directories WHERE path = @path", conn))
            {
                checkCmd.Parameters.Add(new SqliteParameter("@path", req.Path));
                if (Convert.ToInt32(checkCmd.ExecuteScalar()) > 0)
                    return Ok(new { success = false, message = "该路径已存在" });
            }

            var id = Guid.NewGuid().ToString("N").ToUpper();
            var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var sql = @"
                INSERT INTO scan_directories (id, path, recursive, created_at, updated_at)
                VALUES (@id, @path, @recursive, @createdAt, @updatedAt)";

            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.Add(new SqliteParameter("@id", id));
            cmd.Parameters.Add(new SqliteParameter("@path", req.Path));
            cmd.Parameters.Add(new SqliteParameter("@recursive", req.Recursive ? 1 : 0));
            cmd.Parameters.Add(new SqliteParameter("@createdAt", now));
            cmd.Parameters.Add(new SqliteParameter("@updatedAt", now));
            cmd.ExecuteNonQuery();

            return Ok(new { success = true, data = new { id } });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AddDirectory failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 更新扫描目录
    /// </summary>
    [HttpPut("{id}")]
    public IActionResult UpdateDirectory(string id, [FromBody] ScanDirectoryRequest req)
    {
        try
        {
            if (string.IsNullOrEmpty(req.Path))
                return Ok(new { success = false, message = "路径不能为空" });

            using var conn = GetConnection();
            conn.Open();

            var sql = @"
                UPDATE scan_directories SET
                    path = @path,
                    recursive = @recursive,
                    updated_at = @updatedAt
                WHERE id = @id";

            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.Add(new SqliteParameter("@id", id));
            cmd.Parameters.Add(new SqliteParameter("@path", req.Path));
            cmd.Parameters.Add(new SqliteParameter("@recursive", req.Recursive ? 1 : 0));
            cmd.Parameters.Add(new SqliteParameter("@updatedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));

            if (cmd.ExecuteNonQuery() > 0)
                return Ok(new { success = true, message = "更新成功" });
            else
                return Ok(new { success = false, message = "扫描目录不存在" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UpdateDirectory failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 删除扫描目录
    /// </summary>
    [HttpDelete("{id}")]
    public IActionResult DeleteDirectory(string id)
    {
        try
        {
            using var conn = GetConnection();
            conn.Open();

            using var cmd = new SqliteCommand("DELETE FROM scan_directories WHERE id = @id", conn);
            cmd.Parameters.Add(new SqliteParameter("@id", id));
            if (cmd.ExecuteNonQuery() > 0)
                return Ok(new { success = true, message = "删除成功" });
            else
                return Ok(new { success = false, message = "扫描目录不存在" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DeleteDirectory failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    private object ReadDirectory(SqliteDataReader reader)
    {
        return new
        {
            id = reader["id"].ToString(),
            path = reader["path"].ToString(),
            recursive = Convert.ToInt32(reader["recursive"]) == 1,
            createdAt = reader["created_at"]?.ToString(),
            updatedAt = reader["updated_at"]?.ToString()
        };
    }
}

public class ScanDirectoryRequest
{
    public string Path { get; set; } = "";
    public bool Recursive { get; set; } = true;
}

public class DirectoryCheckRequest
{
    public string Path { get; set; } = "";
}

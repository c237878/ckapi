using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;

namespace ckapi.Controllers;

/// <summary>
/// Samba共享管理控制器
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class SambaController : ControllerBase
{
    private readonly IConfiguration _config;
    private readonly ILogger<SambaController> _logger;

    public SambaController(IConfiguration config, ILogger<SambaController> logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// 获取所有Samba共享
    /// </summary>
    [HttpGet("list")]
    public IActionResult GetList()
    {
        try
        {
            using var conn = new SqliteConnection(_config.GetConnectionString("DefaultConnection"));
            conn.Open();

            var sql = @"
                SELECT id, name, path, username, domain, is_enabled, created_at, updated_at
                FROM samba_shares
                ORDER BY created_at DESC";

            using var cmd = new SqliteCommand(sql, conn);
            using var reader = cmd.ExecuteReader();

            var list = new List<object>();
            while (reader.Read())
            {
                list.Add(new
                {
                    id = reader["id"].ToString(),
                    name = reader["name"].ToString(),
                    path = reader["path"].ToString(),
                    username = reader["username"]?.ToString() ?? "",
                    domain = reader["domain"]?.ToString() ?? "",
                    isEnabled = Convert.ToInt32(reader["is_enabled"]) == 1,
                    createdAt = reader["created_at"].ToString(),
                    updatedAt = reader["updated_at"].ToString()
                });
            }

            return Ok(new { success = true, data = list });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取Samba共享列表失败");
            return Ok(new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 根据ID获取Samba共享
    /// </summary>
    [HttpGet("{id}")]
    public IActionResult GetById(string id)
    {
        try
        {
            using var conn = new SqliteConnection(_config.GetConnectionString("DefaultConnection"));
            conn.Open();

            var sql = @"
                SELECT id, name, path, username, domain, is_enabled, created_at, updated_at
                FROM samba_shares WHERE id = @id";

            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@id", id);

            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return Ok(new
                {
                    success = true,
                    data = new
                    {
                        id = reader["id"].ToString(),
                        name = reader["name"].ToString(),
                        path = reader["path"].ToString(),
                        username = reader["username"]?.ToString() ?? "",
                        domain = reader["domain"]?.ToString() ?? "",
                        isEnabled = Convert.ToInt32(reader["is_enabled"]) == 1,
                        createdAt = reader["created_at"].ToString(),
                        updatedAt = reader["updated_at"].ToString()
                    }
                });
            }

            return Ok(new { success = false, message = "Samba共享不存在" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取Samba共享失败");
            return Ok(new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 添加Samba共享
    /// </summary>
    [HttpPost("add")]
    public IActionResult Add([FromBody] AddSambaDto dto)
    {
        try
        {
            if (string.IsNullOrEmpty(dto.Name) || string.IsNullOrEmpty(dto.Path))
            {
                return Ok(new { success = false, message = "名称和路径不能为空" });
            }

            var id = Guid.NewGuid().ToString();
            var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            using var conn = new SqliteConnection(_config.GetConnectionString("DefaultConnection"));
            conn.Open();

            // 检查路径是否已存在
            var checkSql = "SELECT COUNT(*) FROM samba_shares WHERE path = @path";
            using (var checkCmd = new SqliteCommand(checkSql, conn))
            {
                checkCmd.Parameters.AddWithValue("@path", dto.Path);
                if (Convert.ToInt32(checkCmd.ExecuteScalar()) > 0)
                {
                    return Ok(new { success = false, message = "该路径已存在" });
                }
            }

            var sql = @"
                INSERT INTO samba_shares (id, name, path, username, password, domain, is_enabled, created_at, updated_at)
                VALUES (@id, @name, @path, @username, @password, @domain, @isEnabled, @createdAt, @updatedAt)";

            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@name", dto.Name);
            cmd.Parameters.AddWithValue("@path", dto.Path);
            cmd.Parameters.AddWithValue("@username", dto.Username ?? "");
            cmd.Parameters.AddWithValue("@password", EncryptPassword(dto.Password ?? ""));
            cmd.Parameters.AddWithValue("@domain", dto.Domain ?? "");
            cmd.Parameters.AddWithValue("@isEnabled", dto.IsEnabled ? 1 : 0);
            cmd.Parameters.AddWithValue("@createdAt", now);
            cmd.Parameters.AddWithValue("@updatedAt", now);

            cmd.ExecuteNonQuery();

            return Ok(new { success = true, data = new { id, message = "添加成功" } });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "添加Samba共享失败");
            return Ok(new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 更新Samba共享
    /// </summary>
    [HttpPut("{id}")]
    public IActionResult Update(string id, [FromBody] UpdateSambaDto dto)
    {
        try
        {
            var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            using var conn = new SqliteConnection(_config.GetConnectionString("DefaultConnection"));
            conn.Open();

            // 检查ID是否存在
            var checkSql = "SELECT COUNT(*) FROM samba_shares WHERE id = @id";
            using (var checkCmd = new SqliteCommand(checkSql, conn))
            {
                checkCmd.Parameters.AddWithValue("@id", id);
                if (Convert.ToInt32(checkCmd.ExecuteScalar()) == 0)
                {
                    return Ok(new { success = false, message = "Samba共享不存在" });
                }
            }

            // 检查路径是否与其他记录冲突
            var pathCheckSql = "SELECT COUNT(*) FROM samba_shares WHERE path = @path AND id != @id";
            using (var pathCmd = new SqliteCommand(pathCheckSql, conn))
            {
                pathCmd.Parameters.AddWithValue("@path", dto.Path);
                pathCmd.Parameters.AddWithValue("@id", id);
                if (Convert.ToInt32(pathCmd.ExecuteScalar()) > 0)
                {
                    return Ok(new { success = false, message = "该路径已被其他共享使用" });
                }
            }

            var sql = @"
                UPDATE samba_shares 
                SET name = @name, path = @path, username = @username, 
                    domain = @domain, is_enabled = @isEnabled, updated_at = @updatedAt";

            // 如果提供了新密码，则更新
            if (!string.IsNullOrEmpty(dto.Password))
            {
                sql += ", password = @password";
            }

            sql += " WHERE id = @id";

            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@name", dto.Name);
            cmd.Parameters.AddWithValue("@path", dto.Path);
            cmd.Parameters.AddWithValue("@username", dto.Username ?? "");
            cmd.Parameters.AddWithValue("@domain", dto.Domain ?? "");
            cmd.Parameters.AddWithValue("@isEnabled", dto.IsEnabled ? 1 : 0);
            cmd.Parameters.AddWithValue("@updatedAt", now);

            if (!string.IsNullOrEmpty(dto.Password))
            {
                cmd.Parameters.AddWithValue("@password", EncryptPassword(dto.Password));
            }

            cmd.ExecuteNonQuery();

            return Ok(new { success = true, message = "更新成功" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新Samba共享失败");
            return Ok(new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 删除Samba共享
    /// </summary>
    [HttpDelete("{id}")]
    public IActionResult Delete(string id)
    {
        try
        {
            using var conn = new SqliteConnection(_config.GetConnectionString("DefaultConnection"));
            conn.Open();

            var sql = "DELETE FROM samba_shares WHERE id = @id";
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@id", id);

            var rows = cmd.ExecuteNonQuery();
            if (rows > 0)
            {
                return Ok(new { success = true, message = "删除成功" });
            }

            return Ok(new { success = false, message = "Samba共享不存在" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除Samba共享失败");
            return Ok(new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 简单加密密码（使用机器名作为盐值）
    /// </summary>
    private string EncryptPassword(string password)
    {
        if (string.IsNullOrEmpty(password)) return "";

        var salt = Environment.MachineName;
        var bytes = Encoding.UTF8.GetBytes(password + salt);
        var hash = SHA256.HashData(bytes);
        return Convert.ToBase64String(hash);
    }
}

public class AddSambaDto
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? Domain { get; set; }
    public bool IsEnabled { get; set; } = true;
}

public class UpdateSambaDto
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? Domain { get; set; }
    public bool IsEnabled { get; set; } = true;
}

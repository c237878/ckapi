using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using ckapi.Services;

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
    private readonly SambaService _sambaService;

    public SambaController(
        IConfiguration config,
        ILogger<SambaController> logger,
        SambaService sambaService)
    {
        _config = config;
        _logger = logger;
        _sambaService = sambaService;
    }

    /// <summary>
    /// 获取所有Samba共享配置列表
    /// </summary>
    [HttpGet("list")]
    public async Task<IActionResult> GetList()
    {
        try
        {
            // 读取数据库配置
            var dbShares = await GetDbSharesAsync();

            // 读取系统实际共享点
            var sysShares = await _sambaService.GetSharePointsAsync();

            // 合并数据：数据库记录 + 系统实际状态
            var result = new List<object>();
            foreach (var db in dbShares)
            {
                var sys = sysShares.FirstOrDefault(s =>
                    s.Name.Equals(db.Name, StringComparison.OrdinalIgnoreCase) ||
                    s.Path.Equals(db.Path, StringComparison.OrdinalIgnoreCase));

                result.Add(new
                {
                    id = db.Id,
                    name = db.Name,
                    path = db.Path,
                    isEnabled = db.IsEnabled,
                    smbShared = sys?.SMBShared ?? false,
                    guestAccess = sys?.GuestAccess ?? true,
                    readOnly = sys?.ReadOnly ?? false,
                    systemExists = sys != null,
                    createdAt = db.CreatedAt,
                    updatedAt = db.UpdatedAt
                });
            }

            return Ok(new { success = true, data = result });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取Samba共享列表失败");
            return Ok(new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 获取系统实际共享点列表（只读）
    /// </summary>
    [HttpGet("system-shares")]
    public async Task<IActionResult> GetSystemShares()
    {
        try
        {
            var shares = await _sambaService.GetSharePointsAsync();
            return Ok(new { success = true, data = shares });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取系统共享点失败");
            return Ok(new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 添加Samba共享（数据库记录 + 实际创建共享）
    /// </summary>
    [HttpPost("add")]
    public async Task<IActionResult> Add([FromBody] AddSambaDto dto)
    {
        try
        {
            if (string.IsNullOrEmpty(dto.Name) || string.IsNullOrEmpty(dto.Path))
            {
                return Ok(new { success = false, message = "名称和路径不能为空" });
            }

            // 创建系统共享
            var (sysOk, sysMsg, shareName) = await _sambaService.AddShareAsync(
                dto.Path, dto.Name,
                smbEnabled: dto.IsEnabled,
                guestAccess: dto.GuestAccess,
                readOnly: dto.ReadOnly);

            if (!sysOk)
            {
                return Ok(new { success = false, message = $"创建系统共享失败: {sysMsg}" });
            }

            // 保存到数据库
            var id = Guid.NewGuid().ToString();
            var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            using var conn = new SqliteConnection(_config.GetConnectionString("DefaultConnection"));
            conn.Open();

            var sql = @"
                INSERT INTO samba_shares (id, name, path, is_enabled, created_at, updated_at)
                VALUES (@id, @name, @path, @isEnabled, @createdAt, @updatedAt)";

            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@name", dto.Name);
            cmd.Parameters.AddWithValue("@path", dto.Path);
            cmd.Parameters.AddWithValue("@isEnabled", dto.IsEnabled ? 1 : 0);
            cmd.Parameters.AddWithValue("@createdAt", now);
            cmd.Parameters.AddWithValue("@updatedAt", now);

            cmd.ExecuteNonQuery();

            return Ok(new { success = true, data = new { id, name = dto.Name, path = dto.Path, message = "添加成功" } });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "添加Samba共享失败");
            return Ok(new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 更新Samba共享配置
    /// </summary>
    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] UpdateSambaDto dto)
    {
        try
        {
            using var conn = new SqliteConnection(_config.GetConnectionString("DefaultConnection"));
            conn.Open();

            // 查询当前记录
            var selectSql = "SELECT id, name, path, is_enabled FROM samba_shares WHERE id = @id";
            string shareName = "", sharePath = "";
            bool wasEnabled = false;

            using (var selectCmd = new SqliteCommand(selectSql, conn))
            {
                selectCmd.Parameters.AddWithValue("@id", id);
                using var reader = selectCmd.ExecuteReader();
                if (reader.Read())
                {
                    shareName = reader["name"].ToString();
                    sharePath = reader["path"].ToString();
                    wasEnabled = Convert.ToInt32(reader["is_enabled"]) == 1;
                }
                else
                {
                    return Ok(new { success = false, message = "Samba共享不存在" });
                }
            }

            // 更新系统共享配置
            var (sysOk, sysMsg) = await _sambaService.UpdateShareAsync(
                shareName,
                smbEnabled: dto.IsEnabled,
                guestAccess: dto.GuestAccess,
                readOnly: dto.ReadOnly);

            if (!sysOk)
            {
                _logger.LogWarning("更新系统共享配置失败: {msg}", sysMsg);
            }

            // 更新数据库
            var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var sql = @"
                UPDATE samba_shares 
                SET name = @name, path = @path, is_enabled = @isEnabled, updated_at = @updatedAt
                WHERE id = @id";

            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@name", dto.Name);
            cmd.Parameters.AddWithValue("@path", dto.Path);
            cmd.Parameters.AddWithValue("@isEnabled", dto.IsEnabled ? 1 : 0);
            cmd.Parameters.AddWithValue("@updatedAt", now);

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
    public async Task<IActionResult> Delete(string id)
    {
        try
        {
            using var conn = new SqliteConnection(_config.GetConnectionString("DefaultConnection"));
            conn.Open();

            // 查询记录
            var selectSql = "SELECT name, path FROM samba_shares WHERE id = @id";
            string shareName = "", sharePath = "";

            using (var selectCmd = new SqliteCommand(selectSql, conn))
            {
                selectCmd.Parameters.AddWithValue("@id", id);
                using var reader = selectCmd.ExecuteReader();
                if (reader.Read())
                {
                    shareName = reader["name"].ToString();
                    sharePath = reader["path"].ToString();
                }
            }

            if (string.IsNullOrEmpty(shareName))
            {
                return Ok(new { success = false, message = "Samba共享不存在" });
            }

            // 删除系统共享
            var (sysOk, sysMsg) = await _sambaService.RemoveShareAsync(shareName);
            if (!sysOk)
            {
                return Ok(new { success = false, message = $"删除系统共享失败: {sysMsg}" });
            }

            // 系统共享删除成功，再删除数据库记录
            var sql = "DELETE FROM samba_shares WHERE id = @id";
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();

            return Ok(new { success = true, message = "删除成功" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除Samba共享失败");
            return Ok(new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 测试SMB连接
    /// </summary>
    [HttpPost("test-connection")]
    public async Task<IActionResult> TestConnection([FromBody] TestConnectionDto dto)
    {
        var (success, message) = await _sambaService.TestConnectionAsync(dto.Host);
        return Ok(new { success, message });
    }

    // ========== 私有方法 ==========

    private async Task<List<DbShareRecord>> GetDbSharesAsync()
    {
        var result = new List<DbShareRecord>();
        using var conn = new SqliteConnection(_config.GetConnectionString("DefaultConnection"));
        await conn.OpenAsync();

        var sql = "SELECT * FROM samba_shares ORDER BY created_at DESC";
        using var cmd = new SqliteCommand(sql, conn);
        using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            result.Add(new DbShareRecord
            {
                Id = reader["id"].ToString(),
                Name = reader["name"].ToString(),
                Path = reader["path"].ToString(),
                IsEnabled = Convert.ToInt32(reader["is_enabled"]) == 1,
                CreatedAt = reader["created_at"].ToString(),
                UpdatedAt = reader["updated_at"].ToString()
            });
        }

        return result;
    }

    /// <summary>
    /// 导入系统现有的Samba共享到数据库
    /// </summary>
    [HttpPost("import-system")]
    public async Task<IActionResult> ImportSystemShares()
    {
        try
        {
            var sysShares = await _sambaService.GetSharePointsAsync();
            using var conn = new SqliteConnection(_config.GetConnectionString("DefaultConnection"));
            await conn.OpenAsync();

            int imported = 0;
            foreach (var share in sysShares)
            {
                // 检查是否已在数据库中
                var checkSql = "SELECT COUNT(*) FROM samba_shares WHERE name = @name OR path = @path";
                using var checkCmd = new SqliteCommand(checkSql, conn);
                checkCmd.Parameters.AddWithValue("@name", share.Name);
                checkCmd.Parameters.AddWithValue("@path", share.Path);
                var exists = Convert.ToInt32(await checkCmd.ExecuteScalarAsync()) > 0;

                if (!exists)
                {
                    var id = Guid.NewGuid().ToString();
                    var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    var sql = @"INSERT INTO samba_shares (id, name, path, is_enabled, created_at, updated_at)
                                   VALUES (@id, @name, @path, 1, @createdAt, @updatedAt)";
                    using var cmd = new SqliteCommand(sql, conn);
                    cmd.Parameters.AddWithValue("@id", id);
                    cmd.Parameters.AddWithValue("@name", share.Name);
                    cmd.Parameters.AddWithValue("@path", share.Path);
                    cmd.Parameters.AddWithValue("@createdAt", now);
                    cmd.Parameters.AddWithValue("@updatedAt", now);
                    await cmd.ExecuteNonQueryAsync();
                    imported++;
                }
            }

            return Ok(new { success = true, data = new { imported }, message = $"成功导入 {imported} 个共享" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "导入系统共享失败");
            return Ok(new { success = false, message = ex.Message });
        }
    }
}

public class DbShareRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool IsEnabled { get; set; }
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}

public class AddSambaDto
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
    public bool GuestAccess { get; set; } = true;
    public bool ReadOnly { get; set; } = false;
}

public class UpdateSambaDto
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
    public bool GuestAccess { get; set; } = true;
    public bool ReadOnly { get; set; } = false;
}

public class TestConnectionDto
{
    public string Host { get; set; } = "";
}
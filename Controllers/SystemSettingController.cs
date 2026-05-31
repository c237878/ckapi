using Microsoft.AspNetCore.Mvc;
using ckapi.Models;
using ckapi.Utils;
using Microsoft.Data.Sqlite;

namespace ckapi.Controllers;

/// <summary>
/// 系统设置控制器
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class SystemSettingController : ControllerBase
{
    private readonly SQLiteHelper _db;
    private readonly ILogger<SystemSettingController> _logger;

    public SystemSettingController(SQLiteHelper db, ILogger<SystemSettingController> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// 获取所有设置
    /// </summary>
    [HttpGet]
    public IActionResult GetList()
    {
        try
        {
            var dt = _db.ExecuteDataTable("SELECT * FROM SystemSetting ORDER BY id");
            var list = new List<Dictionary<string, object?>>();
            foreach (System.Data.DataRow row in dt.Rows)
            {
                list.Add(new Dictionary<string, object?>
                {
                    ["id"] = row["id"]?.ToString(),
                    ["name"] = row["name"]?.ToString(),
                    ["content"] = row["content"]?.ToString(),
                    ["ctime"] = row["ctime"]?.ToString(),
                    ["utime"] = row["utime"]?.ToString()
                });
            }
            return Ok(new { success = true, data = list });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取系统设置失败");
            return Ok(new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 获取单个设置
    /// </summary>
    [HttpGet("{name}")]
    public IActionResult Get(string name)
    {
        try
        {
            var result = _db.ExecuteScalar(
                "SELECT content FROM SystemSetting WHERE name = @name",
                new SqliteParameter("@name", name));

            if (result == null || result == DBNull.Value)
            {
                return Ok(new { success = false, message = "设置不存在" });
            }

            return Ok(new { success = true, data = result.ToString() });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取设置失败: {Name}", name);
            return Ok(new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 添加或更新设置
    /// </summary>
    [HttpPost]
    public IActionResult Save([FromBody] Dictionary<string, object> data)
    {
        try
        {
            var name = data["name"].ToString();
            var content = data["content"].ToString();
            var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            // 检查是否存在
            var exists = _db.ExecuteScalar(
                "SELECT COUNT(*) FROM SystemSetting WHERE name = @name",
                new SqliteParameter("@name", name));

            if (Convert.ToInt32(exists) > 0)
            {
                // 更新
                _db.ExecuteNonQuery(
                    "UPDATE SystemSetting SET content = @content, utime = @utime WHERE name = @name",
                    new SqliteParameter("@content", content),
                    new SqliteParameter("@utime", now),
                    new SqliteParameter("@name", name));
                _logger.LogInformation("更新设置: {Name} = {Content}", name, content);
            }
            else
            {
                // 新增 - 生成GUID作为id
                var id = Guid.NewGuid().ToString();
                _db.ExecuteNonQuery(
                    "INSERT INTO SystemSetting (id, name, content, ctime, utime) VALUES (@id, @name, @content, @ctime, @utime)",
                    new SqliteParameter("@id", id),
                    new SqliteParameter("@name", name),
                    new SqliteParameter("@content", content),
                    new SqliteParameter("@ctime", now),
                    new SqliteParameter("@utime", now));
                _logger.LogInformation("添加设置: {Name} = {Content}", name, content);
            }

            return Ok(new { success = true, message = "保存成功" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存设置失败");
            return Ok(new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 删除设置
    /// </summary>
    [HttpDelete("{id}")]
    public IActionResult Delete(string id)
    {
        try
        {
            var rows = _db.ExecuteNonQuery(
                "DELETE FROM SystemSetting WHERE id = @id",
                new SqliteParameter("@id", id));

            if (rows > 0)
            {
                _logger.LogInformation("删除设置: {Id}", id);
                return Ok(new { success = true, message = "删除成功" });
            }
            return Ok(new { success = false, message = "设置不存在" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除设置失败: {Id}", id);
            return Ok(new { success = false, message = ex.Message });
        }
    }
}

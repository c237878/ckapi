using Microsoft.AspNetCore.Mvc;
using ckapi.Models;
using ckapi.Utils;
using Microsoft.Data.Sqlite;

namespace ckapi.Controllers;

/// <summary>
/// 友情链接控制器
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class FriendLinkController : ControllerBase
{
    private readonly SQLiteHelper _db;
    private readonly ILogger<FriendLinkController> _logger;

    public FriendLinkController(SQLiteHelper db, ILogger<FriendLinkController> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// 获取友情链接列表
    /// </summary>
    [HttpGet]
    public IActionResult GetList()
    {
        try
        {
            var dt = _db.ExecuteDataTable("SELECT * FROM FriendLink ORDER BY sortorder, id");
            var list = new List<Dictionary<string, object?>>();
            foreach (System.Data.DataRow row in dt.Rows)
            {
                list.Add(new Dictionary<string, object?>
                {
                    ["id"] = row["id"]?.ToString(),
                    ["name"] = row["name"]?.ToString(),
                    ["link"] = row["link"]?.ToString(),
                    ["logo"] = row["logo"]?.ToString(),
                    ["description"] = row["description"]?.ToString(),
                    ["sortorder"] = row["sortorder"] != DBNull.Value ? Convert.ToInt32(row["sortorder"]) : 0,
                    ["ctime"] = row["ctime"]?.ToString(),
                    ["utime"] = row["utime"]?.ToString()
                });
            }
            return Ok(new { success = true, data = list });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取友情链接失败");
            return Ok(new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 添加友情链接
    /// </summary>
    [HttpPost]
    public IActionResult Add([FromBody] FriendLink data)
    {
        try
        {
            var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var id = Guid.NewGuid().ToString();
            
            _db.ExecuteNonQuery(
                "INSERT INTO FriendLink (id, name, link, logo, description, sortorder, ctime, utime) VALUES (@id, @name, @link, @logo, @description, @sortorder, @ctime, @utime)",
                new SqliteParameter("@id", id),
                new SqliteParameter("@name", data.Name ?? ""),
                new SqliteParameter("@link", data.Link ?? ""),
                new SqliteParameter("@logo", data.Logo ?? ""),
                new SqliteParameter("@description", data.Description ?? ""),
                new SqliteParameter("@sortorder", data.SortOrder),
                new SqliteParameter("@ctime", now),
                new SqliteParameter("@utime", now));

            _logger.LogInformation("添加友情链接: {Name}", data.Name);
            return Ok(new { success = true, data = new { id, data.Name, data.Link, data.Logo, data.Description, data.SortOrder, ctime = now, utime = now } });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "添加友情链接失败");
            return Ok(new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 更新友情链接
    /// </summary>
    [HttpPut("{id}")]
    public IActionResult Update(string id, [FromBody] FriendLink data)
    {
        try
        {
            var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var rows = _db.ExecuteNonQuery(
                "UPDATE FriendLink SET name = @name, link = @link, logo = @logo, description = @description, sortorder = @sortorder, utime = @utime WHERE id = @id",
                new SqliteParameter("@name", data.Name ?? ""),
                new SqliteParameter("@link", data.Link ?? ""),
                new SqliteParameter("@logo", data.Logo ?? ""),
                new SqliteParameter("@description", data.Description ?? ""),
                new SqliteParameter("@sortorder", data.SortOrder),
                new SqliteParameter("@utime", now),
                new SqliteParameter("@id", id));

            if (rows > 0)
            {
                _logger.LogInformation("更新友情链接: {Id}", id);
                return Ok(new { success = true, message = "更新成功" });
            }
            return Ok(new { success = false, message = "友情链接不存在" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新友情链接失败: {Id}", id);
            return Ok(new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 删除友情链接
    /// </summary>
    [HttpDelete("{id}")]
    public IActionResult Delete(string id)
    {
        try
        {
            var rows = _db.ExecuteNonQuery(
                "DELETE FROM FriendLink WHERE id = @id",
                new SqliteParameter("@id", id));

            if (rows > 0)
            {
                _logger.LogInformation("删除友情链接: {Id}", id);
                return Ok(new { success = true, message = "删除成功" });
            }
            return Ok(new { success = false, message = "友情链接不存在" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除友情链接失败: {Id}", id);
            return Ok(new { success = false, message = ex.Message });
        }
    }
}

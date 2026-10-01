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
            var dt = _db.ExecuteDataTable("SELECT * FROM friend_links ORDER BY sortorder, id");
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
            return Ok(new { success = false, message = Utils.Api.InternalErrorMessage });
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
            var id = Guid.NewGuid().ToString("N").ToUpper();
            
            _db.ExecuteNonQuery(
                "INSERT INTO friend_links (id, name, link, logo, description, sortorder, ctime, utime) VALUES (@id, @name, @link, @logo, @description, @sortorder, @ctime, @utime)",
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
            return Ok(new { success = false, message = Utils.Api.InternalErrorMessage });
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
                "UPDATE friend_links SET name = @name, link = @link, logo = @logo, description = @description, sortorder = @sortorder, utime = @utime WHERE id = @id",
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
            return Ok(new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>
    /// 按界面给出的顺序把整列重排成 1..N。
    ///
    /// 为什么不是"上移一位"那种一格一请求的接口：页面上挪一条本来是本地瞬时就能看到的事，
    /// 每点一次都要后端重算顺序、前端再重拉整张清单，代价是那块列表被骨架屏顶掉、
    /// 页面高度突变、滚动位置回顶——而挪一条要点好几下，等于每一下都被扔回页面开头。
    /// 现在界面自己换序，攒一下再把最终顺序一次性交上来。
    ///
    /// 这里也不做"交换两条的 sortorder"：库里的数字是 1..29 还带并列（两条都是 5 时交换等于没交换），
    /// 所以一律整体重排成 1..N，只写真的变了的那几行。
    /// </summary>
    [HttpPost("sort")]
    public IActionResult Sort([FromBody] List<string>? ids)
    {
        if (ids is null || ids.Count == 0)
            return Ok(new { success = false, message = "没收到顺序" });
        if (ids.Any(string.IsNullOrWhiteSpace) || ids.Distinct().Count() != ids.Count)
            return Ok(new { success = false, message = "顺序里有空 id 或重复 id" });

        try
        {
            var current = new List<(string Id, int SortOrder)>();
            using var conn = _db.GetConnection();
            conn.Open();
            using (var q = new SqliteCommand("SELECT id, sortorder FROM friend_links ORDER BY sortorder, id", conn))
            using (var reader = q.ExecuteReader())
            {
                while (reader.Read())
                    current.Add((reader.GetString(0), reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader[1])));
            }

            // 条数对不上说明这份清单是旧的（别处刚加过或删过一条）。
            // 按旧快照重排会把那条新记录挤没了，不如直接拒绝，让界面重新拉一次
            if (current.Count != ids.Count)
                return Ok(new { success = false,
                    message = $"清单和库里的条数对不上（{ids.Count} vs {current.Count}），重新拉一次再排" });

            var unknown = ids.FirstOrDefault(id => !current.Any(c => c.Id == id));
            if (unknown is not null)
                return Ok(new { success = false, message = "有不认识的链接 id，重新拉一次再排" });

            var byId = current.ToDictionary(x => x.Id, x => x.SortOrder);
            using var tx = conn.BeginTransaction();
            var changed = 0;
            for (var i = 0; i < ids.Count; i++)
            {
                var want = i + 1;
                if (byId[ids[i]] == want) continue;

                using var upd = new SqliteCommand(
                    "UPDATE friend_links SET sortorder = @s, utime = @t WHERE id = @id", conn, tx);
                upd.Parameters.AddWithValue("@s", want);
                upd.Parameters.AddWithValue("@t", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                upd.Parameters.AddWithValue("@id", ids[i]);
                changed += upd.ExecuteNonQuery();
            }
            tx.Commit();

            _logger.LogInformation("友情链接重排：{Total} 条，实际改动 {Changed} 条", ids.Count, changed);
            return Ok(new
            {
                success = true,
                message = changed == 0 ? "顺序和库里一致，没改动" : $"已保存顺序，改动 {changed} 条",
                data = new { total = ids.Count, changed }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "重排友情链接失败");
            return Ok(new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>
    /// 删除友情链接
    /// </summary>
    [Utils.AdminToken]
    [HttpDelete("{id}")]
    public IActionResult Delete(string id)
    {
        try
        {
            var rows = _db.ExecuteNonQuery(
                "DELETE FROM friend_links WHERE id = @id",
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
            return Ok(new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }
}

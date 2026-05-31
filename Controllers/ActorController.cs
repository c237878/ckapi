using ckapi.Models;
using ckapi.Utils;
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
    private readonly SQLiteHelper _db;

    public ActorController(ILogger<ActorController> logger, SQLiteHelper db)
    {
        _logger = logger;
        _db = db;
    }

    /// <summary>
    /// 获取演员列表
    /// </summary>
    [HttpGet]
    public IActionResult GetActors([FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] string? country = null)
    {
        try
        {
            var offset = (page - 1) * pageSize;
            var whereClause = "WHERE 1=1";
            var parameters = new List<SqliteParameter>();

            if (!string.IsNullOrEmpty(country))
            {
                whereClause += " AND country = @country";
                parameters.Add(new SqliteParameter("@country", country));
            }

            var countSql = $"SELECT COUNT(*) FROM Actor {whereClause}";
            var total = Convert.ToInt32(_db.ExecuteScalar(countSql, parameters.ToArray()));

            var sql = $@"
                SELECT a.*, (SELECT COUNT(*) FROM VideoActor va WHERE va.actorid = a.id) as video_count FROM Actor a 
                {whereClause}
                ORDER BY a.name ASC
                LIMIT @pageSize OFFSET @offset";
            parameters.Add(new SqliteParameter("@pageSize", pageSize));
            parameters.Add(new SqliteParameter("@offset", offset));

            var dt = _db.ExecuteDataTable(sql, parameters.ToArray());
            var actors = new List<Actor>();
            foreach (System.Data.DataRow row in dt.Rows)
            {
                actors.Add(new Actor
                {
                    Id = row["id"]?.ToString(),
                    Name = row["name"]?.ToString(),
                    Alias = row["alias"]?.ToString(),
                    Country = row["country"]?.ToString(),
                    CTime = row["ctime"]?.ToString(),
                    UTime = row["utime"]?.ToString(),
                    VideoCount = row["video_count"] != DBNull.Value ? Convert.ToInt32(row["video_count"]) : 0
                });
            }

            return Ok(new { success = true, data = actors, total, page, pageSize });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取演员列表失败");
            return Ok(new { success = false, message = "获取演员列表失败" });
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
            var sql = "SELECT * FROM Actor WHERE id = @id";
            var dt = _db.ExecuteDataTable(sql, new SqliteParameter("@id", id));
            if (dt.Rows.Count == 0)
            {
                return Ok(new { success = false, message = "演员不存在" });
            }

            var row = dt.Rows[0];
            var actor = new Actor
            {
                Id = row["id"]?.ToString(),
                Name = row["name"]?.ToString(),
                Alias = row["alias"]?.ToString(),
                Country = row["country"]?.ToString(),
                CTime = row["ctime"]?.ToString(),
                UTime = row["utime"]?.ToString()
            };

            return Ok(new { success = true, data = actor });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取演员详情失败");
            return Ok(new { success = false, message = "获取演员详情失败" });
        }
    }

    /// <summary>
    /// 添加演员
    /// </summary>
    [HttpPost]
    public IActionResult AddActor([FromBody] Actor actor)
    {
        try
        {
            if (string.IsNullOrEmpty(actor.Name))
            {
                return Ok(new { success = false, message = "演员姓名不能为空" });
            }

            actor.Id = Guid.NewGuid().ToString();
            actor.CTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            actor.UTime = actor.CTime;

            var sql = @"
                INSERT INTO Actor (id, name, alias, country, ctime, utime)
                VALUES (@id, @name, @alias, @country, @ctime, @utime)";

            _db.ExecuteNonQuery(sql,
                new SqliteParameter("@id", actor.Id),
                new SqliteParameter("@name", actor.Name),
                new SqliteParameter("@alias", actor.Alias ?? ""),
                new SqliteParameter("@country", actor.Country ?? ""),
                new SqliteParameter("@ctime", actor.CTime),
                new SqliteParameter("@utime", actor.UTime)
            );

            return Ok(new { success = true, data = actor, message = "添加成功" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "添加演员失败");
            return Ok(new { success = false, message = "添加演员失败" });
        }
    }

    /// <summary>
    /// 更新演员
    /// </summary>
    [HttpPut("{id}")]
    public IActionResult UpdateActor(string id, [FromBody] Actor actor)
    {
        try
        {
            actor.UTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            var sql = @"
                UPDATE Actor SET 
                    name = @name,
                    alias = @alias,
                    country = @country,
                    utime = @utime
                WHERE id = @id";

            var result = _db.ExecuteNonQuery(sql,
                new SqliteParameter("@id", id),
                new SqliteParameter("@name", actor.Name ?? ""),
                new SqliteParameter("@alias", actor.Alias ?? ""),
                new SqliteParameter("@country", actor.Country ?? ""),
                new SqliteParameter("@utime", actor.UTime)
            );

            if (result > 0)
            {
                return Ok(new { success = true, message = "更新成功" });
            }
            else
            {
                return Ok(new { success = false, message = "演员不存在" });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新演员失败");
            return Ok(new { success = false, message = "更新演员失败" });
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
            // 先删除演员-影片关联
            _db.ExecuteNonQuery("DELETE FROM VideoActor WHERE actorid = @actorid", 
                new SqliteParameter("@actorid", id));

            // 再删除演员
            var sql = "DELETE FROM Actor WHERE id = @id";
            var result = _db.ExecuteNonQuery(sql, new SqliteParameter("@id", id));

            if (result > 0)
            {
                return Ok(new { success = true, message = "删除成功" });
            }
            else
            {
                return Ok(new { success = false, message = "演员不存在" });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除演员失败");
            return Ok(new { success = false, message = "删除演员失败" });
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
            var countSql = @"
                SELECT COUNT(*) FROM Video v
                INNER JOIN VideoActor va ON v.id = va.videoid
                WHERE va.actorid = @actorid";
            var total = Convert.ToInt32(_db.ExecuteScalar(countSql, new SqliteParameter("@actorid", id)));

            var sql = @"
                SELECT v.* FROM Video v
                INNER JOIN VideoActor va ON v.id = va.videoid
                WHERE va.actorid = @actorid
                ORDER BY v.ctime DESC
                LIMIT @pageSize OFFSET @offset";
            var dt = _db.ExecuteDataTable(sql,
                new SqliteParameter("@actorid", id),
                new SqliteParameter("@pageSize", pageSize),
                new SqliteParameter("@offset", offset));

            var videos = new List<Video>();
            foreach (System.Data.DataRow row in dt.Rows)
            {
                videos.Add(new Video
                {
                    Id = row["id"]?.ToString(),
                    Code = row["code"]?.ToString(),
                    Name = row["name"]?.ToString(),
                    Country = row["country"]?.ToString(),
                    CoverUrl = row["coverurl"]?.ToString(),
                    VideoUrl = row["videourl"]?.ToString(),
                    VideoSize = row["videosize"] != DBNull.Value ? Convert.ToInt64(row["videosize"]) : 0,
                    Quality = row["quality"]?.ToString(),
                    SeriesId = row["seriesid"]?.ToString(),
                    SortOrder = row["sortorder"] != DBNull.Value ? Convert.ToInt32(row["sortorder"]) : 0,
                    CTime = row["ctime"]?.ToString(),
                    UTime = row["utime"]?.ToString()
                });
            }

            return Ok(new { success = true, data = videos, total, page, pageSize });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取演员影片失败");
            return Ok(new { success = false, message = "获取演员影片失败" });
        }
    }
}

using ckapi.Models;
using ckapi.Utils;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace ckapi.Controllers;

/// <summary>
/// 影视系列相关接口
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class SeriesController : ControllerBase
{
    private readonly ILogger<SeriesController> _logger;
    private readonly SQLiteHelper _db;

    public SeriesController(ILogger<SeriesController> logger, SQLiteHelper db)
    {
        _logger = logger;
        _db = db;
    }

    /// <summary>
    /// 获取系列列表
    /// </summary>
    [HttpGet]
    public IActionResult GetSeriesList([FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] string? country = null)
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

            var countSql = $"SELECT COUNT(*) FROM VideoSeries {whereClause}";
            var total = Convert.ToInt32(_db.ExecuteScalar(countSql, parameters.ToArray()));

            var sql = $@"
                SELECT * FROM VideoSeries 
                {whereClause}
                ORDER BY name ASC
                LIMIT @pageSize OFFSET @offset";
            parameters.Add(new SqliteParameter("@pageSize", pageSize));
            parameters.Add(new SqliteParameter("@offset", offset));

            var dt = _db.ExecuteDataTable(sql, parameters.ToArray());
            var series = new List<VideoSeries>();
            foreach (System.Data.DataRow row in dt.Rows)
            {
                series.Add(new VideoSeries
                {
                    Id = row["id"]?.ToString(),
                    Name = row["name"]?.ToString(),
                    Alias = row["alias"]?.ToString(),
                    Link = row["link"]?.ToString(),
                    Country = row["country"]?.ToString(),
                    CTime = row["ctime"]?.ToString(),
                    UTime = row["utime"]?.ToString()
                });
            }

            return Ok(new { success = true, data = series, total, page, pageSize });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取系列列表失败");
            return Ok(new { success = false, message = "获取系列列表失败" });
        }
    }

    /// <summary>
    /// 获取系列详情
    /// </summary>
    [HttpGet("{id}")]
    public IActionResult GetSeries(string id)
    {
        try
        {
            var sql = "SELECT * FROM VideoSeries WHERE id = @id";
            var dt = _db.ExecuteDataTable(sql, new SqliteParameter("@id", id));
            if (dt.Rows.Count == 0)
            {
                return Ok(new { success = false, message = "系列不存在" });
            }

            var row = dt.Rows[0];
            var series = new VideoSeries
            {
                Id = row["id"]?.ToString(),
                Name = row["name"]?.ToString(),
                Alias = row["alias"]?.ToString(),
                Link = row["link"]?.ToString(),
                Country = row["country"]?.ToString(),
                CTime = row["ctime"]?.ToString(),
                UTime = row["utime"]?.ToString()
            };

            return Ok(new { success = true, data = series });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取系列详情失败");
            return Ok(new { success = false, message = "获取系列详情失败" });
        }
    }

    /// <summary>
    /// 获取系列下的影片
    /// </summary>
    [HttpGet("{id}/videos")]
    public IActionResult GetSeriesVideos(string id, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        try
        {
            var offset = (page - 1) * pageSize;
            var countSql = "SELECT COUNT(*) FROM Video WHERE seriesid = @seriesid";
            var total = Convert.ToInt32(_db.ExecuteScalar(countSql, new SqliteParameter("@seriesid", id)));

            var sql = @"
                SELECT * FROM Video 
                WHERE seriesid = @seriesid
                ORDER BY sortorder ASC, ctime DESC
                LIMIT @pageSize OFFSET @offset";
            var dt = _db.ExecuteDataTable(sql,
                new SqliteParameter("@seriesid", id),
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
            _logger.LogError(ex, "获取系列影片失败");
            return Ok(new { success = false, message = "获取系列影片失败" });
        }
    }

    /// <summary>
    /// 添加系列
    /// </summary>
    [HttpPost]
    public IActionResult AddSeries([FromBody] VideoSeries series)
    {
        try
        {
            if (string.IsNullOrEmpty(series.Name))
            {
                return Ok(new { success = false, message = "系列名称不能为空" });
            }

            series.Id = Guid.NewGuid().ToString();
            series.CTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            series.UTime = series.CTime;

            var sql = @"
                INSERT INTO VideoSeries (id, name, alias, link, country, ctime, utime)
                VALUES (@id, @name, @alias, @link, @country, @ctime, @utime)";

            _db.ExecuteNonQuery(sql,
                new SqliteParameter("@id", series.Id),
                new SqliteParameter("@name", series.Name),
                new SqliteParameter("@alias", series.Alias ?? ""),
                new SqliteParameter("@link", series.Link ?? ""),
                new SqliteParameter("@country", series.Country ?? ""),
                new SqliteParameter("@ctime", series.CTime),
                new SqliteParameter("@utime", series.UTime)
            );

            return Ok(new { success = true, data = series, message = "添加成功" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "添加系列失败");
            return Ok(new { success = false, message = "添加系列失败" });
        }
    }

    /// <summary>
    /// 更新系列
    /// </summary>
    [HttpPut("{id}")]
    public IActionResult UpdateSeries(string id, [FromBody] VideoSeries series)
    {
        try
        {
            series.UTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            var sql = @"
                UPDATE VideoSeries SET 
                    name = @name,
                    alias = @alias,
                    link = @link,
                    country = @country,
                    utime = @utime
                WHERE id = @id";

            var result = _db.ExecuteNonQuery(sql,
                new SqliteParameter("@id", id),
                new SqliteParameter("@name", series.Name ?? ""),
                new SqliteParameter("@alias", series.Alias ?? ""),
                new SqliteParameter("@link", series.Link ?? ""),
                new SqliteParameter("@country", series.Country ?? ""),
                new SqliteParameter("@utime", series.UTime)
            );

            if (result > 0)
            {
                return Ok(new { success = true, message = "更新成功" });
            }
            else
            {
                return Ok(new { success = false, message = "系列不存在" });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新系列失败");
            return Ok(new { success = false, message = "更新系列失败" });
        }
    }

    /// <summary>
    /// 删除系列
    /// </summary>
    [HttpDelete("{id}")]
    public IActionResult DeleteSeries(string id)
    {
        try
        {
            // 先清空该系列下影片的seriesid
            _db.ExecuteNonQuery("UPDATE Video SET seriesid = NULL WHERE seriesid = @seriesid",
                new SqliteParameter("@seriesid", id));

            // 再删除系列
            var sql = "DELETE FROM VideoSeries WHERE id = @id";
            var result = _db.ExecuteNonQuery(sql, new SqliteParameter("@id", id));

            if (result > 0)
            {
                return Ok(new { success = true, message = "删除成功" });
            }
            else
            {
                return Ok(new { success = false, message = "系列不存在" });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除系列失败");
            return Ok(new { success = false, message = "删除系列失败" });
        }
    }
}

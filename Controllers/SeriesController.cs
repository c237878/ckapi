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

            var countSql = $"SELECT COUNT(*) FROM video_series s {whereClause}";
            var total = Convert.ToInt32(_db.ExecuteScalar(countSql, parameters.ToArray()));

            var sql = $@"
                SELECT s.*, 
                       (SELECT COUNT(*) FROM videos v WHERE v.seriesid = s.id) as video_count,
                       (SELECT COUNT(*) FROM video_likes vl JOIN videos v ON vl.video_id = v.id WHERE v.seriesid = s.id) as like_count
                FROM video_series s
                {whereClause}
                ORDER BY s.name ASC
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
                    UTime = row["utime"]?.ToString(),
                    VideoCount = row["video_count"] != DBNull.Value ? Convert.ToInt32(row["video_count"]) : 0,
                    LikeCount = row["like_count"] != DBNull.Value ? Convert.ToInt32(row["like_count"]) : 0
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
            var sql = @"SELECT s.*, 
                        (SELECT COUNT(*) FROM video_likes vl JOIN videos v ON vl.video_id = v.id WHERE v.seriesid = s.id) as like_count
                        FROM video_series s WHERE s.id = @id";
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
                UTime = row["utime"]?.ToString(),
                LikeCount = row["like_count"] != DBNull.Value ? Convert.ToInt32(row["like_count"]) : 0
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

            var countSql = "SELECT COUNT(*) FROM videos WHERE seriesid = @seriesid";
            var total = Convert.ToInt32(_db.ExecuteScalar(countSql, new SqliteParameter("@seriesid", id)));

            var sql = @"
                SELECT * FROM videos 
                WHERE seriesid = @seriesid
                ORDER BY ctime DESC
                LIMIT @pageSize OFFSET @offset";
            var dt = _db.ExecuteDataTable(sql,
                new SqliteParameter("@seriesid", id),
                new SqliteParameter("@pageSize", pageSize),
                new SqliteParameter("@offset", offset));

            var videos = new List<object>();
            foreach (System.Data.DataRow row in dt.Rows)
            {
                var videoId = row["id"]?.ToString();

                var actorSql = @"SELECT a.* FROM actors a INNER JOIN video_actors va ON a.id = va.actor_id WHERE va.video_id = @videoId";
                var actorDt = _db.ExecuteDataTable(actorSql, new SqliteParameter("@videoId", videoId));
                var actors = new List<object>();
                foreach (System.Data.DataRow actorRow in actorDt.Rows)
                {
                    actors.Add(new
                    {
                        Id = actorRow["id"]?.ToString(),
                        Name = actorRow["name"]?.ToString(),
                        Alias = actorRow["alias"]?.ToString(),
                        Country = actorRow["country"]?.ToString()
                    });
                }

                videos.Add(new
                {
                    Id = videoId,
                    Code = row["code"]?.ToString(),
                    Name = row["name"]?.ToString(),
                    Category = row["category"]?.ToString(),
                    Country = row["country"] == DBNull.Value ? "" : row["country"].ToString(),
                    CoverPath = row["cover_path"] == DBNull.Value ? null : row["cover_path"].ToString(),
                    FilePath = row["file_path"]?.ToString(),
                    FileSize = row["file_size"] != DBNull.Value ? Convert.ToInt64(row["file_size"]) : 0,
                    SeriesId = row["seriesid"]?.ToString(),
                    Ctime = row["ctime"]?.ToString(),
                    Actors = actors
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

            if (string.IsNullOrEmpty(series.Id))
            {
                series.Id = Guid.NewGuid().ToString("N").ToUpper();
            }

            var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            series.CTime = now;
            series.UTime = now;

            var sql = @"
                INSERT INTO video_series (id, name, alias, link, country, ctime, utime)
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

            return Ok(new { success = true, data = series });
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
            var sql = @"
                UPDATE video_series SET 
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
                return Ok(new { success = true, message = "更新成功" });
            else
                return Ok(new { success = false, message = "系列不存在" });
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
            // 将系列下影片的seriesid置为空
            _db.ExecuteNonQuery("UPDATE videos SET seriesid = NULL WHERE seriesid = @seriesid",
                new SqliteParameter("@seriesid", id));

            // 删除系列
            var result = _db.ExecuteNonQuery("DELETE FROM video_series WHERE id = @id",
                new SqliteParameter("@id", id));

            if (result > 0)
                return Ok(new { success = true, message = "删除成功" });
            else
                return Ok(new { success = false, message = "系列不存在" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除系列失败");
            return Ok(new { success = false, message = "删除系列失败" });
        }
    }
}

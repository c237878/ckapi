using Microsoft.AspNetCore.Mvc;
using ckapi.Models;
using ckapi.Utils;
using Microsoft.Data.Sqlite;

namespace ckapi.Controllers;

/// <summary>
/// 影片控制器
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class VideoController : ControllerBase
{
    private readonly SQLiteHelper _db;
    private readonly ILogger<VideoController> _logger;

    public VideoController(SQLiteHelper db, ILogger<VideoController> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// 获取视频列表
    /// </summary>
    [HttpGet]
    public IActionResult GetList([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        try
        {
            var offset = (page - 1) * pageSize;
            
            // 获取总数
            var countSql = "SELECT COUNT(*) FROM Video";
            var total = Convert.ToInt32(_db.ExecuteScalar(countSql));

            // 获取列表
            var sql = @"
                SELECT v.* FROM Video v
                ORDER BY v.sortorder ASC, v.ctime DESC
                LIMIT @pageSize OFFSET @offset";

            var dt = _db.ExecuteDataTable(sql,
                new SqliteParameter("@pageSize", pageSize),
                new SqliteParameter("@offset", offset));

            var videos = new List<Video>();
            foreach (System.Data.DataRow row in dt.Rows)
            {
                var video = new Video
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
                    UTime = row["utime"]?.ToString(),
                    Actors = new List<Actor>()
                };
                
                // 获取该视频的演员列表
                var actorSql = @"
                    SELECT a.* FROM Actor a 
                    INNER JOIN VideoActor va ON a.id = va.actor_id 
                    WHERE va.video_id = @videoId
                    ORDER BY a.name";
                var actorDt = _db.ExecuteDataTable(actorSql, 
                    new SqliteParameter("@videoId", video.Id));
                
                foreach (System.Data.DataRow actorRow in actorDt.Rows)
                {
                    video.Actors.Add(new Actor
                    {
                        Id = actorRow["id"]?.ToString(),
                        Name = actorRow["name"]?.ToString(),
                        Country = actorRow["country"]?.ToString()
                    });
                }
                
                videos.Add(video);
            }

            return Ok(new { success = true, data = videos, total, page, pageSize });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取视频列表失败");
            return Ok(new { success = false, message = "获取视频列表失败" });
        }
    }

    /// <summary>
    /// 获取视频详情
    /// </summary>
    [HttpGet("{id}")]
    public IActionResult GetVideo(string id)
    {
        try
        {
            var sql = "SELECT * FROM Video WHERE id = @id";
            var dt = _db.ExecuteDataTable(sql, new SqliteParameter("@id", id));
            if (dt.Rows.Count == 0)
            {
                return Ok(new { success = false, message = "视频不存在" });
            }

            var row = dt.Rows[0];
            var video = new Video
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
                UTime = row["utime"]?.ToString(),
                Actors = new List<Actor>()
            };

            // 获取关联的演员列表
            var actorSql = @"
                SELECT a.* FROM Actor a
                INNER JOIN VideoActor va ON a.id = va.actor_id
                WHERE va.video_id = @videoId
                ORDER BY a.name";
            var actorDt = _db.ExecuteDataTable(actorSql, new SqliteParameter("@videoId", id));

            var actors = new List<Actor>();
            foreach (System.Data.DataRow actorRow in actorDt.Rows)
            {
                actors.Add(new Actor
                {
                    Id = actorRow["id"]?.ToString(),
                    Name = actorRow["name"]?.ToString(),
                    Alias = actorRow["alias"]?.ToString(),
                    Country = actorRow["country"]?.ToString(),
                    CTime = actorRow["ctime"]?.ToString(),
                    UTime = actorRow["utime"]?.ToString()
                });
            }

            return Ok(new { success = true, data = video, actors });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取视频详情失败");
            return Ok(new { success = false, message = "获取视频详情失败" });
        }
    }

    /// <summary>
    /// 获取今日推荐
    /// </summary>
    [HttpGet("recommend")]
    public IActionResult GetRecommend([FromQuery] int limit = 10)
    {
        try
        {
            var sql = @"
                SELECT * FROM Video 
                ORDER BY sortorder ASC, ctime DESC
                LIMIT @limit";

            var dt = _db.ExecuteDataTable(sql, new SqliteParameter("@limit", limit));

            var videos = new List<Video>();
            foreach (System.Data.DataRow row in dt.Rows)
            {
                var video = new Video
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
                    UTime = row["utime"]?.ToString(),
                    Actors = new List<Actor>()
                };
                
                // 获取该视频的演员列表
                var actorSql = @"
                    SELECT a.* FROM Actor a 
                    INNER JOIN VideoActor va ON a.id = va.actor_id 
                    WHERE va.video_id = @videoId
                    ORDER BY a.name";
                var actorDt = _db.ExecuteDataTable(actorSql, 
                    new SqliteParameter("@videoId", video.Id));
                
                foreach (System.Data.DataRow actorRow in actorDt.Rows)
                {
                    video.Actors.Add(new Actor
                    {
                        Id = actorRow["id"]?.ToString(),
                        Name = actorRow["name"]?.ToString(),
                        Country = actorRow["country"]?.ToString()
                    });
                }
                
                videos.Add(video);
            }

            return Ok(new { success = true, data = videos });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取推荐视频失败");
            return Ok(new { success = false, message = "获取推荐视频失败" });
        }
    }

    /// <summary>
    /// 添加视频
    /// </summary>
    [HttpPost]
    public IActionResult AddVideo([FromBody] Video video)
    {
        try
        {
            if (string.IsNullOrEmpty(video.Name))
            {
                return Ok(new { success = false, message = "视频名称不能为空" });
            }

            video.Id = Guid.NewGuid().ToString();
            video.CTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            video.UTime = video.CTime;

            var sql = @"
                INSERT INTO Video (id, code, name, country, coverurl, videourl, videosize, quality, seriesid, sortorder, ctime, utime)
                VALUES (@id, @code, @name, @country, @coverurl, @videourl, @videosize, @quality, @seriesid, @sortorder, @ctime, @utime)";

            _db.ExecuteNonQuery(sql,
                new SqliteParameter("@id", video.Id),
                new SqliteParameter("@code", video.Code ?? ""),
                new SqliteParameter("@name", video.Name),
                new SqliteParameter("@country", video.Country ?? ""),
                new SqliteParameter("@coverurl", video.CoverUrl ?? ""),
                new SqliteParameter("@videourl", video.VideoUrl ?? ""),
                new SqliteParameter("@videosize", video.VideoSize),
                new SqliteParameter("@quality", video.Quality ?? ""),
                new SqliteParameter("@seriesid", video.SeriesId ?? ""),
                new SqliteParameter("@sortorder", video.SortOrder),
                new SqliteParameter("@ctime", video.CTime),
                new SqliteParameter("@utime", video.UTime)
            );

            return Ok(new { success = true, data = video, message = "添加成功" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "添加视频失败");
            return Ok(new { success = false, message = "添加视频失败: " + ex.Message });
        }
    }

    /// <summary>
    /// 更新视频
    /// </summary>
    [HttpPut("{id}")]
    public IActionResult UpdateVideo(string id, [FromBody] Video video)
    {
        try
        {
            video.UTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            var sql = @"
                UPDATE Video 
                SET code = @code, 
                    name = @name, 
                    country = @country,
                    coverurl = @coverurl,
                    videourl = @videourl,
                    videosize = @videosize, 
                    quality = @quality, 
                    seriesid = @seriesid, 
                    sortorder = @sortorder, 
                    utime = @utime
                WHERE id = @id";

            var rows = _db.ExecuteNonQuery(sql,
                new SqliteParameter("@code", video.Code ?? ""),
                new SqliteParameter("@name", video.Name ?? ""),
                new SqliteParameter("@country", video.Country ?? ""),
                new SqliteParameter("@coverurl", video.CoverUrl ?? ""),
                new SqliteParameter("@videourl", video.VideoUrl ?? ""),
                new SqliteParameter("@videosize", video.VideoSize),
                new SqliteParameter("@quality", video.Quality ?? ""),
                new SqliteParameter("@seriesid", video.SeriesId ?? ""),
                new SqliteParameter("@sortorder", video.SortOrder),
                new SqliteParameter("@utime", video.UTime),
                new SqliteParameter("@id", id)
            );

            if (rows > 0)
            {
                return Ok(new { success = true, message = "更新成功" });
            }
            return Ok(new { success = false, message = "视频不存在" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新视频失败");
            return Ok(new { success = false, message = "更新视频失败" });
        }
    }

    /// <summary>
    /// 删除视频
    /// </summary>
    [HttpDelete("{id}")]
    public IActionResult DeleteVideo(string id)
    {
        try
        {
            var rows = _db.ExecuteNonQuery(
                "DELETE FROM Video WHERE id = @id",
                new SqliteParameter("@id", id));

            if (rows > 0)
            {
                return Ok(new { success = true, message = "删除成功" });
            }
            return Ok(new { success = false, message = "视频不存在" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除视频失败");
            return Ok(new { success = false, message = "删除视频失败" });
        }
    }
}

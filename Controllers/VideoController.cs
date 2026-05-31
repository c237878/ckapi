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
    public IActionResult GetList([FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] string? country = null, [FromQuery] string? seriesId = null)
    {
        try
        {
            var offset = (page - 1) * pageSize;
            var whereClause = "WHERE 1=1";
            var parameters = new List<SqliteParameter>();

            if (!string.IsNullOrEmpty(country))
            {
                whereClause += " AND v.country = @country";
                parameters.Add(new SqliteParameter("@country", country));
            }

            if (!string.IsNullOrEmpty(seriesId))
            {
                whereClause += " AND v.seriesid = @seriesId";
                parameters.Add(new SqliteParameter("@seriesId", seriesId));
            }

            // 获取总数
            var countSql = $"SELECT COUNT(*) FROM Video v {whereClause}";
            var total = Convert.ToInt32(_db.ExecuteScalar(countSql, parameters.ToArray()));

            // 获取列表
            var sql = $@"
                SELECT v.* FROM Video v
                {whereClause}
                ORDER BY v.sortorder ASC, v.ctime DESC
                LIMIT @pageSize OFFSET @offset";
            parameters.Add(new SqliteParameter("@pageSize", pageSize));
            parameters.Add(new SqliteParameter("@offset", offset));

            var dt = _db.ExecuteDataTable(sql, parameters.ToArray());

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
                    INNER JOIN VideoActor va ON a.id = va.actorid 
                    WHERE va.videoid = @videoId
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
                INNER JOIN VideoActor va ON a.id = va.actorid
                WHERE va.videoid = @videoId
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

            // 获取所属系列
            object seriesData = null;
            if (!string.IsNullOrEmpty(video.SeriesId))
            {
                var seriesSql = "SELECT * FROM VideoSeries WHERE id = @seriesId";
                var seriesDt = _db.ExecuteDataTable(seriesSql, new SqliteParameter("@seriesId", video.SeriesId));
                if (seriesDt.Rows.Count > 0)
                {
                    var sRow = seriesDt.Rows[0];
                    seriesData = new
                    {
                        id = sRow["id"]?.ToString(),
                        name = sRow["name"]?.ToString()
                    };
                }
            }

            return Ok(new { success = true, data = video, actors, series = seriesData });
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
    public IActionResult GetRecommend([FromQuery] string videoId = "", [FromQuery] int limit = 10)
    {
        try
        {
            // 确保limit为偶数且至少8
            if (limit < 8) limit = 8;
            if (limit % 2 != 0) limit++;

            List<string> videoIds = new List<string>();

            if (!string.IsNullOrEmpty(videoId))
            {
                // 获取当前视频所属系列ID
                var seriesSql = "SELECT seriesid FROM Video WHERE id = @id";
                var seriesDt = _db.ExecuteDataTable(seriesSql, new SqliteParameter("@id", videoId));
                string seriesId = seriesDt.Rows.Count > 0 ? seriesDt.Rows[0]["seriesid"]?.ToString() : null;

                // 优先推荐同系列影片
                if (!string.IsNullOrEmpty(seriesId))
                {
                    var sameSeriesSql = "SELECT id FROM Video WHERE seriesid = @seriesId AND id != @id ORDER BY ctime DESC";
                    var sameSeriesDt = _db.ExecuteDataTable(sameSeriesSql,
                        new SqliteParameter("@seriesId", seriesId),
                        new SqliteParameter("@id", videoId));
                    foreach (System.Data.DataRow r in sameSeriesDt.Rows)
                    {
                        videoIds.Add(r["id"].ToString());
                    }
                }

                // 其次推荐同演员影片
                var sameActorSql = @"
                    SELECT v.id FROM Video v
                    INNER JOIN VideoActor va ON v.id = va.videoid
                    INNER JOIN VideoActor va2 ON va.actorid = va2.actorid
                    WHERE va2.videoid = @id AND v.id != @id
                    ORDER BY v.ctime DESC";
                var sameActorDt = _db.ExecuteDataTable(sameActorSql, new SqliteParameter("@id", videoId));
                foreach (System.Data.DataRow r in sameActorDt.Rows)
                {
                    var vid = r["id"].ToString();
                    if (!videoIds.Contains(vid)) videoIds.Add(vid);
                }

                // 如果不够，随机推荐
                if (videoIds.Count < limit)
                {
                    var idList = string.Join(",", videoIds.Select((_, i) => $"@eid{i}"));
                    var excludeSql = string.IsNullOrEmpty(idList)
                        ? "SELECT id FROM Video WHERE id != @id ORDER BY RANDOM()"
                        : $"SELECT id FROM Video WHERE id != @id AND id NOT IN ({idList}) ORDER BY RANDOM()";
                    var excludeParams = new List<SqliteParameter>
                    {
                        new SqliteParameter("@id", videoId)
                    };
                    for (int i = 0; i < videoIds.Count; i++)
                    {
                        excludeParams.Add(new SqliteParameter($"@eid{i}", videoIds[i]));
                    }
                    var remain = limit - videoIds.Count;
                    var randomDt = _db.ExecuteDataTable(excludeSql, excludeParams.ToArray());
                    foreach (System.Data.DataRow r in randomDt.Rows)
                    {
                        videoIds.Add(r["id"].ToString());
                        remain--;
                        if (remain <= 0) break;
                    }
                }

                // 限制数量并取前limit个
                videoIds = videoIds.Take(limit).ToList();
            }
            else
            {
                // 无videoId时随机推荐
                var randomSql = "SELECT id FROM Video ORDER BY RANDOM() LIMIT @limit";
                var randomDt = _db.ExecuteDataTable(randomSql, new SqliteParameter("@limit", limit));
                foreach (System.Data.DataRow r in randomDt.Rows)
                {
                    videoIds.Add(r["id"].ToString());
                }
            }

            if (videoIds.Count == 0)
            {
                return Ok(new { success = true, data = new List<Video>() });
            }

            // 批量查询视频详情
            var idsStr = string.Join(",", videoIds.Select((_, i) => $"@vid{i}"));
            var fetchSql = $"SELECT * FROM Video WHERE id IN ({idsStr})";
            var fetchParams = new List<SqliteParameter>();
            for (int i = 0; i < videoIds.Count; i++)
            {
                fetchParams.Add(new SqliteParameter($"@vid{i}", videoIds[i]));
            }
            var dt = _db.ExecuteDataTable(fetchSql, fetchParams.ToArray());

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
                    INNER JOIN VideoActor va ON a.id = va.actorid 
                    WHERE va.videoid = @videoId
                    ORDER BY a.name";
                var actorDt = _db.ExecuteDataTable(actorSql, new SqliteParameter("@videoId", video.Id));
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

            // 保持推荐顺序
            return Ok(new { success = true, data = videos.OrderBy(v => videoIds.IndexOf(v.Id)).ToList() });
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

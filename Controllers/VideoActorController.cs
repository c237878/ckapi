using Microsoft.AspNetCore.Mvc;
using ckapi.Models;
using ckapi.Utils;
using Microsoft.Data.Sqlite;

namespace ckapi.Controllers;

/// <summary>
/// 视频-演员关联控制器
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class VideoActorController : ControllerBase
{
    private readonly SQLiteHelper _db;
    private readonly ILogger<VideoActorController> _logger;

    public VideoActorController(SQLiteHelper db, ILogger<VideoActorController> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// 获取视频关联的演员列表
    /// </summary>
    [HttpGet("video/{videoId}")]
    public IActionResult GetActorsByVideo(string videoId)
    {
        try
        {
            var sql = @"
                SELECT a.* FROM Actor a
                INNER JOIN VideoActor va ON a.id = va.actorid
                WHERE va.videoid = @videoId
                ORDER BY a.name";

            var dt = _db.ExecuteDataTable(sql, new SqliteParameter("@videoId", videoId));

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
                    UTime = row["utime"]?.ToString()
                });
            }

            return Ok(new { success = true, data = actors });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取视频演员失败: {VideoId}", videoId);
            return Ok(new { success = false, message = "获取视频演员失败" });
        }
    }

    /// <summary>
    /// 获取演员关联的视频列表
    /// </summary>
    [HttpGet("actor/{actorId}")]
    public IActionResult GetVideosByActor(string actorId)
    {
        try
        {
            var sql = @"
                SELECT v.* FROM Video v
                INNER JOIN VideoActor va ON v.id = va.videoid
                WHERE va.actorid = @actorId
                ORDER BY v.sortorder ASC, v.ctime DESC";

            var dt = _db.ExecuteDataTable(sql, new SqliteParameter("@actorId", actorId));

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

            return Ok(new { success = true, data = videos });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取演员视频失败: {ActorId}", actorId);
            return Ok(new { success = false, message = "获取演员视频失败" });
        }
    }

    /// <summary>
    /// 添加视频-演员关联
    /// </summary>
    [HttpPost]
    public IActionResult AddRelation([FromBody] VideoActor relation)
    {
        try
        {
            if (string.IsNullOrEmpty(relation.VideoId) || string.IsNullOrEmpty(relation.ActorId))
            {
                return Ok(new { success = false, message = "视频ID和演员ID不能为空" });
            }

            // 检查是否已存在
            var checkSql = "SELECT COUNT(*) FROM VideoActor WHERE videoid = @videoId AND actorid = @actorId";
            var exists = Convert.ToInt32(_db.ExecuteScalar(checkSql,
                new SqliteParameter("@videoId", relation.VideoId),
                new SqliteParameter("@actorId", relation.ActorId))) > 0;

            if (exists)
            {
                return Ok(new { success = false, message = "该关联已存在" });
            }

            relation.Id = Guid.NewGuid().ToString();
            relation.CTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            relation.UTime = relation.CTime;

            var sql = @"
                INSERT INTO VideoActor (id, videoid, actorid, ctime, utime)
                VALUES (@id, @videoId, @actorId, @ctime, @utime)";

            _db.ExecuteNonQuery(sql,
                new SqliteParameter("@id", relation.Id),
                new SqliteParameter("@videoId", relation.VideoId),
                new SqliteParameter("@actorId", relation.ActorId),
                new SqliteParameter("@ctime", relation.CTime),
                new SqliteParameter("@utime", relation.UTime)
            );

            return Ok(new { success = true, data = relation, message = "添加成功" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "添加视频-演员关联失败");
            return Ok(new { success = false, message = "添加关联失败: " + ex.Message });
        }
    }

    /// <summary>
    /// 删除视频-演员关联
    /// </summary>
    [HttpDelete("{id}")]
    public IActionResult DeleteRelation(string id)
    {
        try
        {
            var rows = _db.ExecuteNonQuery(
                "DELETE FROM VideoActor WHERE id = @id",
                new SqliteParameter("@id", id));

            if (rows > 0)
            {
                return Ok(new { success = true, message = "删除成功" });
            }
            return Ok(new { success = false, message = "关联不存在" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除视频-演员关联失败");
            return Ok(new { success = false, message = "删除关联失败" });
        }
    }

    /// <summary>
    /// 删除视频的所有演员关联
    /// </summary>
    [HttpDelete("video/{videoId}")]
    public IActionResult DeleteByVideo(string videoId)
    {
        try
        {
            var rows = _db.ExecuteNonQuery(
                "DELETE FROM VideoActor WHERE videoid = @videoId",
                new SqliteParameter("@videoId", videoId));

            return Ok(new { success = true, message = $"已删除{rows}个关联" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除视频演员关联失败");
            return Ok(new { success = false, message = "删除关联失败" });
        }
    }

    /// <summary>
    /// 批量设置视频的演员（先删除旧的，再添加新的）
    /// </summary>
    [HttpPost("video/{videoId}/actors")]
    public IActionResult SetVideoActors(string videoId, [FromBody] List<string> actorIds)
    {
        try
        {
            // 先删除旧的关联
            _db.ExecuteNonQuery(
                "DELETE FROM VideoActor WHERE videoid = @videoId",
                new SqliteParameter("@videoId", videoId));

            // 添加新的关联
            if (actorIds != null && actorIds.Count > 0)
            {
                foreach (var actorId in actorIds)
                {
                    var relation = new VideoActor
                    {
                        Id = Guid.NewGuid().ToString(),
                        VideoId = videoId,
                        ActorId = actorId,
                        CTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        UTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                    };

                    var sql = @"
                        INSERT INTO VideoActor (id, videoid, actorid, ctime, utime)
                        VALUES (@id, @videoId, @actorId, @ctime, @utime)";

                    _db.ExecuteNonQuery(sql,
                        new SqliteParameter("@id", relation.Id),
                        new SqliteParameter("@videoId", relation.VideoId),
                        new SqliteParameter("@actorId", relation.ActorId),
                        new SqliteParameter("@ctime", relation.CTime),
                        new SqliteParameter("@utime", relation.UTime)
                    );
                }
            }

            return Ok(new { success = true, message = "设置成功" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "设置视频演员失败");
            return Ok(new { success = false, message = "设置失败: " + ex.Message });
        }
    }
}

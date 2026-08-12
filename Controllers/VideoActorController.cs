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
                SELECT a.* FROM actors a
                INNER JOIN video_actors va ON a.id = va.actor_id
                WHERE va.video_id = @videoId
                ORDER BY a.name";

            var dt = _db.ExecuteDataTable(sql, new SqliteParameter("@videoId", videoId));

            var actors = new List<object>();
            foreach (System.Data.DataRow row in dt.Rows)
            {
                actors.Add(new
                {
                    Id = row["id"]?.ToString(),
                    Name = row["name"]?.ToString(),
                    Alias = row["alias"]?.ToString(),
                    Country = row["country"]?.ToString(),
                    AvatarPath = row["avatar_path"]?.ToString(),
                    Bio = row["bio"]?.ToString(),
                    AddedAt = row["added_at"]?.ToString()
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
                SELECT v.* FROM videos v
                INNER JOIN video_actors va ON v.id = va.video_id
                WHERE va.actor_id = @actorId
                ORDER BY v.added_at DESC";

            var dt = _db.ExecuteDataTable(sql, new SqliteParameter("@actorId", actorId));

            var videos = new List<object>();
            foreach (System.Data.DataRow row in dt.Rows)
            {
                videos.Add(new
                {
                    Id = row["id"]?.ToString(),
                    Code = row["code"]?.ToString(),
                    Name = row["name"]?.ToString(),
                    Category = row["category"]?.ToString(),
                    Country = row["country"]?.ToString(),
                    FilePath = row["file_path"]?.ToString(),
                    FileSize = row["file_size"] != DBNull.Value ? Convert.ToInt64(row["file_size"]) : 0,
                    CoverPath = row["cover_path"]?.ToString(),
                    SeriesId = row["seriesid"]?.ToString(),
                    AddedAt = row["added_at"]?.ToString()
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
    public IActionResult AddRelation([FromBody] VideoActorRelation relation)
    {
        try
        {
            if (string.IsNullOrEmpty(relation.VideoId) || string.IsNullOrEmpty(relation.ActorId))
            {
                return Ok(new { success = false, message = "视频ID和演员ID不能为空" });
            }

            var checkSql = "SELECT COUNT(*) FROM video_actors WHERE video_id = @videoId AND actor_id = @actorId";
            var exists = Convert.ToInt32(_db.ExecuteScalar(checkSql,
                new SqliteParameter("@videoId", relation.VideoId),
                new SqliteParameter("@actorId", relation.ActorId))) > 0;

            if (exists)
            {
                return Ok(new { success = false, message = "该关联已存在" });
            }

            var sql = @"
                INSERT OR IGNORE INTO video_actors (video_id, actor_id, ctime)
                VALUES (@videoId, @actorId, @ctime)";

            _db.ExecuteNonQuery(sql,
                new SqliteParameter("@videoId", relation.VideoId),
                new SqliteParameter("@actorId", relation.ActorId),
                new SqliteParameter("@ctime", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
            );

            return Ok(new { success = true, message = "添加成功" });
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
    [HttpDelete]
    public IActionResult DeleteRelation([FromQuery] string videoId, [FromQuery] string actorId)
    {
        try
        {
            var rows = _db.ExecuteNonQuery(
                "DELETE FROM video_actors WHERE video_id = @videoId AND actor_id = @actorId",
                new SqliteParameter("@videoId", videoId),
                new SqliteParameter("@actorId", actorId));

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
                "DELETE FROM video_actors WHERE video_id = @videoId",
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
            _db.ExecuteNonQuery(
                "DELETE FROM video_actors WHERE video_id = @videoId",
                new SqliteParameter("@videoId", videoId));

            if (actorIds != null && actorIds.Count > 0)
            {
                foreach (var actorId in actorIds)
                {
                    var sql = @"
                        INSERT OR IGNORE INTO video_actors (video_id, actor_id, ctime)
                        VALUES (@videoId, @actorId, @ctime)";

                    _db.ExecuteNonQuery(sql,
                        new SqliteParameter("@videoId", videoId),
                        new SqliteParameter("@actorId", actorId),
                        new SqliteParameter("@ctime", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
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

/// <summary>
/// 视频-演员关联请求模型
/// </summary>
public class VideoActorRelation
{
    public string VideoId { get; set; }
    public string ActorId { get; set; }
}

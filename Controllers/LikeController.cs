using ckapi.Models;
using ckapi.Utils;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace ckapi.Controllers;

/// <summary>
/// 点赞相关接口
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class LikeController : ControllerBase
{
    private readonly ILogger<LikeController> _logger;
    private readonly SQLiteHelper _db;

    public LikeController(ILogger<LikeController> logger, SQLiteHelper db)
    {
        _logger = logger;
        _db = db;
    }

    /// <summary>
    /// 点赞视频
    /// </summary>
    [HttpPost("{videoId}")]
    public IActionResult LikeVideo(string videoId, [FromQuery] string? userToken = null)
    {
        try
        {
            // 检查视频是否存在
            var videoSql = "SELECT id FROM Video WHERE id = @id";
            var videoResult = _db.ExecuteScalar(videoSql, new SqliteParameter("@id", videoId));
            if (videoResult == null)
            {
                return Ok(new { success = false, message = "视频不存在" });
            }

            // 如果有userToken，检查是否已经点赞过
            if (!string.IsNullOrEmpty(userToken))
            {
                var checkSql = "SELECT id FROM LikeRecord WHERE videoid = @videoid AND usertoken = @usertoken";
                var existResult = _db.ExecuteScalar(checkSql,
                    new SqliteParameter("@videoid", videoId),
                    new SqliteParameter("@usertoken", userToken));
                if (existResult != null)
                {
                    return Ok(new { success = false, message = "已经点赞过了" });
                }
            }

            var likeRecord = new LikeRecord
            {
                Id = Guid.NewGuid().ToString(),
                VideoId = videoId,
                LikeTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                UserToken = userToken ?? "",
                CTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                UTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };

            var sql = @"
                INSERT INTO LikeRecord (id, videoid, liketime, usertoken, ctime, utime)
                VALUES (@id, @videoid, @liketime, @usertoken, @ctime, @utime)";

            _db.ExecuteNonQuery(sql,
                new SqliteParameter("@id", likeRecord.Id),
                new SqliteParameter("@videoid", likeRecord.VideoId),
                new SqliteParameter("@liketime", likeRecord.LikeTime),
                new SqliteParameter("@usertoken", likeRecord.UserToken),
                new SqliteParameter("@ctime", likeRecord.CTime),
                new SqliteParameter("@utime", likeRecord.UTime)
            );

            return Ok(new { success = true, data = likeRecord, message = "点赞成功" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "点赞失败");
            return Ok(new { success = false, message = "点赞失败" });
        }
    }

    /// <summary>
    /// 取消点赞
    /// </summary>
    [HttpDelete("{videoId}")]
    public IActionResult UnlikeVideo(string videoId, [FromQuery] string? userToken = null)
    {
        try
        {
            string sql;
            SqliteParameter[] parameters;

            if (!string.IsNullOrEmpty(userToken))
            {
                sql = "DELETE FROM LikeRecord WHERE videoid = @videoid AND usertoken = @usertoken";
                parameters = new[]
                {
                    new SqliteParameter("@videoid", videoId),
                    new SqliteParameter("@usertoken", userToken)
                };
            }
            else
            {
                // 如果没有userToken，只删除最近的一条点赞记录
                sql = @"
                    DELETE FROM LikeRecord 
                    WHERE id = (
                        SELECT id FROM LikeRecord 
                        WHERE videoid = @videoid 
                        ORDER BY ctime DESC 
                        LIMIT 1
                    )";
                parameters = new[] { new SqliteParameter("@videoid", videoId) };
            }

            var result = _db.ExecuteNonQuery(sql, parameters);

            if (result > 0)
            {
                return Ok(new { success = true, message = "取消点赞成功" });
            }
            else
            {
                return Ok(new { success = false, message = "点赞记录不存在" });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "取消点赞失败");
            return Ok(new { success = false, message = "取消点赞失败" });
        }
    }

    /// <summary>
    /// 获取视频的点赞数
    /// </summary>
    [HttpGet("count/{videoId}")]
    public IActionResult GetLikeCount(string videoId)
    {
        try
        {
            var sql = "SELECT COUNT(*) FROM LikeRecord WHERE videoid = @videoid";
            var count = Convert.ToInt32(_db.ExecuteScalar(sql, new SqliteParameter("@videoid", videoId)));

            return Ok(new { success = true, data = count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取点赞数失败");
            return Ok(new { success = false, message = "获取点赞数失败" });
        }
    }

    /// <summary>
    /// 检查是否已点赞
    /// </summary>
    [HttpGet("check/{videoId}")]
    public IActionResult CheckLiked(string videoId, [FromQuery] string? userToken = null)
    {
        try
        {
            if (string.IsNullOrEmpty(userToken))
            {
                return Ok(new { success = true, data = false });
            }

            var sql = "SELECT id FROM LikeRecord WHERE videoid = @videoid AND usertoken = @usertoken";
            var result = _db.ExecuteScalar(sql,
                new SqliteParameter("@videoid", videoId),
                new SqliteParameter("@usertoken", userToken));

            return Ok(new { success = true, data = result != null });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "检查点赞状态失败");
            return Ok(new { success = false, message = "检查点赞状态失败" });
        }
    }
}

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
    /// 点赞视频（video_likes 表：id, video_id, liked_at）
    /// </summary>
    [HttpPost("{videoId}")]
    public IActionResult LikeVideo(string videoId)
    {
        try
        {
            // 检查视频是否存在
            var videoResult = _db.ExecuteScalar(
                "SELECT id FROM videos WHERE id = @id",
                new SqliteParameter("@id", videoId));
            if (videoResult == null)
            {
                return Ok(new { success = false, message = "视频不存在" });
            }

            var likedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            _db.ExecuteNonQuery(
                "INSERT INTO video_likes (video_id, liked_at) VALUES (@videoId, @likedAt)",
                new SqliteParameter("@videoId", videoId),
                new SqliteParameter("@likedAt", likedAt));

            return Ok(new { success = true, message = "点赞成功" });
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
    public IActionResult UnlikeVideo(string videoId)
    {
        try
        {
            // 删除该视频最近一条点赞记录
            var result = _db.ExecuteNonQuery(
                @"DELETE FROM video_likes WHERE id = (
                    SELECT id FROM video_likes WHERE video_id = @videoId ORDER BY liked_at DESC LIMIT 1
                )",
                new SqliteParameter("@videoId", videoId));

            if (result > 0)
                return Ok(new { success = true, message = "取消点赞成功" });
            else
                return Ok(new { success = false, message = "点赞记录不存在" });
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
            var sql = "SELECT COUNT(*) FROM video_likes WHERE video_id = @videoId";
            var count = Convert.ToInt32(_db.ExecuteScalar(sql, new SqliteParameter("@videoId", videoId)));

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
    public IActionResult CheckLiked(string videoId)
    {
        try
        {
            var sql = "SELECT id FROM video_likes WHERE video_id = @videoId LIMIT 1";
            var result = _db.ExecuteScalar(sql, new SqliteParameter("@videoId", videoId));

            return Ok(new { success = true, data = result != null });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "检查点赞状态失败");
            return Ok(new { success = false, message = "检查点赞状态失败" });
        }
    }
}

using ckapi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace ckapi.Controllers;

/// <summary>
/// 影片推荐
/// 路由前缀保持 api/video，与前端既有调用一致
/// </summary>
[ApiController]
[Route("api/video")]
public class VideoRecommendationController : ControllerBase
{
    private readonly IConfiguration _config;
    private readonly ILogger<VideoRecommendationController> _logger;

    public VideoRecommendationController(IConfiguration config, ILogger<VideoRecommendationController> logger)
    {
        _config = config;
        _logger = logger;
    }

    private SqliteConnection GetConnection()
    {
        return new SqliteConnection(_config.GetConnectionString("DefaultConnection"));
    }

    /// <summary>
    /// 获取推荐影片
    /// </summary>
    [HttpGet("{id}/recommend")]
    public IActionResult GetRecommend(string id, [FromQuery] int limit = 8)
    {
        try
        {
            limit = Utils.Paging.ClampCount(limit, 8);
            using var conn = GetConnection();
            conn.Open();

            // 获取当前影片信息
            var currentVideo = new { seriesId = "", actorIds = new List<string>() };
            using (var cmd = new SqliteCommand("SELECT seriesid FROM videos WHERE id = @id", conn))
            {
                cmd.Parameters.Add(new SqliteParameter("@id", id));
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    currentVideo = new {
                        seriesId = reader["seriesid"] == DBNull.Value ? "" : reader["seriesid"].ToString() ?? "",
                        actorIds = new List<string>()
                    };
                }
            }

            // 获取当前影片的演员
            using (var actorCmd = new SqliteCommand("SELECT actor_id FROM video_actors WHERE video_id = @videoId", conn))
            {
                actorCmd.Parameters.Add(new SqliteParameter("@videoId", id));
                using var actorReader = actorCmd.ExecuteReader();
                while (actorReader.Read())
                {
                    var actorId = actorReader["actor_id"].ToString();
                    if (!string.IsNullOrEmpty(actorId)) currentVideo.actorIds.Add(actorId);
                }
            }

            var recommendList = new List<object>();
            var excludeIds = new HashSet<string> { id };

            // 1. 优先获取同系列影片（不限制数量）
            if (!string.IsNullOrEmpty(currentVideo.seriesId))
            {
                var seriesSql = $@"
                    SELECT {VideoCardQuery.ColumnsWithSeries}
                    FROM videos v
                    LEFT JOIN video_series s ON v.seriesid = s.id
                    WHERE v.seriesid = @seriesId AND v.id != @currentId AND v.file_size > 0
                    ORDER BY RANDOM()
                    LIMIT 20";

                using var seriesCmd = new SqliteCommand(seriesSql, conn);
                seriesCmd.Parameters.Add(new SqliteParameter("@seriesId", currentVideo.seriesId));
                seriesCmd.Parameters.Add(new SqliteParameter("@currentId", id));
                using var seriesReader = seriesCmd.ExecuteReader();
                while (seriesReader.Read())
                {
                    var vid = seriesReader["id"].ToString() ?? "";
                    if (!excludeIds.Contains(vid))
                    {
                        recommendList.Add(VideoCardQuery.Map(seriesReader));
                        excludeIds.Add(vid);
                    }
                }
            }

            // 2. 如果同系列影片不够，获取同演员影片
            if (recommendList.Count < limit && currentVideo.actorIds.Count > 0)
            {
                // 参数化 IN 子句，避免字符串拼接造成 SQL 注入
                var actorIdParams = currentVideo.actorIds
                    .Select((a, i) => new SqliteParameter($"@actorId{i}", a))
                    .ToArray();
                var actorIdPlaceholders = string.Join(",", actorIdParams.Select(p => p.ParameterName));
                var actorSql = $@"
                    SELECT DISTINCT {VideoCardQuery.ColumnsWithSeries}
                    FROM videos v
                    INNER JOIN video_actors va ON v.id = va.video_id
                    LEFT JOIN video_series s ON v.seriesid = s.id
                    WHERE va.actor_id IN ({actorIdPlaceholders}) AND v.file_size > 0
                    ORDER BY RANDOM()
                    LIMIT 50";

                using var actorCmd = new SqliteCommand(actorSql, conn);
                actorCmd.Parameters.AddRange(actorIdParams);
                using var actorReader = actorCmd.ExecuteReader();
                while (actorReader.Read() && recommendList.Count < limit)
                {
                    var vid = actorReader["id"].ToString() ?? "";
                    if (!excludeIds.Contains(vid))
                    {
                        recommendList.Add(VideoCardQuery.Map(actorReader));
                        excludeIds.Add(vid);
                    }
                }
            }

            // 3. 如果还不够，随机获取其他影片
            if (recommendList.Count < limit)
            {
                var randomSql = $@"
                    SELECT {VideoCardQuery.ColumnsWithSeries}
                    FROM videos v
                    LEFT JOIN video_series s ON v.seriesid = s.id
                    WHERE v.file_size > 0
                    ORDER BY RANDOM()
                    LIMIT 50";

                using var randomCmd = new SqliteCommand(randomSql, conn);
                using var randomReader = randomCmd.ExecuteReader();
                while (randomReader.Read() && recommendList.Count < limit)
                {
                    var vid = randomReader["id"].ToString() ?? "";
                    if (!excludeIds.Contains(vid))
                    {
                        recommendList.Add(VideoCardQuery.Map(randomReader));
                        excludeIds.Add(vid);
                    }
                }
            }

            return Ok(new { success = true, data = recommendList });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取推荐影片失败");
            return Ok(new { success = false, message = "获取推荐影片失败" });
        }
    }
}

using ckapi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using System.Text.Json.Serialization;

namespace ckapi.Controllers;

/// <summary>
/// 片源两维（字幕 / 广告水印）的标记，与分辨率扫描。
/// 路由前缀仍是 api/video，与前端既有调用一致。
/// </summary>
[ApiController]
[Route("api/video")]
public class VideoSourceController : ControllerBase
{
    private readonly ILogger<VideoSourceController> _logger;
    private readonly Utils.SQLiteHelper _db;
    private readonly SourceScanner _scanner;
    private readonly SourceScanJob _job;

    public VideoSourceController(
        ILogger<VideoSourceController> logger, Utils.SQLiteHelper db,
        SourceScanner scanner, SourceScanJob job)
    {
        _logger = logger;
        _db = db;
        _scanner = scanner;
        _job = job;
    }

    /// <summary>
    /// 标记片源。两维各自独立，传哪一维改哪一维；把某一维改回 unknown 就是清掉这个结论。
    /// 旧的 media-flags 接口在"已设置"时会拒绝再改，这里不设这个限制——
    /// 两维分开之后重标是常态，拦人只会让人去改数据库。
    /// </summary>
    [HttpPut("{id}/source")]
    public IActionResult UpdateSource(string id, [FromBody] UpdateSourceRequest req)
    {
        try
        {
            if (req.Subtitle is not null && !Utils.SourceStates.IsSubtitle(req.Subtitle))
                return Ok(new { success = false, message = $"subtitle 取值不对：{req.Subtitle}" });
            if (req.Watermark is not null && !Utils.SourceStates.IsWatermark(req.Watermark))
                return Ok(new { success = false, message = $"watermark 取值不对：{req.Watermark}" });
            if (req.Subtitle is null && req.Watermark is null)
                return Ok(new { success = false, message = "没有要改的维度" });

            using var conn = _db.GetConnection();
            conn.Open();

            const string update = @"
                UPDATE videos
                SET subtitle_state  = COALESCE(@subtitle, subtitle_state),
                    watermark_state = COALESCE(@watermark, watermark_state),
                    watched = CASE WHEN COALESCE(NULLIF(@subtitle, 'unknown'),
                                                 NULLIF(@watermark, 'unknown')) IS NOT NULL
                                   THEN 1 ELSE watched END
                WHERE id = @id";

            using (var cmd = new SqliteCommand(update, conn))
            {
                cmd.Parameters.AddWithValue("@subtitle", (object?)req.Subtitle ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@watermark", (object?)req.Watermark ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@id", id);
                if (cmd.ExecuteNonQuery() == 0)
                    return NotFound(new { success = false, message = "视频不存在" });
            }

            // 两维都清回未标，就当这次结论没给过：今日推荐要能重新推出来
            using (var clear = new SqliteCommand(@"
                UPDATE videos SET watched = 0
                WHERE id = @id AND subtitle_state = 'unknown' AND watermark_state = 'unknown'", conn))
            {
                clear.Parameters.AddWithValue("@id", id);
                clear.ExecuteNonQuery();
            }

            using var read = new SqliteCommand(
                "SELECT subtitle_state, watermark_state, watched FROM videos WHERE id = @id", conn);
            read.Parameters.AddWithValue("@id", id);
            using var reader = read.ExecuteReader();
            if (!reader.Read()) return NotFound(new { success = false, message = "视频不存在" });

            return Ok(new
            {
                success = true,
                message = "片源标记已更新",
                data = new
                {
                    subtitleState = reader.GetString(0),
                    watermarkState = reader.GetString(1),
                    watched = reader.GetInt32(2) == 1
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UpdateSource failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>扫描单条：读容器头拿分辨率，顺带判断有没有字幕</summary>
    [HttpPost("{id}/scan")]
    public IActionResult ScanOne(string id)
    {
        try
        {
            using var conn = _db.GetConnection();
            conn.Open();

            var ins = _scanner.ScanOne(conn, id);
            if (!ins.Ok) return Ok(new { success = false, message = ins.Error ?? "扫描失败" });

            using var read = new SqliteCommand(
                "SELECT subtitle_state, watermark_state FROM videos WHERE id = @id", conn);
            read.Parameters.AddWithValue("@id", id);
            using var reader = read.ExecuteReader();
            var (sub, mark) = reader.Read()
                ? (reader.GetString(0), reader.GetString(1))
                : (ins.SetSubtitle ? "has" : "unknown", "unknown");

            return Ok(new
            {
                success = true,
                message = $"{ins.Width}×{ins.Height}" + (ins.SetSubtitle ? "，并检测到字幕" : ""),
                data = new
                {
                    resW = ins.Width,
                    resH = ins.Height,
                    codec = ins.Codec,
                    subtitleDetected = ins.Subtitle,
                    subtitleState = sub,
                    watermarkState = mark
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ScanOne failed for {Id}", id);
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>
    /// 启动全站扫描。force=false 只量没扫过的，force=true 全库重来一遍。
    /// 立刻返回，进度看 GET scan/status。
    /// </summary>
    [HttpPost("scan/all")]
    public IActionResult StartScan([FromQuery] bool force = false)
    {
        var (started, message) = _job.Start(force);
        return Ok(started
            ? new { success = true, message, data = _job.Status() }
            : new { success = false, message, data = _job.Status() });
    }

    /// <summary>扫描进度</summary>
    [HttpGet("scan/status")]
    public IActionResult ScanStatus() => Ok(new { success = true, data = _job.Status() });

    /// <summary>请求停止扫描（正在处理的那一批会跑完）</summary>
    [HttpDelete("scan/all")]
    public IActionResult StopScan()
    {
        if (!_job.IsRunning)
            return Ok(new { success = false, message = "现在没有在跑的任务", data = _job.Status() });

        _job.Stop();
        return Ok(new { success = true, message = "已请求停止", data = _job.Status() });
    }

    public sealed class UpdateSourceRequest
    {
        /// <summary>字幕情况；null 表示这一维不动</summary>
        [JsonPropertyName("subtitle")]
        public string? Subtitle { get; set; }

        /// <summary>广告水印；null 表示这一维不动</summary>
        [JsonPropertyName("watermark")]
        public string? Watermark { get; set; }
    }
}

using ckapi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using System.Text.Json.Serialization;

namespace ckapi.Controllers;

/// <summary>
/// 全站片源扫描任务的开关与进度。
/// 路由前缀仍是 api/video，与前端既有调用一致。
///
/// 单条操作（某一版的两维标记、重扫这一版）在 v11 挪到了 api/video/version/* ——
/// 分辨率与两维都是"这一份文件"的属性，作用对象是版本行而不是影片。
/// </summary>
[ApiController]
[Route("api/video")]
public class VideoSourceController : ControllerBase
{
    private readonly ILogger<VideoSourceController> _logger;
    private readonly Utils.SQLiteHelper _db;
    private readonly SourceScanJob _job;

    public VideoSourceController(
        ILogger<VideoSourceController> logger, Utils.SQLiteHelper db, SourceScanJob job)
    {
        _logger = logger;
        _db = db;
        _job = job;
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
}

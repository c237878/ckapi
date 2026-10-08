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
    private readonly FileRelocateJob _relocate;

    public VideoSourceController(
        ILogger<VideoSourceController> logger, Utils.SQLiteHelper db, SourceScanJob job,
        FileRelocateJob relocate)
    {
        _logger = logger;
        _db = db;
        _job = job;
        _relocate = relocate;
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

    // ------------------------------------------------------------ 搬移文件到别的目录

    public sealed record RelocateRequest(List<string>? FileIds, string? Dest, string? Mode);

    /// <summary>
    /// 把选中的版本文件搬到目标目录（配合"拷到新设备重编码"）。
    ///
    /// **默认 dryRun=true 只出预检清单**：这是不可逆的批量文件动作，先让人看清
    /// "要动哪几个、合计多大、哪几个不能动以及为什么"，点了执行才带 ?dryRun=false。
    /// 执行接口和改名/删除那批一样挂管理口令门禁。
    /// </summary>
    [HttpPost("relocate")]
    [Utils.AdminToken]
    public IActionResult Relocate([FromBody] RelocateRequest req, [FromQuery] bool dryRun = true)
    {
        try
        {
            var copy = string.Equals(req.Mode, "copy", StringComparison.OrdinalIgnoreCase);
            var ids = req.FileIds ?? new List<string>();

            if (dryRun)
            {
                var (rows, problems) = FileRelocateJob.Plan(_db, ids, req.Dest, copy);
                return Ok(new
                {
                    success = problems.Count == 0,
                    message = problems.Count > 0 ? string.Join("；", problems) : $"预检完成：可动 {rows.Count(r => r.Ok)} 个",
                    data = new
                    {
                        rows = rows.Select(r => new
                        {
                            fileId = r.FileId, videoCode = r.VideoCode, fileCode = r.FileCode,
                            path = r.Path, size = r.Size, target = r.Target, ok = r.Ok, reason = r.Reason
                        }),
                        okCount = rows.Count(r => r.Ok),
                        skipCount = rows.Count(r => !r.Ok),
                        totalSize = rows.Where(r => r.Ok).Sum(r => r.Size),
                        problems,
                        mode = copy ? "copy" : "move"
                    }
                });
            }

            var (started, message) = _relocate.Start(_db, ids, req.Dest, copy);
            return Ok(new { success = started, message, data = _relocate.Status() });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "搬移文件失败");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>搬移进度</summary>
    [HttpGet("relocate/status")]
    public IActionResult RelocateStatus() => Ok(new { success = true, data = _relocate.Status() });

    /// <summary>请求停止搬移（手上那个文件弄完就停，不会半路掐断一个正在复制的文件）</summary>
    [HttpDelete("relocate")]
    public IActionResult StopRelocate()
    {
        if (!_relocate.IsRunning)
            return Ok(new { success = false, message = "现在没有在跑的搬移任务", data = _relocate.Status() });

        _relocate.Stop();
        return Ok(new { success = true, message = "已请求停止", data = _relocate.Status() });
    }
}

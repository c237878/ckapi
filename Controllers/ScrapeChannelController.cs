using ckapi.Services;
using Microsoft.AspNetCore.Mvc;

namespace ckapi.Controllers;

/// <summary>
/// 抓取通道的配置接口：列表 / 保存 / 删除 / 清冷却 / 试抓 / 单条应用 / 批量补。
///
/// 试抓是这个界面的重点：他改完地址模板与规则，当场就能看到抽出了什么，
/// 不用改一版部署一次。试抓一个字都不写库。
/// 批量补是试抓验证过之后的那一步，跑在后台线程上，接口只负责起、看、停。
/// </summary>
[ApiController]
[Route("api/scrape/channel")]
public class ScrapeChannelController : ControllerBase
{
    private readonly ILogger<ScrapeChannelController> _logger;
    private readonly ScrapeChannelService _channels;
    private readonly ChannelBatch _batch;

    public ScrapeChannelController(ILogger<ScrapeChannelController> logger,
        ScrapeChannelService channels, ChannelBatch batch)
    {
        _logger = logger;
        _channels = channels;
        _batch = batch;
    }

    /// <summary>通道列表。状态字段（能不能问、为什么不能）由服务端算好，前端不重复判</summary>
    [HttpGet]
    public IActionResult List()
    {
        try
        {
            var data = _channels.List().Select(c =>
            {
                var gate = _channels.Check(c);
                return new
                {
                    id = c.Id,
                    c.Name,
                    c.Entity,
                    c.Enabled,
                    c.QuerySource,
                    c.FetchUrl,
                    c.FetchKind,
                    c.Referer,
                    c.UserAgent,
                    c.Rules,
                    c.IdentityRegex,
                    c.MinIntervalMs,
                    c.DailyQuota,
                    c.FailLimit,
                    c.CooldownMinutes,
                    c.Note,
                    c.UsedToday,
                    c.UsedDate,
                    c.ConsecutiveFails,
                    c.BlockedUntil,
                    c.LastError,
                    c.LastOkAt,
                    usable = gate.Allowed,
                    why = gate.Allowed ? "可以问" : gate.Why,
                    // "还能补多少条"：批量按钮上要显示影响条数，点了才知道要跑多久
                    missingCount = _channels.MissingCount(c),
                    quotaLeft = _channels.RemainingQuota(c)
                };
            }).ToList();
            return Ok(new { success = true, data, targets = ScrapeChannelService.Targets });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "List channels failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    [HttpPut]
    public IActionResult Save([FromBody] ScrapeChannelService.Channel c)
    {
        try
        {
            var (ok, message) = _channels.Save(c);
            return Ok(new { success = ok, message, data = ok ? new { id = c.Id } : null });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Save channel failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    [Utils.AdminToken]
    [HttpDelete("{id}")]
    public IActionResult Delete(string id)
    {
        try
        {
            return Ok(_channels.Delete(id)
                ? new { success = true, message = "通道已删除" }
                : NotFound(new { success = false, message = "通道不存在" }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Delete channel failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>清掉冷却与当日计数：换了 IP、或确认对方已放行时用</summary>
    [Utils.AdminToken]
    [HttpPost("{id}/reset")]
    public IActionResult Reset(string id)
    {
        try
        {
            _channels.Reset(id);
            return Ok(new { success = true, message = "已清除冷却与今日计数" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reset channel failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>一键开关：站点翻脸时先关掉，比进编辑对话框翻一圈快</summary>
    [HttpPost("{id}/toggle")]
    public IActionResult Toggle(string id)
    {
        try
        {
            var on = _channels.Toggle(id);
            if (on is null) return NotFound(new { success = false, message = "通道不存在" });
            return Ok(new { success = true, message = on.Value ? "已启用，会继续向这个站点发请求" : "已停用，抓取遇到它会直接跳过" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Toggle channel failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>
    /// 试抓：按给定查询词（或某条真实记录）走一遍"取 + 抽"，只回结果不写库。
    /// 走的是真请求，所以同样受开关/冷却/配额约束——界面里想试抓被拦的通道，先点「清除冷却」。
    /// </summary>
    [HttpPost("{id}/test")]
    public async Task<IActionResult> Test(string id, [FromQuery] string? q = null, [FromQuery] string? entityId = null)
    {
        var c = _channels.Get(id);
        if (c is null) return NotFound(new { success = false, message = "通道不存在" });

        var gate = _channels.Check(c);
        if (!gate.Allowed) return Ok(new { success = false, message = gate.Why });

        var query = q;
        if (string.IsNullOrWhiteSpace(query) && !string.IsNullOrWhiteSpace(entityId))
            query = _channels.QueryValueFor(c, entityId);
        if (string.IsNullOrWhiteSpace(query))
            return Ok(new { success = false, message = "要给定查询词或某条真实记录" });

        try
        {
            await _channels.WaitTurnAsync(c, HttpContext.RequestAborted);
            var (ok, message, found) = await _channels.FetchAsync(c, query, HttpContext.RequestAborted);
            // 试抓也计入配额：它是真敲了对方一下的，不计的话配额就成了只防自己人的摆设
            _channels.Report(c.Id, ok, ok ? null : message);
            return Ok(new
            {
                success = ok,
                message,
                data = new { query, found, builtin = c.FetchKind == "builtin" }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Test channel failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>对某一条记录走一遍通道并落库（只填空值那条规矩照旧）</summary>
    [HttpPost("{id}/apply")]
    public async Task<IActionResult> Apply(string id, [FromQuery] string entityId, CancellationToken ct)
    {
        var c = _channels.Get(id);
        if (c is null) return NotFound(new { success = false, message = "通道不存在" });
        if (string.IsNullOrWhiteSpace(entityId)) return Ok(new { success = false, message = "要指定 entityId" });

        var gate = _channels.Check(c);
        if (!gate.Allowed) return Ok(new { success = false, message = gate.Why });

        try
        {
            var (written, skipped) = await _channels.ApplyAsync(id, entityId, ct);
            return Ok(new
            {
                success = true,
                message = $"写入 {written} 项" + (skipped.Count > 0 ? $"，跳过 {skipped.Count} 项" : ""),
                data = new { written, skipped }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Apply channel failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    // ---------------------------------------------------------------- 批量补数据

    /// <summary>
    /// 起一轮批量：把这条通道能补的字段缺着的记录，按它自己的间隔/配额/熔断一条条问过去。
    /// 接口立刻返回，进度靠 <see cref="BatchStatus"/> 轮询——一次问几百条要几分钟，
    /// 挂在一个 HTTP 请求上必然被中途断掉。
    /// </summary>
    [HttpPost("{id}/batch")]
    public IActionResult StartBatch(string id, [FromQuery] int limit = 50)
    {
        try
        {
            var (started, message) = _batch.Start(id, limit);
            return Ok(new { success = started, message, data = _batch.Current });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Start batch failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>当前或最后一轮批量的进度。没有则回 null，前端按"没在跑"处理</summary>
    [HttpGet("batch/status")]
    public IActionResult BatchStatus() => Ok(new { success = true, data = _batch.Current });

    /// <summary>停止批量：正在处理的那一条会做完，不会半路掐断请求</summary>
    [HttpPost("batch/stop")]
    public IActionResult StopBatch()
    {
        _batch.Stop();
        return Ok(new { success = true, message = "已请求停止", data = _batch.Current });
    }
}

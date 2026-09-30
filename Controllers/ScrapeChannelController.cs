using ckapi.Services;
using Microsoft.AspNetCore.Mvc;

namespace ckapi.Controllers;

/// <summary>
/// 抓取通道的配置接口：列表 / 保存 / 删除 / 清冷却 / 试抓 / 单条应用。
///
/// 试抓是这个界面的重点：他改完地址模板与规则，当场就能看到抽出了什么，
/// 不用改一版部署一次。试抓一个字都不写库。
/// </summary>
[ApiController]
[Route("api/scrape/channel")]
public class ScrapeChannelController : ControllerBase
{
    private readonly ILogger<ScrapeChannelController> _logger;
    private readonly ScrapeChannelService _channels;

    public ScrapeChannelController(ILogger<ScrapeChannelController> logger, ScrapeChannelService channels)
    {
        _logger = logger;
        _channels = channels;
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
                    why = gate.Allowed ? "可以问" : gate.Why
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
    public IActionResult Apply(string id, [FromQuery] string entityId)
    {
        var c = _channels.Get(id);
        if (c is null) return NotFound(new { success = false, message = "通道不存在" });
        if (string.IsNullOrWhiteSpace(entityId)) return Ok(new { success = false, message = "要指定 entityId" });

        var gate = _channels.Check(c);
        if (!gate.Allowed) return Ok(new { success = false, message = gate.Why });

        try
        {
            var (written, skipped) = _channels.Apply(id, entityId);
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

}

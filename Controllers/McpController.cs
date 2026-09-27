using ckapi.Services;
using Microsoft.AspNetCore.Mvc;

namespace ckapi.Controllers;

/// <summary>
/// MCP 端点：大模型按 JSON-RPC 2.0 调工具，走同一个 /mcp 地址 + X-API-Key 头。
///
/// 另外两个是设置页用的管理接口——查状态与换密钥。
/// </summary>
[ApiController]
[Route("api/mcp")]
public class McpController : ControllerBase
{
    private readonly ILogger<McpController> _logger;
    private readonly McpService _mcp;

    public McpController(ILogger<McpController> logger, McpService mcp)
    {
        _logger = logger;
        _mcp = mcp;
    }

    /// <summary>协议入口。GET/DELETE 按规范回 405：本服务端不往客户端推事件流。</summary>
    [HttpPost("/mcp")]
    public async Task<IActionResult> Handle()
    {
        if (!_mcp.Authorized(Request.Headers["X-API-Key"]))
            return Unauthorized(Gate());

        using var reader = new StreamReader(Request.Body);
        var raw = await reader.ReadToEndAsync();
        var outcome = _mcp.Handle(raw);

        return outcome.Body is null ? StatusCode(outcome.Status) : new JsonResult(outcome.Body);
    }

    [HttpGet("/mcp")]
    [HttpDelete("/mcp")]
    public IActionResult StreamUnsupported() => StatusCode(405, new { error = "这个端点只收 POST，不提供 SSE 事件流" });

    /// <summary>设置页：开关状态、当前密钥、地址、工具清单</summary>
    [HttpGet("status")]
    public IActionResult Status()
    {
        try
        {
            var key = _mcp.ApiKey();
            return Ok(new
            {
                success = true,
                data = new
                {
                    enabled = !string.IsNullOrEmpty(key),
                    key,
                    endpoint = $"{Request.Scheme}://{Request.Host}/mcp",
                    tools = _mcp.ToolNames()
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MCP status failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>生成新密钥。旧密钥当场失效，已经配好的客户端要一起换。</summary>
    [HttpPost("key")]
    public IActionResult Rotate()
    {
        try
        {
            var key = _mcp.RotateKey();
            return Ok(new { success = true, message = "新密钥已生成，旧的立即失效", data = new { key } });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MCP key rotation failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    private object Gate() => new
    {
        error = new { code = -32001, message = "缺少或不对的 X-API-Key：先到 设置 → AI 接口 生成密钥" }
    };
}

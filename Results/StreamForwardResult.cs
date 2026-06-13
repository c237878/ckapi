using System.Net;
using System.Net.Http;
using Microsoft.AspNetCore.Mvc;

namespace ckapi.Results;

/// <summary>
/// 将 HttpContent 流式转发到响应体，支持大文件
/// </summary>
public class StreamForwardResult : IActionResult
{
    private readonly HttpContent _content;
    private readonly HttpResponseMessage? _httpResponse;
    private readonly string _contentType;

    public StreamForwardResult(HttpContent content, string contentType, HttpResponseMessage? httpResponse = null)
    {
        _content = content;
        _contentType = contentType;
        _httpResponse = httpResponse;
    }

    public async Task ExecuteResultAsync(ActionContext context)
    {
        var httpContext = context.HttpContext;
        var response = httpContext.Response;
        response.ContentType = _contentType;

        var contentLength = _content.Headers.ContentLength;
        if (contentLength.HasValue)
            response.ContentLength = contentLength.Value;

        response.StatusCode = (int)HttpStatusCode.OK;

        try
        {
            // 直接写入响应体，ASP.NET Core 负责流管理
            await _content.CopyToAsync(response.Body, httpContext.RequestAborted);
        }
        finally
        {
            _httpResponse?.Dispose();
            _content.Dispose();
        }
    }
}
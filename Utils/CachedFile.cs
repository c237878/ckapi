using Microsoft.AspNetCore.Mvc;

namespace ckapi.Utils;

/// <summary>
/// 静态媒体文件的条件响应。
///
/// 封面/海报原先不带任何 Cache-Control，浏览器每次进页面都要重新回源；而这些文件位于
/// 挂载卷上，冷读一次可能耗时十几秒，等于每次浏览都付一遍磁盘等待。
/// 这里用文件的修改时间+长度生成 ETag，命中就直接 304，连文件都不用打开。
/// </summary>
public static class CachedFile
{
    private const string OneWeek = "public, max-age=604800";

    /// <summary>
    /// 返回给定文件的响应；客户端缓存有效时返回 304。文件不存在时返回 null 交给调用方处理。
    /// </summary>
    public static IActionResult? TryServe(ControllerBase controller, string? path, string? contentTypeOverride = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0) return null;

        var etag = $"\"{info.LastWriteTimeUtc.Ticks:x}-{info.Length:x}\"";
        var http = controller.HttpContext;

        http.Response.Headers.CacheControl = OneWeek;

        if (http.Request.Headers.IfNoneMatch == etag)
            return controller.StatusCode(StatusCodes.Status304NotModified);

        http.Response.Headers.ETag = etag;
        var contentType = contentTypeOverride ?? SafePath.ImageContentType(path) ?? "application/octet-stream";

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return controller.File(stream, contentType);
    }
}

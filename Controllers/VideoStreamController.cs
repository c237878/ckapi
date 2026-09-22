using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace ckapi.Controllers;

/// <summary>
/// 视频流媒体 / 封面 / 字幕代理
/// 路由前缀保持 api/video，与前端既有调用完全一致
/// </summary>
[ApiController]
[Route("api/video")]
public class VideoStreamController : ControllerBase
{
    private readonly IConfiguration _config;
    private readonly ILogger<VideoStreamController> _logger;

    public VideoStreamController(IConfiguration config, ILogger<VideoStreamController> logger)
    {
        _config = config;
        _logger = logger;
    }

    private SqliteConnection GetConnection()
    {
        return new SqliteConnection(_config.GetConnectionString("DefaultConnection"));
    }

    /// <summary>
    /// 视频流代理（支持 Range 请求，可拖动进度条）
    /// </summary>
    [HttpGet("stream/{id}")]
    public IActionResult StreamVideo(string id)
    {
        try
        {
            var sql = "SELECT file_path FROM videos WHERE id = @id";
            using var conn = GetConnection();
            conn.Open();

            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.Add(new SqliteParameter("@id", id));

            var filePath = cmd.ExecuteScalar()?.ToString();
            if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath))
                return NotFound(new { success = false, message = "视频文件不存在" });

            var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var response = File(fileStream, "video/mp4", enableRangeProcessing: true);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StreamVideo failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>
    /// 封面代理
    /// </summary>
    [HttpGet("cover/{id}")]
    public IActionResult GetCover(string id)
    {
        try
        {
            var sql = "SELECT cover_path FROM videos WHERE id = @id";
            using var conn = GetConnection();
            conn.Open();

            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.Add(new SqliteParameter("@id", id));

            var coverPath = cmd.ExecuteScalar()?.ToString();

            var result = Utils.CachedFile.TryServe(this, coverPath, "image/jpeg");
            if (result is not null) return result;

            return NotFound(new { success = false, message = "封面不存在" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetCover failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>
    /// 检查字幕是否存在
    /// </summary>
    [HttpGet("{code}/subtitle/check")]
    public IActionResult CheckSubtitle(string code)
    {
        try
        {
            var sql = "SELECT path FROM scan_directories WHERE category = '字幕' LIMIT 1";
            using var conn = GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand(sql, conn);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return NotFound(new { success = false, message = "视频不存在" });
            var filePath = reader["path"]?.ToString();
            if (string.IsNullOrEmpty(filePath))
                return Ok(new { success = true, hasSubtitle = false });

            var subPath = FindSubtitleFile(filePath, code);
            if (subPath != null)
            {
                var ext = Path.GetExtension(subPath).ToLower();
                var ct = ext == ".vtt" ? "text/vtt" : "text/plain";
                return Ok(new { success = true, hasSubtitle = true, url = $"/api/video/{code}/subtitle", contentType = ct, ext });
            }
            return Ok(new { success = true, hasSubtitle = false });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CheckSubtitle failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>
    /// 字幕流代理
    /// </summary>
    [HttpGet("{code}/subtitle")]
    public IActionResult GetSubtitle(string code)
    {
        try
        {
            var sql = "SELECT path FROM scan_directories WHERE category = '字幕' LIMIT 1";
            using var conn = GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand(sql, conn);
            var filePath = cmd.ExecuteScalar()?.ToString();
            if (string.IsNullOrEmpty(filePath))
                return NotFound(new { success = false, message = "字幕目录不存在" });

            var subPath = FindSubtitleFile(filePath, code);
            if (subPath == null || !System.IO.File.Exists(subPath))
                return NotFound(new { success = false, message = "字幕不存在" });

            var ext = Path.GetExtension(subPath).ToLower();
            var contentType = ext == ".vtt" ? "text/vtt" : "text/plain";
            var fileStream = new FileStream(subPath, FileMode.Open, FileAccess.Read);
            return File(fileStream, contentType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetSubtitle failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>
    /// 根据视频路径查找字幕文件：同番号.*
    /// </summary>
    private string? FindSubtitleFile(string path, string code)
    {
        try
        {
            var subDir = Path.GetFullPath(path);
            if (string.IsNullOrEmpty(subDir)) return null;
            if (!Directory.Exists(subDir)) return null;

            var extensions = new[] { ".srt", ".ass", ".ssa", ".vtt", ".sub" };
            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(subDir, code + ext);
                if (System.IO.File.Exists(candidate)) return candidate;
            }
            // 也尝试不带扩展名的同名文件
            var direct = Path.Combine(subDir, code);
            if (System.IO.File.Exists(direct)) return direct;
            return null;
        }
        catch { return null; }
    }
}

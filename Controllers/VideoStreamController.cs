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
    private readonly Utils.SQLiteHelper _db;

    public VideoStreamController(IConfiguration config, ILogger<VideoStreamController> logger, Utils.SQLiteHelper db)
    {
        _config = config;
        _logger = logger;
        _db = db;
    }

    /// <summary>
    /// 走 SQLiteHelper 而不是自己 new 连接：连接级参数（busy_timeout / synchronous）在那里统一设。
    /// 这里原先是手写 `new SqliteConnection(连接串)`，等于这一个控制器的连接全都拿不到默认参数——
    /// 而它恰恰是最常被访问的（每个封面、每段流都过它）。
    /// </summary>
    private SqliteConnection GetConnection() => _db.GetConnection();

    /// <summary>
    /// 视频流代理（支持 Range 请求，可拖动进度条）。
    /// 参数是**版本行 id**（v11）：前端下拉选中的是哪一版，就播哪一版的文件。
    /// </summary>
    [HttpGet("stream/{fileId}")]
    public IActionResult StreamVideo(string fileId)
    {
        try
        {
            var sql = "SELECT file_path FROM video_files WHERE id = @id";
            using var conn = GetConnection();
            conn.Open();

            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.Add(new SqliteParameter("@id", fileId));

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
    /// 封面缩略图（size 只认 Thumbs 里那两档：s=160 / m=400）。
    ///
    /// 为什么要：卡片画框只有一两百像素宽，却去拉一张中位 142KB、p99 2.7MB 的原图，
    /// 而这些图在挂载卷上——一屏 24 张就是 3MB 加 24 次网络往返。
    /// 复用演员图那套 Thumbs：按需生成、落本地缓存盘（不是媒体卷）、按源文件 mtime 失效，
    /// 换封面不需要任何清缓存动作。
    ///
    /// 原图那条路由（cover/{id}）保留：详情页大图与"另存封面"要的是原画质。
    /// </summary>
    [HttpGet("cover/{id}/{size}")]
    public IActionResult GetCoverThumb(string id, string size)
    {
        try
        {
            var width = Utils.Thumbs.WidthOf(size);
            if (width == 0) return NotFound(new { success = false, message = "没有这个缩略图档位" });

            string? coverPath;
            using (var conn = GetConnection())
            {
                conn.Open();
                using var cmd = new SqliteCommand("SELECT cover_path FROM videos WHERE id = @id", conn);
                cmd.Parameters.Add(new SqliteParameter("@id", id));
                coverPath = cmd.ExecuteScalar()?.ToString();
            }

            if (string.IsNullOrWhiteSpace(coverPath)) return NotFound(new { success = false, message = "封面不存在" });

            var name = Path.GetFileName(coverPath);
            var thumb = Utils.Thumbs.Ensure(coverPath,
                Utils.Thumbs.CacheRoot(_config, _db.GetDbPath()), "cover", name, width, out _, out _);

            // 解码不了的就退回原图（损坏、或没编进来的格式）：页面至少还有东西可看，
            // 而不是因为一个缩略图生成失败就空一块
            var result = thumb is null
                ? Utils.CachedFile.TryServe(this, coverPath, "image/jpeg")
                : Utils.CachedFile.TryServe(this, thumb.Value.Path, "image/webp");

            return result ?? NotFound(new { success = false, message = "封面读不出来" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetCoverThumb failed id={Id} size={Size}", id, size);
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

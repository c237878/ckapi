using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace ckapi.Controllers;

/// <summary>
/// 艳图（照片墙）：图片放在 &lt;艳图目录&gt;/default/ 下，清单落在 highlight_images 表里。
/// 与演员图片同一套口径 —— 开页只读表，扫盘由界面上的「同步照片」触发。
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class HighlightsController : ControllerBase
{
    /// <summary>缩略图缓存里这一池的子目录名。演员用演员 ID，艳图池固定用这个</summary>
    private const string Pool = "highlights";

    private readonly ILogger<HighlightsController> _logger;
    private readonly IConfiguration _config;
    private readonly Utils.SQLiteHelper _db;

    public HighlightsController(ILogger<HighlightsController> logger, IConfiguration config, Utils.SQLiteHelper db)
    {
        _logger = logger;
        _config = config;
        _db = db;
    }

    private SqliteConnection GetConnection()
    {
        return new SqliteConnection(_config.GetConnectionString("DefaultConnection"));
    }

    /// <summary>
    /// 艳图清单（读表，不碰磁盘）。宽高可能为空：那张还没出过缩略图，前端按默认比例占位。
    /// </summary>
    [HttpGet("posters")]
    public IActionResult GetPosters()
    {
        try
        {
            using var conn = GetConnection();
            conn.Open();

            var list = new List<object>();
            using var cmd = new SqliteCommand(
                @"SELECT file_name, IFNULL(width, 0), IFNULL(height, 0), size
                  FROM highlight_images ORDER BY file_name", conn);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                list.Add(new
                {
                    fileName = reader.GetString(0),
                    width = reader.GetInt32(1),
                    height = reader.GetInt32(2),
                    size = reader.GetInt64(3)
                });

            var message = list.Count > 0 ? null : "还没有艳图，点「同步照片」从 default 目录导入";
            return Ok(new { success = true, data = list, total = list.Count, message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取艳图列表失败");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>
    /// 扫一次 default 目录入库。根目录或 default 不存在时报错返回、一个字都不写 ——
    /// 挂载卷暂时没挂上时，"按差集删除"等于把整张表清空。
    /// </summary>
    [HttpPost("images/sync")]
    public IActionResult SyncImages()
    {
        try
        {
            using var conn = GetConnection();
            conn.Open();

            var posterDir = Utils.ImageIndex.PosterDir(conn);
            if (string.IsNullOrEmpty(posterDir))
                return Ok(new { success = false, message = "未配置艳图目录（系统设置 → 艳图目录）" });

            var dir = Path.Combine(posterDir, "default");
            if (!Directory.Exists(dir))
                return Ok(new { success = false, message = $"艳图目录不存在：{dir}" });

            var stat = Utils.ImageIndex.Sync(conn, "highlight_images", null, null, dir);
            return Ok(new
            {
                success = true,
                data = new { stat.Added, stat.Updated, stat.Removed, stat.Total },
                message = $"新增 {stat.Added}、更新 {stat.Updated}、移除 {stat.Removed}，共 {stat.Total} 张"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "同步艳图失败");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>艳图原图（灯箱用）。清单来自表，所以这里也只认表里有的文件名。</summary>
    [HttpGet("poster/{fileName}")]
    public IActionResult GetPoster(string fileName)
    {
        try
        {
            var safeFileName = Utils.SafePath.AsFileName(fileName);
            if (safeFileName is null || !Utils.SafePath.IsImageFile(safeFileName)) return NotFound();

            using var conn = GetConnection();
            conn.Open();

            var posterDir = Utils.ImageIndex.PosterDir(conn);
            if (string.IsNullOrEmpty(posterDir)) return NotFound();
            if (!Known(conn, safeFileName)) return NotFound();

            var filePath = Path.Combine(posterDir, "default", safeFileName);
            if (!Utils.SafePath.IsInside(filePath, posterDir)) return NotFound();

            return Utils.CachedFile.TryServe(this, filePath) ?? NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取艳图照片失败");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>
    /// 缩略图：s=160、m=400。一墙几十张原来全拉原图（最大一张 2 MB），
    /// 画布格子只有 200px 宽，用 m 档足够还省掉几十兆传输。
    /// </summary>
    [HttpGet("thumb/{size}/{fileName}")]
    public IActionResult GetThumb(string size, string fileName)
    {
        try
        {
            var safeFileName = Utils.SafePath.AsFileName(fileName);
            var width = Utils.Thumbs.WidthOf(size);
            if (safeFileName is null || width == 0 || !Utils.SafePath.IsImageFile(safeFileName)) return NotFound();

            using var conn = GetConnection();
            conn.Open();

            var posterDir = Utils.ImageIndex.PosterDir(conn);
            if (string.IsNullOrEmpty(posterDir)) return NotFound();
            if (!Known(conn, safeFileName)) return NotFound();

            var source = Path.Combine(posterDir, "default", safeFileName);
            if (!Utils.SafePath.IsInside(source, posterDir)) return NotFound();

            var cacheRoot = Utils.Thumbs.CacheRoot(_config, _db.GetDbPath());
            var thumb = Utils.Thumbs.Ensure(source, cacheRoot, Pool, safeFileName, width, out var sw, out var sh);
            if (thumb is null)
                // 解码不了的（损坏或没编进来的格式）退回原图，页面至少还有东西可看
                return Utils.CachedFile.TryServe(this, source) ?? NotFound();

            if (sw > 0)
            {
                // 顺手回填宽高：前端靠它预留格子比例。只在缺失时写，避免每次命中都产生写操作
                using var back = new SqliteCommand(
                    "UPDATE highlight_images SET width = @w, height = @h WHERE file_name = @file AND width IS NULL", conn);
                back.Parameters.Add(new SqliteParameter("@w", sw));
                back.Parameters.Add(new SqliteParameter("@h", sh));
                back.Parameters.Add(new SqliteParameter("@file", safeFileName));
                back.ExecuteNonQuery();
            }

            return Utils.CachedFile.TryServe(this, thumb.Value.Path, "image/webp") ?? NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "生成艳图缩略图失败");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>这张图片是否已经同步进表</summary>
    private static bool Known(SqliteConnection conn, string fileName)
    {
        using var cmd = new SqliteCommand("SELECT 1 FROM highlight_images WHERE file_name = @file", conn);
        cmd.Parameters.Add(new SqliteParameter("@file", fileName));
        return cmd.ExecuteScalar() is not null;
    }
}

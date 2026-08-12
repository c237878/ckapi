using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace ckapi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class HighlightsController : ControllerBase
{
    private readonly ILogger<HighlightsController> _logger;
    private readonly IConfiguration _config;

    public HighlightsController(ILogger<HighlightsController> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    private SqliteConnection GetConnection()
    {
        return new SqliteConnection(_config.GetConnectionString("DefaultConnection"));
    }

    /// <summary>
    /// 获取精彩瞬间海报列表（default 文件夹）
    /// </summary>
    [HttpGet("posters")]
    public IActionResult GetPosters()
    {
        try
        {
            using var conn = GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand("SELECT content FROM system_settings WHERE name = 'posterDir'", conn);
            var posterDir = cmd.ExecuteScalar()?.ToString();

            if (string.IsNullOrEmpty(posterDir))
            {
                return Ok(new { success = true, data = new string[0], message = "未配置海报墙目录" });
            }

            var defaultDir = Path.Combine(posterDir, "default");
            if (!Directory.Exists(defaultDir))
            {
                return Ok(new { success = true, data = new string[0], message = "default 文件夹不存在" });
            }

            var files = Directory.GetFiles(defaultDir, "*.*")
                .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
                .Select(f => Path.GetFileName(f))
                .ToList();

            return Ok(new { success = true, data = files });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取精彩瞬间海报失败");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 获取精彩瞬间海报图片
    /// </summary>
    [HttpGet("poster/{fileName}")]
    public IActionResult GetPoster(string fileName)
    {
        try
        {
            using var conn = GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand("SELECT content FROM system_settings WHERE name = 'posterDir'", conn);
            var posterDir = cmd.ExecuteScalar()?.ToString();

            if (string.IsNullOrEmpty(posterDir))
            {
                return NotFound();
            }

            var filePath = Path.Combine(posterDir, "default", fileName);
            if (!System.IO.File.Exists(filePath))
            {
                return NotFound();
            }

            var ext = Path.GetExtension(fileName).ToLower();
            var contentType = ext switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".webp" => "image/webp",
                _ => "application/octet-stream"
            };

            return PhysicalFile(filePath, contentType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取精彩瞬间图片失败");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }
}

using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using System.Text.Json.Serialization;

namespace ckapi.Controllers;

/// <summary>
/// 文件上传接口
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class UploadController : ControllerBase
{
    private readonly ILogger<UploadController> _logger;
    private readonly IConfiguration _config;

    public UploadController(ILogger<UploadController> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    /// <summary>
    /// 上传视频文件
    /// </summary>
    [HttpPost("video")]
    public async Task<IActionResult> UploadVideo(
        [FromForm] string directory,
        [FromForm] IFormFile file)
    {
        return await SaveFile(directory, file, "video");
    }

    /// <summary>
    /// 上传封面图片
    /// </summary>
    [HttpPost("cover")]
    public async Task<IActionResult> UploadCover(
        [FromForm] string directory,
        [FromForm] IFormFile file)
    {
        return await SaveFile(directory, file, "cover");
    }

    private async Task<IActionResult> SaveFile(string directory, IFormFile file, string type)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return BadRequest(new { success = false, message = "请选择上传目录" });

        if (file == null || file.Length == 0)
            return BadRequest(new { success = false, message = "请选择要上传的文件" });

        // 验证目录是否存在
        if (!Directory.Exists(directory))
        {
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建目录失败: {Dir}", directory);
                return BadRequest(new { success = false, message = $"无法创建目录: {ex.Message}" });
            }
        }

        // 使用原文件名
        var fileName = Path.GetFileName(file.FileName);
        var savePath = Path.Combine(directory, fileName);

        // 如果文件已存在，追加时间戳
        if (System.IO.File.Exists(savePath))
        {
            var ext = Path.GetExtension(fileName);
            var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
            var timestamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            fileName = $"{nameWithoutExt}_{timestamp}{ext}";
            savePath = Path.Combine(directory, fileName);
        }

        try
        {
            using (var stream = new FileStream(savePath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }

            _logger.LogInformation("[Upload] {Type} 上传成功: {Path}", type, savePath);

            return Ok(new
            {
                success = true,
                message = "上传成功",
                filePath = savePath,
                fileName = fileName,
                fileSize = file.Length
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Upload] {Type} 上传失败: {Path}", type, savePath);
            return StatusCode(500, new { success = false, message = $"上传失败: {ex.Message}" });
        }
    }
}

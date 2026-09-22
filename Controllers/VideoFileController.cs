using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace ckapi.Controllers;

/// <summary>
/// 视频文件操作：重置文件大小、删除物理文件、更新文件信息、片源质量
/// 路由前缀保持 api/video，与前端既有调用一致
/// </summary>
[ApiController]
[Route("api/video")]
public class VideoFileController : ControllerBase
{
    private readonly IConfiguration _config;
    private readonly ILogger<VideoFileController> _logger;

    public VideoFileController(IConfiguration config, ILogger<VideoFileController> logger)
    {
        _config = config;
        _logger = logger;
    }

    private SqliteConnection GetConnection()
    {
        return new SqliteConnection(_config.GetConnectionString("DefaultConnection"));
    }

    /// <summary>
    /// 重置视频文件大小
    /// </summary>
    [HttpPost("{id}/reset-file-size")]
    public IActionResult ResetFileSize(string id)
    {
        try
        {
            using var conn = GetConnection();
            conn.Open();

            // 获取视频完整信息
            string? videoCode = null;
            string? currentFilePath = null;
            string? currentCoverPath = null;
            long currentFileSize = 0;

            const string selectVideoSql = "SELECT code, file_path, cover_path, file_size FROM videos WHERE id = @id";
            using var getCmd = new SqliteCommand(selectVideoSql, conn);
            getCmd.Parameters.AddWithValue("@id", id);

            using (var reader = getCmd.ExecuteReader())
            {
                if (!reader.Read())
                    return NotFound(new { success = false, message = "视频不存在" });

                videoCode = reader["code"]?.ToString();
                currentFilePath = reader["file_path"] == DBNull.Value ? null : reader["file_path"]?.ToString();
                currentCoverPath = reader["cover_path"] == DBNull.Value ? null : reader["cover_path"]?.ToString();
                currentFileSize = reader["file_size"] == DBNull.Value ? 0 : Convert.ToInt64(reader["file_size"]);
            }

            var newFilePath = currentFilePath;
            long? newFileSize = null;
            var newCoverPath = currentCoverPath;
            var messages = new List<string>();

            // ===== 1. 处理视频文件路径与大小 =====
            if (!string.IsNullOrEmpty(currentFilePath) && System.IO.File.Exists(currentFilePath))
            {
                var fi = new FileInfo(currentFilePath);
                newFileSize = fi.Length;
                messages.Add($"文件大小: {FormatFileSize(newFileSize.Value)}");
            }
            else if (!string.IsNullOrEmpty(videoCode))
            {
                var videoDirs = QueryScanDirectoriesByCategory(conn, "视频");
                bool found = false;

                foreach (var dirPath in videoDirs)
                {
                    if (!Directory.Exists(dirPath)) continue;
                    var searchPath = Path.Combine(dirPath, $"{videoCode}.mp4");

                    if (System.IO.File.Exists(searchPath))
                    {
                        var fi = new FileInfo(searchPath);
                        newFilePath = searchPath;
                        newFileSize = fi.Length;
                        messages.Add($"在目录 [{dirPath}] 中找到匹配文件");
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    messages.Add(videoDirs.Any() ? "在所有配置的视频目录中未找到匹配文件" : "未配置视频目录");
                }
            }
            else
            {
                messages.Add("无番号，无法搜索");
            }

            // ===== 2. 处理封面路径（仅封面为空时搜索）=====
            if (string.IsNullOrEmpty(newCoverPath) && !string.IsNullOrEmpty(videoCode))
            {
                var coverDirs = QueryScanDirectoriesByCategory(conn, "封面");
                bool coverFound = false;

                foreach (var dir in coverDirs)
                {
                    if (!Directory.Exists(dir)) continue;
                    var searchPath = Path.Combine(dir, $"{videoCode}.jpg");

                    if (System.IO.File.Exists(searchPath))
                    {
                        newCoverPath = searchPath;
                        messages.Add("封面已找回");
                        coverFound = true;
                        break;
                    }
                }

                if (!coverFound)
                {
                    messages.Add("未找到封面");
                }
            }

            // ===== 3. 合并为一次 UPDATE，消除重复SQL分支 =====
            long finalFileSize = newFileSize ?? 0;
            bool sizeChanged = finalFileSize != currentFileSize;
            var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            const string updateSql = @"
                UPDATE videos
                SET file_path = @fp,
                    file_size = @fs,
                    cover_path = @cp,
                    media_attr_flags = 0,
                    ctime = CASE WHEN @updateCtime = 1 THEN @ctime ELSE ctime END
                WHERE id = @id";

            using var updCmd = new SqliteCommand(updateSql, conn);
            updCmd.Parameters.AddWithValue("@fp", newFilePath ?? "");
            updCmd.Parameters.AddWithValue("@fs", finalFileSize);
            updCmd.Parameters.AddWithValue("@cp", string.IsNullOrEmpty(newCoverPath) ? DBNull.Value : newCoverPath);
            updCmd.Parameters.AddWithValue("@updateCtime", sizeChanged ? 1 : 0);
            updCmd.Parameters.AddWithValue("@ctime", now);
            updCmd.Parameters.AddWithValue("@id", id);
            updCmd.ExecuteNonQuery();

            return Ok(new {
                success = true,
                data = new { filePath = newFilePath, fileSize = newFileSize, coverPath = newCoverPath },
                message = string.Join("；", messages)
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ResetFileSize failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    // 提取通用查询方法，消除重复代码
    private List<string> QueryScanDirectoriesByCategory(SqliteConnection conn, string category)
    {
        var dirs = new List<string>();
        const string sql = "SELECT path FROM scan_directories WHERE category = @cat ORDER BY path ASC";

        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@cat", category);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var path = reader.GetString(0);
            if (!string.IsNullOrWhiteSpace(path))
            {
                dirs.Add(path);
            }
        }
        return dirs;
    }

    /// <summary>
    /// 删除视频文件（置空路径和大小，不动封面）
    /// </summary>
    [HttpDelete("{id}/file")]
    public IActionResult DeleteVideoFile(string id)
    {
        try
        {
            using var conn = GetConnection();
            conn.Open();

            // 获取当前文件路径
            string? currentFilePath = null;
            using (var getCmd = new SqliteCommand("SELECT file_path FROM videos WHERE id = @id", conn))
            {
                getCmd.Parameters.Add(new SqliteParameter("@id", id));
                var result = getCmd.ExecuteScalar();
                if (result != null && result != DBNull.Value)
                    currentFilePath = result.ToString();
            }

            // 删除物理文件
            string message = "";
            if (!string.IsNullOrEmpty(currentFilePath) && currentFilePath.StartsWith("/"))
            {
                if (System.IO.File.Exists(currentFilePath))
                {
                    try
                    {
                        System.IO.File.Delete(currentFilePath);
                        message = "文件已删除";
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "删除文件失败: {path}", currentFilePath);
                        message = "文件删除失败（可能被其他程序占用）";
                    }
                }
                else
                {
                    message = "文件不存在，无需删除";
                }
            }
            else
            {
                message = "无有效文件路径";
            }

            // 置空 file_path 和 file_size
            using var updCmd = new SqliteCommand(
                "UPDATE videos SET file_path = NULL, file_size = 0 WHERE id = @id", conn);
            updCmd.Parameters.Add(new SqliteParameter("@id", id));
            updCmd.ExecuteNonQuery();

            return Ok(new { success = true, message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DeleteVideoFile failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>
    /// 更新视频的文件路径和大小（上传成功后调用）
    /// </summary>
    [HttpPut("{id}/file-info")]
    public IActionResult UpdateFileInfo(string id, [FromBody] UpdateFileInfoRequest req)
    {
        try
        {
            using var conn = GetConnection();
            conn.Open();

            long? newFileSize = null;
            if (!string.IsNullOrEmpty(req.FilePath) && System.IO.File.Exists(req.FilePath))
            {
                var fi = new System.IO.FileInfo(req.FilePath);
                newFileSize = fi.Length;
            }

            using var cmd = new SqliteCommand(@"
                UPDATE videos SET
                    file_path = @fp,
                    cover_path = COALESCE(@cp, cover_path),
                    file_size = COALESCE(@fs, file_size)
                WHERE id = @id", conn);
            cmd.Parameters.Add(new SqliteParameter("@id", id));
            cmd.Parameters.AddWithValue("@fp", (object?)req.FilePath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@cp", (object?)req.CoverPath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@fs", (object?)newFileSize ?? DBNull.Value);
            var rows = cmd.ExecuteNonQuery();

            if (rows == 0)
                return NotFound(new { success = false, message = "视频不存在" });

            return Ok(new { success = true, data = new { filePath = req.FilePath, fileSize = newFileSize, coverPath = req.CoverPath }, message = "文件信息已更新" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UpdateFileInfo failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>
    /// 更新媒体属性标志（片源质量）
    /// </summary>
    [HttpPut("{id}/media-flags")]
    public IActionResult UpdateMediaFlags(string id, [FromBody] UpdateMediaFlagsRequest req)
    {
        try
        {
            if (req.Flags < 0 || req.Flags > 3)
                return Ok(new { success = false, message = "flags 值必须在 0~3 之间" });

            using var conn = GetConnection();
            conn.Open();

            // 检查当前值，如果已设置（非0）则不允许修改
            using var checkCmd = new SqliteCommand("SELECT media_attr_flags FROM videos WHERE id = @id", conn);
            checkCmd.Parameters.Add(new SqliteParameter("@id", id));
            var current = Convert.ToInt32(checkCmd.ExecuteScalar() ?? 0);
            if (current != 0)
                return Ok(new { success = false, message = "片源质量已设置，不可修改。请先重置后再设置。" });

            using var cmd = new SqliteCommand("UPDATE videos SET media_attr_flags = @flags WHERE id = @id", conn);
            cmd.Parameters.Add(new SqliteParameter("@flags", req.Flags));
            cmd.Parameters.Add(new SqliteParameter("@id", id));
            var rows = cmd.ExecuteNonQuery();
            if (rows == 0)
                return NotFound(new { success = false, message = "视频不存在" });

            return Ok(new { success = true, message = "片源质量已更新", data = new { mediaAttrFlags = req.Flags } });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UpdateMediaFlags failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>
    /// 检查并重命名文件名与番号一致
    /// </summary>
    [HttpPost("rename-to-code")]
    public IActionResult RenameFilesToCode()
    {
        try
        {
            using var conn = GetConnection();
            conn.Open();

            var results = new List<object>();
            var renamed = 0;
            var skipped = 0;
            var failed = 0;

            using var cmd = new SqliteCommand(@"
                SELECT id, code, file_path, cover_path
                FROM videos
                WHERE code IS NOT NULL AND code != ''
                AND ((file_path != '' AND file_path NOT LIKE 'manual://%' AND file_path NOT LIKE '%'||code||'%')
                OR (cover_path != '' AND cover_path NOT LIKE 'manual://%' AND cover_path NOT LIKE '%'||code||'%'))", conn);
            using var reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                var videoId = reader["id"].ToString()!;
                var code = reader["code"].ToString()!;
                var filePath = reader["file_path"]?.ToString() ?? "";
                var coverPath = reader["cover_path"]?.ToString() ?? "";

                var fileRenamed = false;
                var coverRenamed = false;
                var newFilePath = filePath;
                var newCoverPath = coverPath;
                var errors = new List<string>();

                // 检查视频文件名
                if (!string.IsNullOrEmpty(filePath) && System.IO.File.Exists(filePath))
                {
                    var dir = Path.GetDirectoryName(filePath)!;
                    var ext = Path.GetExtension(filePath);
                    var currentName = Path.GetFileNameWithoutExtension(filePath);
                    if (currentName != code)
                    {
                        newFilePath = Path.Combine(dir, code + ext);
                        try
                        {
                            // 避免覆盖：如果目标文件已存在且不是同一个文件，跳过
                            if (System.IO.File.Exists(newFilePath) && newFilePath != filePath)
                            {
                                errors.Add($"视频文件目标已存在: {code + ext}");
                            }
                            else
                            {
                                System.IO.File.Move(filePath, newFilePath);
                                fileRenamed = true;
                            }
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"重命名视频失败: {ex.Message}");
                        }
                        newFilePath = filePath;
                    }
                }

                // 检查封面文件名
                if (!string.IsNullOrEmpty(coverPath) && System.IO.File.Exists(coverPath))
                {
                    var dir = Path.GetDirectoryName(coverPath)!;
                    var ext = Path.GetExtension(coverPath);
                    var currentName = Path.GetFileNameWithoutExtension(coverPath);
                    if (currentName != code)
                    {
                        newCoverPath = Path.Combine(dir, code + ext);
                        try
                        {
                            if (System.IO.File.Exists(newCoverPath) && newCoverPath != coverPath)
                            {
                                errors.Add($"封面文件目标已存在: {code + ext}");
                            }
                            else
                            {
                                System.IO.File.Move(coverPath, newCoverPath);
                                coverRenamed = true;
                            }
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"重命名封面失败: {ex.Message}");
                        }
                        newCoverPath = coverPath;
                    }
                }

                // 如果有文件被重命名，更新数据库中的路径
                if (fileRenamed || coverRenamed)
                {
                    using var updCmd = new SqliteCommand("UPDATE videos SET file_path = @fp, cover_path = @cp WHERE id = @id", conn);
                    updCmd.Parameters.Add(new SqliteParameter("@fp", fileRenamed ? newFilePath : filePath));
                    updCmd.Parameters.Add(new SqliteParameter("@cp", coverRenamed ? newCoverPath : coverPath));
                    updCmd.Parameters.Add(new SqliteParameter("@id", videoId));
                    updCmd.ExecuteNonQuery();
                    renamed++;
                    results.Add(new { videoId, code, fileRenamed, coverRenamed, oldFile = filePath, newFile = newFilePath, oldCover = coverPath, newCover = newCoverPath, errors });
                }
                else if (errors.Count > 0)
                {
                    failed++;
                    results.Add(new { videoId, code, fileRenamed = false, coverRenamed = false, errors });
                }
                else
                {
                    skipped++;
                }
            }

            return Ok(new
            {
                success = true,
                data = new { renamed, skipped, failed, details = results }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RenameFilesToCode failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    private string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        int order = 0; double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1) { order++; size /= 1024; }
        return Math.Round(size, 2) + " " + sizes[order];
    }
}

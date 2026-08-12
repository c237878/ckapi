using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.IO;

namespace ckapi.Controllers;

/// <summary>
/// 影片控制器（新版 - Samba影城系统）
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class VideoController : ControllerBase
{
    private readonly IConfiguration _config;
    private readonly ILogger<VideoController> _logger;

    public VideoController(IConfiguration config, ILogger<VideoController> logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// 获取视频列表
    /// </summary>
    [HttpGet("list")]
    public IActionResult GetList(
        [FromQuery] int pageIndex = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? category = null,
        [FromQuery] string? country = null,
        [FromQuery] string? keyword = null,
        [FromQuery] string? sambaDir = null)
    {
        try
        {
            var offset = (pageIndex - 1) * pageSize;
            var whereClause = "WHERE 1=1";
            var parameters = new List<SqliteParameter>();

            if (!string.IsNullOrEmpty(category))
            {
                whereClause += " AND category = @category";
                parameters.Add(new SqliteParameter("@category", category));
            }

            if (!string.IsNullOrEmpty(keyword))
            {
                whereClause += " AND name LIKE @keyword";
                parameters.Add(new SqliteParameter("@keyword", $"%{keyword}%"));
            }

            if (!string.IsNullOrEmpty(country))
            {
                whereClause += " AND country = @country";
                parameters.Add(new SqliteParameter("@country", country));
            }

            // 获取总数
            var countSql = $"SELECT COUNT(*) FROM videos {whereClause}";
            var total = Convert.ToInt32(ExecuteScalar(countSql, parameters.ToArray()));

            // 获取列表
            var sql = $@"
                SELECT * FROM videos
                {whereClause}
                ORDER BY ctime DESC
                LIMIT @pageSize OFFSET @offset";
            
            parameters.Add(new SqliteParameter("@pageSize", pageSize));
            parameters.Add(new SqliteParameter("@offset", offset));

            using var conn = new SqliteConnection(_config.GetConnectionString("DefaultConnection"));
            conn.Open();
            
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddRange(parameters.ToArray());
            
            using var reader = cmd.ExecuteReader();
            
            var videos = new List<object>();
            while (reader.Read())
            {
                videos.Add(new
                {
                    id = reader["id"].ToString(),
                    name = reader["name"].ToString(),
                    title = reader["name"].ToString(),
                    category = reader["category"] == DBNull.Value ? "" : reader["category"].ToString(),
                    country = reader["country"] == DBNull.Value ? "" : reader["country"].ToString(),
                    code = reader["code"] == DBNull.Value ? "" : reader["code"].ToString(),
                    filePath = reader["file_path"] == DBNull.Value ? "" : reader["file_path"].ToString(),
                    fileSize = reader["file_size"] == DBNull.Value ? 0 : Convert.ToInt64(reader["file_size"]),
                    coverPath = reader["cover_path"] == DBNull.Value ? "" : reader["cover_path"].ToString(),
                    ctime = reader["ctime"] == DBNull.Value ? "" : reader["ctime"].ToString(),
                    addedAt = reader["ctime"] == DBNull.Value ? "" : reader["ctime"].ToString(),
                    utime = reader["utime"] == DBNull.Value ? "" : reader["utime"].ToString(),
                    seriesid = reader["seriesid"] == DBNull.Value ? "" : reader["seriesid"].ToString(),
                    mediaAttrFlags = reader["media_attr_flags"] == DBNull.Value ? 0 : Convert.ToInt32(reader["media_attr_flags"]),
                    alias = ""
                });
            }

            return Ok(new
            {
                success = true,
                data = new
                {
                    list = videos,
                    total = total,
                    page = pageIndex,
                    pageSize = pageSize
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetList failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 获取所有已有的分类和国家（用于表单下拉）
    /// </summary>
    [HttpGet("meta")]
    public IActionResult GetMeta()
    {
        try
        {
            using var conn = new SqliteConnection(_config.GetConnectionString("DefaultConnection"));
            conn.Open();

            var categories = new List<string>();
            var countries = new List<string>();

            using (var catCmd = new SqliteCommand("SELECT DISTINCT category FROM videos WHERE category IS NOT NULL AND category != '' ORDER BY category", conn))
            using (var reader = catCmd.ExecuteReader())
            {
                while (reader.Read()) categories.Add(reader.GetString(0));
            }

            using (var countryCmd = new SqliteCommand("SELECT DISTINCT country FROM videos WHERE country IS NOT NULL AND country != '' ORDER BY country", conn))
            using (var reader = countryCmd.ExecuteReader())
            {
                while (reader.Read()) countries.Add(reader.GetString(0));
            }

            return Ok(new { success = true, categories, countries });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetMeta failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 获取视频详情
    /// </summary>
    [HttpGet("{id}")]
    public IActionResult GetById(string id)
    {
        try
        {
            var sql = "SELECT * FROM videos WHERE id = @id";
            using var conn = new SqliteConnection(_config.GetConnectionString("DefaultConnection"));
            conn.Open();
            
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.Add(new SqliteParameter("@id", id));
            
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return NotFound(new { success = false, message = "视频不存在" });

            var video = new
            {
                id = reader["id"].ToString(),
                name = reader["name"].ToString(),
                title = reader["name"].ToString(),
                category = reader["category"] == DBNull.Value ? "" : reader["category"].ToString(),
                country = reader["country"] == DBNull.Value ? "" : reader["country"].ToString(),
                code = reader["code"] == DBNull.Value ? "" : reader["code"].ToString(),
                filePath = reader["file_path"] == DBNull.Value ? "" : reader["file_path"].ToString(),
                fileSize = reader["file_size"] == DBNull.Value ? 0 : Convert.ToInt64(reader["file_size"]),
                coverPath = reader["cover_path"] == DBNull.Value ? "" : reader["cover_path"].ToString(),
                ctime = reader["ctime"] == DBNull.Value ? "" : reader["ctime"].ToString(),
                addedAt = reader["ctime"] == DBNull.Value ? "" : reader["ctime"].ToString(),
                utime = reader["utime"] == DBNull.Value ? "" : reader["utime"].ToString(),
                seriesid = reader["seriesid"] == DBNull.Value ? "" : reader["seriesid"].ToString(),
                alias = reader["alias"] == DBNull.Value ? "" : reader["alias"].ToString()
            };

            // 获取演员列表
            var actorSql = @"
                SELECT a.* FROM actors a
                INNER JOIN video_actors va ON a.id = va.actor_id
                WHERE va.video_id = @videoId";
            
            using var actorCmd = new SqliteCommand(actorSql, conn);
            actorCmd.Parameters.Add(new SqliteParameter("@videoId", id));
            
            using var actorReader = actorCmd.ExecuteReader();
            var actors = new List<object>();
            while (actorReader.Read())
            {
                actors.Add(new
                {
                    id = actorReader["id"].ToString(),
                    name = actorReader["name"].ToString(),
                    avatar_path = actorReader["avatar_path"] == DBNull.Value ? "" : actorReader["avatar_path"].ToString()
                });
            }

            // 统计点赞数（video_likes 表）
            int likeCount = 0;
            try
            {
                using var likeCmd = new SqliteCommand("SELECT COUNT(*) FROM video_likes WHERE video_id = @videoId", conn);
                likeCmd.Parameters.Add(new SqliteParameter("@videoId", id));
                likeCount = Convert.ToInt32(likeCmd.ExecuteScalar());
            }
            catch { /* 表不存在时忽略 */ }

            return Ok(new
            {
                success = true,
                data = new
                {
                    video = video,
                    actors = actors,
                    likeCount = likeCount
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetById failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 点赞影片
    /// </summary>
    [HttpPost("{id}/like")]
    public IActionResult LikeVideo(string id)
    {
        try
        {
            using var conn = new SqliteConnection(_config.GetConnectionString("DefaultConnection"));
            conn.Open();

            // 检查视频是否存在
            using (var checkCmd = new SqliteCommand("SELECT id FROM videos WHERE id = @id", conn))
            {
                checkCmd.Parameters.Add(new SqliteParameter("@id", id));
                if (checkCmd.ExecuteScalar() == null)
                    return NotFound(new { success = false, message = "视频不存在" });
            }

            // 插入 video_likes 记录
            var likedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            using (var insertCmd = new SqliteCommand("INSERT INTO video_likes (video_id, liked_at) VALUES (@videoId, @likedAt)", conn))
            {
                insertCmd.Parameters.Add(new SqliteParameter("@videoId", id));
                insertCmd.Parameters.Add(new SqliteParameter("@likedAt", likedAt));
                insertCmd.ExecuteNonQuery();
            }

            // 统计点赞数
            int likeCount = 0;
            using (var countCmd = new SqliteCommand("SELECT COUNT(*) FROM video_likes WHERE video_id = @videoId", conn))
            {
                countCmd.Parameters.Add(new SqliteParameter("@videoId", id));
                likeCount = Convert.ToInt32(countCmd.ExecuteScalar());
            }

            return Ok(new { success = true, likeCount });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LikeVideo failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 手动添加视频
    /// </summary>
    [HttpPost("add")]
    public IActionResult AddVideo([FromBody] AddVideoRequest req)
    {
        try
        {
            var id = Guid.NewGuid().ToString();
            var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var sql = @"
                INSERT INTO videos (id, name, category, country, file_path, file_size, cover_path, code, ctime, utime, alias)
                VALUES (@id, @name, @category, @country, @filePath, @fileSize, @coverPath, @code, @ctime, @utime, @alias)";
            
            using var conn = new SqliteConnection(_config.GetConnectionString("DefaultConnection"));
            conn.Open();
            
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.Add(new SqliteParameter("@id", id));
            cmd.Parameters.Add(new SqliteParameter("@name", req.Title ?? req.Name ?? ""));
            cmd.Parameters.Add(new SqliteParameter("@category", req.Category ?? ""));
            cmd.Parameters.Add(new SqliteParameter("@country", req.Country ?? ""));
            cmd.Parameters.Add(new SqliteParameter("@filePath", req.FilePath ?? (object)DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@fileSize", req.FileSize ?? 0));
            cmd.Parameters.Add(new SqliteParameter("@coverPath", req.CoverPath ?? (object)DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@code", req.Code ?? (object)DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@ctime", now));
            cmd.Parameters.Add(new SqliteParameter("@utime", now));
            cmd.Parameters.Add(new SqliteParameter("@alias", req.Alias ?? (object)DBNull.Value));
            cmd.ExecuteNonQuery();

            // 关联演员
            if (req.ActorIds != null && req.ActorIds.Any())
            {
                foreach (var actorId in req.ActorIds)
                {
                    var relSql = "INSERT OR IGNORE INTO video_actors (video_id, actor_id) VALUES (@videoId, @actorId)";
                    using var relCmd = new SqliteCommand(relSql, conn);
                    relCmd.Parameters.Add(new SqliteParameter("@videoId", id));
                    relCmd.Parameters.Add(new SqliteParameter("@actorId", actorId));
                    relCmd.ExecuteNonQuery();
                }
            }

            return Ok(new { success = true, data = new { id = id }, message = "添加成功" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AddVideo failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 更新视频
    /// </summary>
    [HttpPut("{id}")]
    public IActionResult UpdateVideo(string id, [FromBody] UpdateVideoRequest req)
    {
        try
        {
            var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var sql = @"
                UPDATE videos SET 
                    name = @name, 
                    category = @category, 
                    country = @country,
                    file_path = @filePath, 
                    cover_path = @coverPath,
                    code = @code,
                    utime = @utime,
                    alias = @alias
                WHERE id = @id";

            using var conn = new SqliteConnection(_config.GetConnectionString("DefaultConnection"));
            conn.Open();

            using var checkCmd = new SqliteCommand("SELECT COUNT(*) FROM videos WHERE id = @id", conn);
            checkCmd.Parameters.Add(new SqliteParameter("@id", id));
            if (Convert.ToInt32(checkCmd.ExecuteScalar()) == 0)
                return NotFound(new { success = false, message = "视频不存在" });

            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.Add(new SqliteParameter("@id", id));
            cmd.Parameters.Add(new SqliteParameter("@name", req.Title ?? req.Name ?? ""));
            cmd.Parameters.Add(new SqliteParameter("@category", req.Category ?? ""));
            cmd.Parameters.Add(new SqliteParameter("@country", req.Country ?? ""));
            cmd.Parameters.Add(new SqliteParameter("@filePath", req.FilePath ?? (object)DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@coverPath", req.CoverPath ?? (object)DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@code", req.Code ?? (object)DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@utime", now));
            cmd.Parameters.Add(new SqliteParameter("@alias", req.Alias ?? (object)DBNull.Value));
            cmd.ExecuteNonQuery();

            // 更新演员关联
            if (req.ActorIds != null)
            {
                using var delCmd = new SqliteCommand("DELETE FROM video_actors WHERE video_id = @videoId", conn);
                delCmd.Parameters.Add(new SqliteParameter("@videoId", id));
                delCmd.ExecuteNonQuery();

                foreach (var actorId in req.ActorIds)
                {
                    var relSql = "INSERT INTO video_actors (video_id, actor_id) VALUES (@videoId, @actorId)";
                    using var relCmd = new SqliteCommand(relSql, conn);
                    relCmd.Parameters.Add(new SqliteParameter("@videoId", id));
                    relCmd.Parameters.Add(new SqliteParameter("@actorId", actorId));
                    relCmd.ExecuteNonQuery();
                }
            }

            return Ok(new { success = true, message = "更新成功" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UpdateVideo failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 删除视频
    /// </summary>
    [HttpDelete("{id}")]
    public IActionResult DeleteVideo(string id)
    {
        try
        {
            using var conn = new SqliteConnection(_config.GetConnectionString("DefaultConnection"));
            conn.Open();

            using var delRelCmd = new SqliteCommand("DELETE FROM video_actors WHERE video_id = @videoId", conn);
            delRelCmd.Parameters.Add(new SqliteParameter("@videoId", id));
            delRelCmd.ExecuteNonQuery();

            using var delCmd = new SqliteCommand("DELETE FROM videos WHERE id = @id", conn);
            delCmd.Parameters.Add(new SqliteParameter("@id", id));
            var rows = delCmd.ExecuteNonQuery();

            if (rows == 0)
                return NotFound(new { success = false, message = "视频不存在" });

            return Ok(new { success = true, message = "删除成功" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DeleteVideo failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 批量删除视频
    /// </summary>
    [HttpDelete("batch")]
    public IActionResult BatchDeleteVideos([FromBody] BatchDeleteRequest req)
    {
        try
        {
            if (req.Ids == null || req.Ids.Count == 0)
                return BadRequest(new { success = false, message = "ids 不能为空" });

            using var conn = new SqliteConnection(_config.GetConnectionString("DefaultConnection"));
            conn.Open();

            using var transaction = conn.BeginTransaction();
            var deleted = 0;
            var failed = 0;

            foreach (var id in req.Ids)
            {
                try
                {
                    using var delRelCmd = new SqliteCommand("DELETE FROM video_actors WHERE video_id = @videoId", conn, transaction);
                    delRelCmd.Parameters.Add(new SqliteParameter("@videoId", id));
                    delRelCmd.ExecuteNonQuery();

                    using var delCmd = new SqliteCommand("DELETE FROM videos WHERE id = @id", conn, transaction);
                    delCmd.Parameters.Add(new SqliteParameter("@id", id));
                    var rows = delCmd.ExecuteNonQuery();

                    if (rows > 0)
                        deleted++;
                    else
                        failed++;
                }
                catch { failed++; }
            }

            transaction.Commit();

            return Ok(new { success = true, data = new { deleted, failed } });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BatchDeleteVideos failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 扫描目录
    /// </summary>
    [HttpPost("scan")]
    public IActionResult ScanDirectory([FromBody] ScanRequest req)
    {
        try
        {
            var sql = @"
                INSERT INTO scan_tasks (task_type, status, target_path, started_at)
                VALUES ('manual', 'pending', @targetPath, @startedAt);
                SELECT last_insert_rowid();";
            
            using var conn = new SqliteConnection(_config.GetConnectionString("DefaultConnection"));
            conn.Open();
            
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.Add(new SqliteParameter("@targetPath", req.TargetPath));
            cmd.Parameters.Add(new SqliteParameter("@startedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
            
            var taskId = Convert.ToInt32(cmd.ExecuteScalar());

            // 启动后台扫描
            _ = Task.Run(() => ScanDirectoryAsync(req.TargetPath, req.Recursive, taskId));

            return Ok(new { success = true, data = new { taskId }, message = "扫描任务已启动" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ScanDirectory failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 获取扫描任务状态
    /// </summary>
    [HttpGet("scan/{taskId}")]
    public IActionResult GetScanStatus(int taskId)
    {
        try
        {
            var sql = "SELECT * FROM scan_tasks WHERE id = @taskId";
            using var conn = new SqliteConnection(_config.GetConnectionString("DefaultConnection"));
            conn.Open();
            
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.Add(new SqliteParameter("@taskId", taskId));
            
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return NotFound(new { success = false, message = "任务不存在" });

            return Ok(new
            {
                success = true,
                data = new
                {
                    id = Convert.ToInt32(reader["id"]),
                    task_type = reader["task_type"].ToString(),
                    status = reader["status"].ToString(),
                    target_path = reader["target_path"].ToString(),
                    started_at = reader["started_at"]?.ToString(),
                    completed_at = reader["completed_at"]?.ToString(),
                    files_found = reader["files_found"] == DBNull.Value ? 0 : Convert.ToInt32(reader["files_found"]),
                    files_added = reader["files_added"] == DBNull.Value ? 0 : Convert.ToInt32(reader["files_added"]),
                    files_updated = reader["files_updated"] == DBNull.Value ? 0 : Convert.ToInt32(reader["files_updated"]),
                    errors = reader["errors"]?.ToString()
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetScanStatus failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 视频流代理（支持 Range 请求）
    /// </summary>
    [HttpGet("stream/{id}")]
    public IActionResult StreamVideo(string id)
    {
        try
        {
            var sql = "SELECT file_path FROM videos WHERE id = @id";
            using var conn = new SqliteConnection(_config.GetConnectionString("DefaultConnection"));
            conn.Open();
            
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.Add(new SqliteParameter("@id", id));
            
            var filePath = cmd.ExecuteScalar()?.ToString();
            if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath))
                return NotFound(new { success = false, message = "视频文件不存在" });

            var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return File(fileStream, "video/mp4", enableRangeProcessing: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StreamVideo failed");
            return StatusCode(500, new { success = false, message = ex.Message });
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
            using var conn = new SqliteConnection(_config.GetConnectionString("DefaultConnection"));
            conn.Open();
            
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.Add(new SqliteParameter("@id", id));
            
            var coverPath = cmd.ExecuteScalar()?.ToString();
            if (string.IsNullOrEmpty(coverPath) || !System.IO.File.Exists(coverPath))
                return NotFound(new { success = false, message = "封面不存在" });

            var fileStream = new FileStream(coverPath, FileMode.Open, FileAccess.Read);
            return File(fileStream, "image/jpeg");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetCover failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    #region 私有方法

    private object? ExecuteScalar(string sql, SqliteParameter[] parameters)
    {
        using var conn = new SqliteConnection(_config.GetConnectionString("DefaultConnection"));
        conn.Open();
        
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddRange(parameters);
        
        return cmd.ExecuteScalar();
    }

    private void ScanDirectoryAsync(string targetPath, bool recursive, int taskId)
    {
        try
        {
            var filesFound = 0;
            var filesAdded = 0;
            var filesUpdated = 0;
            var errors = new List<string>();

            var enumOpts = new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                IgnoreInaccessible = true
            };
            var mp4Files = Array.Empty<string>();
            try
            {
                mp4Files = Directory.GetFiles(targetPath, "*.mp4", enumOpts);
            }
            catch (Exception ex)
            {
                errors.Add($"扫描目录失败: {ex.Message}");
            }

            filesFound = mp4Files.Length;

            using var conn = new SqliteConnection(_config.GetConnectionString("DefaultConnection"));
            conn.Open();

            foreach (var filePath in mp4Files)
            {
                if (Path.GetFileName(filePath).StartsWith("._")) continue;

                try
                {
                    var fileName = Path.GetFileNameWithoutExtension(filePath);
                    var fileInfo = new FileInfo(filePath);
                    var coverPath = Path.ChangeExtension(filePath, ".jpg");
                    var coverExists = System.IO.File.Exists(coverPath);

                    var checkSql = "SELECT COUNT(*) FROM videos WHERE file_path = @filePath";
                    using var checkCmd = new SqliteCommand(checkSql, conn);
                    checkCmd.Parameters.Add(new SqliteParameter("@filePath", filePath));
                    var exists = Convert.ToInt32(checkCmd.ExecuteScalar()) > 0;

                    if (exists)
                    {
                        var updateSql = @"UPDATE videos SET file_size = @fileSize, cover_path = @coverPath WHERE file_path = @filePath";
                        using var updateCmd = new SqliteCommand(updateSql, conn);
                        updateCmd.Parameters.Add(new SqliteParameter("@fileSize", fileInfo.Length));
                        updateCmd.Parameters.Add(new SqliteParameter("@coverPath", coverExists ? coverPath : (object)DBNull.Value));
                        updateCmd.Parameters.Add(new SqliteParameter("@filePath", filePath));
                        updateCmd.ExecuteNonQuery();
                        filesUpdated++;
                    }
                    else
                    {
                        var id = Guid.NewGuid().ToString();
                        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                        var category = DetermineCategory(filePath);

                        var insertSql = @"INSERT INTO videos (id, name, category, file_path, file_size, cover_path, ctime, utime) 
                                        VALUES (@id, @name, @category, @filePath, @fileSize, @coverPath, @ctime, @utime)";
                        using var insertCmd = new SqliteCommand(insertSql, conn);
                        insertCmd.Parameters.Add(new SqliteParameter("@id", id));
                        insertCmd.Parameters.Add(new SqliteParameter("@name", fileName));
                        insertCmd.Parameters.Add(new SqliteParameter("@category", category));
                        insertCmd.Parameters.Add(new SqliteParameter("@filePath", filePath));
                        insertCmd.Parameters.Add(new SqliteParameter("@fileSize", fileInfo.Length));
                        insertCmd.Parameters.Add(new SqliteParameter("@coverPath", coverExists ? coverPath : (object)DBNull.Value));
                        insertCmd.Parameters.Add(new SqliteParameter("@ctime", now));
                        insertCmd.Parameters.Add(new SqliteParameter("@utime", now));
                        insertCmd.ExecuteNonQuery();
                        filesAdded++;
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"{filePath}: {ex.Message}");
                }
            }

            var updateTaskSql = @"UPDATE scan_tasks SET status = 'completed', completed_at = @completedAt, 
                                    files_found = @filesFound, files_added = @filesAdded, 
                                    files_updated = @filesUpdated, errors = @errors 
                                    WHERE id = @taskId";
            using var updateTaskCmd = new SqliteCommand(updateTaskSql, conn);
            updateTaskCmd.Parameters.Add(new SqliteParameter("@completedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
            updateTaskCmd.Parameters.Add(new SqliteParameter("@filesFound", filesFound));
            updateTaskCmd.Parameters.Add(new SqliteParameter("@filesAdded", filesAdded));
            updateTaskCmd.Parameters.Add(new SqliteParameter("@filesUpdated", filesUpdated));
            updateTaskCmd.Parameters.Add(new SqliteParameter("@errors", errors.Any() ? JsonSerializer.Serialize(errors) : (object)DBNull.Value));
            updateTaskCmd.Parameters.Add(new SqliteParameter("@taskId", taskId));
            updateTaskCmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            using var conn = new SqliteConnection(_config.GetConnectionString("DefaultConnection"));
            conn.Open();
            
            var updateTaskSql = @"UPDATE scan_tasks SET status = 'failed', completed_at = @completedAt, errors = @errors WHERE id = @taskId";
            using var updateTaskCmd = new SqliteCommand(updateTaskSql, conn);
            updateTaskCmd.Parameters.Add(new SqliteParameter("@completedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
            updateTaskCmd.Parameters.Add(new SqliteParameter("@errors", ex.Message));
            updateTaskCmd.Parameters.Add(new SqliteParameter("@taskId", taskId));
            updateTaskCmd.ExecuteNonQuery();
            
            _logger.LogError(ex, "ScanDirectoryAsync failed");
        }
    }

    private string DetermineCategory(string filePath)
    {
        var parts = filePath.Split('/', '\\');
        foreach (var part in parts)
        {
            if (part == "电影") return "电影";
            if (part == "电视剧") return "电视剧";
            if (part == "动漫") return "动漫";
        }
        return "其他";
    }

    #endregion
}

#region 请求模型

public class AddVideoRequest
{
    public string? Title { get; set; }
    public string? Name { get; set; }
    public string Category { get; set; } = "";
    public string Country { get; set; } = "";
    public string? FilePath { get; set; }
    public long? FileSize { get; set; }
    public string? CoverPath { get; set; }
    public string? Code { get; set; }
    public string? Alias { get; set; }
    public List<string>? ActorIds { get; set; }
}

public class UpdateVideoRequest
{
    public string? Title { get; set; }
    public string? Name { get; set; }
    public string Category { get; set; } = "";
    public string Country { get; set; } = "";
    public string? FilePath { get; set; }
    public string? CoverPath { get; set; }
    public string? Code { get; set; }
    public string? Alias { get; set; }
    public List<string>? ActorIds { get; set; }
}

public class ScanRequest
{
    public string TargetPath { get; set; } = "";
    public bool Recursive { get; set; } = true;
}

public class BatchDeleteRequest
{
    public List<string> Ids { get; set; } = new List<string>();
}

#endregion

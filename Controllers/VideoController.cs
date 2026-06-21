using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.IO;

namespace ckapi.Controllers;

/// <summary>
/// 影片控制器
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

    private SqliteConnection GetConnection()
    {
        return new SqliteConnection(_config.GetConnectionString("DefaultConnection"));
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
        [FromQuery] string? seriesId = null)
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
                whereClause += " AND (name LIKE @keyword OR code LIKE @keyword)";
                parameters.Add(new SqliteParameter("@keyword", $"%{keyword}%"));
            }

            if (!string.IsNullOrEmpty(seriesId))
            {
                whereClause += " AND seriesid = @seriesId";
                parameters.Add(new SqliteParameter("@seriesId", seriesId));
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
                SELECT v.*, s.name as series_name
                FROM videos v
                LEFT JOIN video_series s ON v.seriesid = s.id
                {whereClause}
                ORDER BY v.added_at DESC
                LIMIT @pageSize OFFSET @offset";
            
            parameters.Add(new SqliteParameter("@pageSize", pageSize));
            parameters.Add(new SqliteParameter("@offset", offset));

            using var conn = GetConnection();
            conn.Open();
            
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddRange(parameters.ToArray());
            
            using var reader = cmd.ExecuteReader();
            
            var videos = new List<object>();
            while (reader.Read())
            {
                videos.Add(ReadVideoRow(reader, withSeriesName: true));
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
    /// 获取所有已有的分类、国家、系列（用于表单下拉）
    /// </summary>
    [HttpGet("meta")]
    public IActionResult GetMeta()
    {
        try
        {
            using var conn = GetConnection();
            conn.Open();

            var categories = new List<string>();
            var countries = new List<string>();
            var series = new List<object>();

            using (var catCmd = new SqliteCommand("SELECT DISTINCT category FROM videos WHERE category != '' ORDER BY category", conn))
            using (var reader = catCmd.ExecuteReader())
            {
                while (reader.Read()) categories.Add(reader.GetString(0));
            }

            using (var countryCmd = new SqliteCommand("SELECT DISTINCT country FROM videos WHERE country != '' ORDER BY country", conn))
            using (var reader = countryCmd.ExecuteReader())
            {
                while (reader.Read()) countries.Add(reader.GetString(0));
            }

            using (var seriesCmd = new SqliteCommand("SELECT id, name FROM video_series ORDER BY name", conn))
            using (var reader = seriesCmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    series.Add(new { id = reader["id"].ToString(), name = reader["name"].ToString() });
                }
            }

            return Ok(new { success = true, categories, countries, series });
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
            var sql = @"
                SELECT v.*, s.name as series_name
                FROM videos v
                LEFT JOIN video_series s ON v.seriesid = s.id
                WHERE v.id = @id";
            using var conn = GetConnection();
            conn.Open();
            
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.Add(new SqliteParameter("@id", id));
            
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return NotFound(new { success = false, message = "视频不存在" });

            var video = ReadVideoRow(reader, withSeriesName: true);

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
                    alias = actorReader["alias"] == DBNull.Value ? null : actorReader["alias"].ToString(),
                    country = actorReader["country"] == DBNull.Value ? null : actorReader["country"].ToString(),
                    avatarPath = actorReader["avatar_path"] == DBNull.Value ? null : actorReader["avatar_path"].ToString()
                });
            }

            // 统计点赞数
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
            using var conn = GetConnection();
            conn.Open();

            // 检查视频是否存在
            using (var checkCmd = new SqliteCommand("SELECT id FROM videos WHERE id = @id", conn))
            {
                checkCmd.Parameters.Add(new SqliteParameter("@id", id));
                if (checkCmd.ExecuteScalar() == null)
                    return NotFound(new { success = false, message = "视频不存在" });
            }

            // 创建点赞记录表（如果不存在）
            using (var createCmd = new SqliteCommand(@"
                CREATE TABLE IF NOT EXISTS video_likes (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    video_id TEXT NOT NULL,
                    liked_at TEXT NOT NULL
                )", conn))
            {
                createCmd.ExecuteNonQuery();
            }

            // 插入点赞记录
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
            var id = Guid.NewGuid().ToString("N").ToUpper();
            var sql = @"
                INSERT INTO videos (id, code, name, category, country, file_path, file_size, cover_path, added_at, seriesid)
                VALUES (@id, @code, @name, @category, @country, @filePath, @fileSize, @coverPath, @addedAt, @seriesId)";
            
            using var conn = GetConnection();
            conn.Open();
            
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.Add(new SqliteParameter("@id", id));
            cmd.Parameters.Add(new SqliteParameter("@code", (object?)req.Code ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@name", req.Name));
            cmd.Parameters.Add(new SqliteParameter("@category", req.Category));
            cmd.Parameters.Add(new SqliteParameter("@country", req.Country ?? ""));
            // 文件路径为空时生成唯一占位，避免 UNIQUE 约束冲突
            var filePath = string.IsNullOrEmpty(req.FilePath) ? $"manual://{Guid.NewGuid().ToString("N").ToUpper()}" : req.FilePath;
            cmd.Parameters.Add(new SqliteParameter("@filePath", filePath));
            cmd.Parameters.Add(new SqliteParameter("@fileSize", req.FileSize ?? 0));
            cmd.Parameters.Add(new SqliteParameter("@coverPath", req.CoverPath ?? (object)DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@addedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
            cmd.Parameters.Add(new SqliteParameter("@seriesId", (object?)req.SeriesId ?? DBNull.Value));
            
            cmd.ExecuteNonQuery();

            // 关联演员
            if (req.ActorIds != null && req.ActorIds.Any())
            {
                foreach (var actorId in req.ActorIds)
                {
                    var relSql = "INSERT OR IGNORE INTO video_actors (id, video_id, actor_id, created_at) VALUES (@id, @videoId, @actorId, @createdAt)";
                    using var relCmd = new SqliteCommand(relSql, conn);
                    relCmd.Parameters.Add(new SqliteParameter("@id", Guid.NewGuid().ToString("N").ToUpper()));
                    relCmd.Parameters.Add(new SqliteParameter("@videoId", id));
                    relCmd.Parameters.Add(new SqliteParameter("@actorId", actorId));
                    relCmd.Parameters.Add(new SqliteParameter("@createdAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
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
            var sql = @"
                UPDATE videos SET 
                    code = @code,
                    name = @name, 

                    category = @category, 
                    country = @country,
                    file_path = @filePath, 
                    cover_path = @coverPath,


                    seriesid = @seriesId
                WHERE id = @id";

            using var conn = GetConnection();
            conn.Open();

            // 检查是否存在
            using var checkCmd = new SqliteCommand("SELECT COUNT(*) FROM videos WHERE id = @id", conn);
            checkCmd.Parameters.Add(new SqliteParameter("@id", id));
            if (Convert.ToInt32(checkCmd.ExecuteScalar()) == 0)
                return NotFound(new { success = false, message = "视频不存在" });

            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.Add(new SqliteParameter("@id", id));
            cmd.Parameters.Add(new SqliteParameter("@code", (object?)req.Code ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@name", req.Name));
            cmd.Parameters.Add(new SqliteParameter("@category", req.Category));
            cmd.Parameters.Add(new SqliteParameter("@country", req.Country ?? ""));
            cmd.Parameters.Add(new SqliteParameter("@filePath", req.FilePath));
            cmd.Parameters.Add(new SqliteParameter("@coverPath", req.CoverPath ?? (object)DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@seriesId", (object?)req.SeriesId ?? DBNull.Value));
            cmd.ExecuteNonQuery();

            // 更新演员关联
            if (req.ActorIds != null)
            {
                // 先删除所有旧关联
                using var delCmd = new SqliteCommand("DELETE FROM video_actors WHERE video_id = @videoId", conn);
                delCmd.Parameters.Add(new SqliteParameter("@videoId", id));
                delCmd.ExecuteNonQuery();

                // 重新添加
                foreach (var actorId in req.ActorIds)
                {
                    var relSql = "INSERT OR IGNORE INTO video_actors (id, video_id, actor_id, created_at) VALUES (@id, @videoId, @actorId, @createdAt)";
                    using var relCmd = new SqliteCommand(relSql, conn);
                    relCmd.Parameters.Add(new SqliteParameter("@id", Guid.NewGuid().ToString("N").ToUpper()));
                    relCmd.Parameters.Add(new SqliteParameter("@videoId", id));
                    relCmd.Parameters.Add(new SqliteParameter("@actorId", actorId));
                    relCmd.Parameters.Add(new SqliteParameter("@createdAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
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
            using var conn = GetConnection();
            conn.Open();

            // 删除演员关联
            using var delRelCmd = new SqliteCommand("DELETE FROM video_actors WHERE video_id = @videoId", conn);
            delRelCmd.Parameters.Add(new SqliteParameter("@videoId", id));
            delRelCmd.ExecuteNonQuery();

            // 删除视频
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

            using var conn = GetConnection();
            conn.Open();

            using var transaction = conn.BeginTransaction();
            var deleted = 0;
            var failed = 0;

            foreach (var id in req.Ids)
            {
                try
                {
                    // 删除演员关联
                    using var delRelCmd = new SqliteCommand("DELETE FROM video_actors WHERE video_id = @videoId", conn, transaction);
                    delRelCmd.Parameters.Add(new SqliteParameter("@videoId", id));
                    delRelCmd.ExecuteNonQuery();

                    // 删除视频
                    using var delCmd = new SqliteCommand("DELETE FROM videos WHERE id = @id", conn, transaction);
                    delCmd.Parameters.Add(new SqliteParameter("@id", id));
                    var rows = delCmd.ExecuteNonQuery();

                    if (rows > 0)
                        deleted++;
                    else
                        failed++;
                }
                catch
                {
                    failed++;
                }
            }

            transaction.Commit();

            return Ok(new
            {
                success = true,
                data = new
                {
                    deleted = deleted,
                    failed = failed
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BatchDeleteVideos failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 扫描所有配置目录
    /// </summary>
    [HttpPost("scan")]
    public IActionResult ScanAll()
    {
        try
        {
            using var conn = GetConnection();
            conn.Open();

            // 读取启用的扫描目录
            var dirs = new List<(string id, string path, string videoTypes, bool recursive)>();
            using (var dirCmd = new SqliteCommand(
                "SELECT id, path, video_types, recursive FROM scan_directories WHERE is_enabled = 1", conn))
            using (var reader = dirCmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    dirs.Add((
                        reader["id"].ToString()!,
                        reader["path"].ToString()!,
                        reader["video_types"].ToString()!,
                        Convert.ToInt32(reader["recursive"]) == 1));
                }
            }

            if (dirs.Count == 0)
            {
                return Ok(new { success = false, message = "没有配置扫描目录" });
            }

            // 读取启用的视频类型
            var types = new List<(string name, string extensions)>();
            using (var typeCmd = new SqliteCommand(
                "SELECT name, extensions FROM video_types ORDER BY sort_order", conn))
            using (var reader = typeCmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    types.Add((reader["name"].ToString()!, reader["extensions"].ToString()!));
                }
            }

            if (types.Count == 0)
            {
                return Ok(new { success = false, message = "没有配置视频类型" });
            }

            // 创建扫描任务
            var taskId = 0;
            var insertTaskSql = @"
                INSERT INTO scan_tasks (task_type, status, target_path, started_at)
                VALUES ('all', 'pending', @targetPath, @startedAt);
                SELECT last_insert_rowid();";
            using (var taskCmd = new SqliteCommand(insertTaskSql, conn))
            {
                taskCmd.Parameters.Add(new SqliteParameter("@targetPath", "all"));
                taskCmd.Parameters.Add(new SqliteParameter("@startedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
                taskId = Convert.ToInt32(taskCmd.ExecuteScalar());
            }

            // 异步执行扫描
            _ = Task.Run(() => ScanAllDirectoriesAsync(dirs, types, taskId));

            return Ok(new { success = true, data = new { taskId = taskId }, message = "扫描任务已启动" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ScanAll failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 扫描单个目录
    /// </summary>
    [HttpPost("scan-directory")]
    public IActionResult ScanDirectory([FromBody] ScanRequest req)
    {
        try
        {
            if (string.IsNullOrEmpty(req.TargetPath) || !Directory.Exists(req.TargetPath))
            {
                return Ok(new { success = false, message = "扫描目录不存在" });
            }

            using var conn = GetConnection();
            conn.Open();

            var taskId = 0;
            var sql = @"
                INSERT INTO scan_tasks (task_type, status, target_path, started_at)
                VALUES ('manual', 'pending', @targetPath, @startedAt);
                SELECT last_insert_rowid();";
            
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.Add(new SqliteParameter("@targetPath", req.TargetPath));
            cmd.Parameters.Add(new SqliteParameter("@startedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
            
            taskId = Convert.ToInt32(cmd.ExecuteScalar());

            // 异步执行扫描
            _ = Task.Run(() => ScanDirectoryAsync(req.TargetPath, req.Recursive, taskId));

            return Ok(new { success = true, data = new { taskId = taskId }, message = "扫描任务已启动" });
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
            using var conn = GetConnection();
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
                    started_at = reader["started_at"].ToString(),
                    completed_at = reader["completed_at"]?.ToString(),
                    files_found = reader["files_found"] == DBNull.Value ? 0 : Convert.ToInt32(reader["files_found"]),
                    files_added = reader["files_added"] == DBNull.Value ? 0 : Convert.ToInt32(reader["files_added"]),
                    files_updated = reader["files_updated"] == DBNull.Value ? 0 : Convert.ToInt32(reader["files_updated"])
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
            using var conn = GetConnection();
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

    private object ReadVideoRow(SqliteDataReader reader, bool withSeriesName = false)
    {
        var result = new Dictionary<string, object?>
        {
            ["id"] = reader["id"].ToString(),
            ["code"] = reader["code"] == DBNull.Value ? null : reader["code"].ToString(),
            ["name"] = reader["name"].ToString(),
            ["category"] = reader["category"] == DBNull.Value ? "" : reader["category"].ToString(),
            ["country"] = reader["country"] == DBNull.Value ? "" : reader["country"].ToString(),
            ["filePath"] = reader["file_path"].ToString(),
            ["fileSize"] = reader["file_size"] == DBNull.Value ? 0 : Convert.ToInt64(reader["file_size"]),
            ["coverPath"] = reader["cover_path"] == DBNull.Value ? null : reader["cover_path"].ToString(),
            ["addedAt"] = reader["added_at"].ToString(),
            ["seriesId"] = reader["seriesid"] == DBNull.Value ? null : reader["seriesid"].ToString()
        };

        if (withSeriesName)
        {
            result["seriesName"] = reader["series_name"] == DBNull.Value ? null : reader["series_name"].ToString();
        }

        return result;
    }

    private object? ExecuteScalar(string sql, SqliteParameter[] parameters)
    {
        using var conn = GetConnection();
        conn.Open();
        
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddRange(parameters);
        
        return cmd.ExecuteScalar();
    }

    private void ScanAllDirectoriesAsync(List<(string id, string path, string videoTypes, bool recursive)> dirs, 
        List<(string name, string extensions)> types, int taskId)
    {
        try
        {
            var totalFilesFound = 0;
            var totalFilesAdded = 0;
            var totalFilesUpdated = 0;
            var errors = new List<string>();

            using var conn = GetConnection();
            conn.Open();

            // 先更新任务状态为 running
            using (var cmd = new SqliteCommand("UPDATE scan_tasks SET status = 'running' WHERE id = @taskId", conn))
            {
                cmd.Parameters.Add(new SqliteParameter("@taskId", taskId));
                cmd.ExecuteNonQuery();
            }

            foreach (var dir in dirs)
            {
                if (!Directory.Exists(dir.path)) continue;

                // 解析该目录要扫描的视频类型扩展名
                var typeNames = dir.videoTypes.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(t => t.Trim()).ToList();
                var extensions = types
                    .Where(t => typeNames.Contains(t.name, StringComparer.OrdinalIgnoreCase))
                    .SelectMany(t => t.extensions.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(e => e.Trim().ToLowerInvariant()))
                    .Distinct()
                    .ToList();

                if (extensions.Count == 0) continue;

                var enumOpts = new EnumerationOptions
                {
                    RecurseSubdirectories = dir.recursive,
                    IgnoreInaccessible = true
                };

                var allFiles = new List<string>();
                foreach (var ext in extensions)
                {
                    try
                    {
                        var pattern = $"*{ext}";
                        allFiles.AddRange(Directory.GetFiles(dir.path, pattern, enumOpts));
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"扫描目录 {dir.path} 扩展名 {ext} 失败: {ex.Message}");
                    }
                }

                allFiles = allFiles.Distinct().ToList();
                totalFilesFound += allFiles.Count;

                foreach (var filePath in allFiles)
                {
                    if (Path.GetFileName(filePath).StartsWith("._")) continue;

                    try
                    {
                        var (added, updated) = UpsertVideoFromFile(filePath, conn);
                        if (added) totalFilesAdded++;
                        if (updated) totalFilesUpdated++;
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{filePath}: {ex.Message}");
                    }
                }
            }

            // 更新任务状态为完成
            var updateTaskSql = @"UPDATE scan_tasks SET status = 'completed', completed_at = @completedAt, 
                                    files_found = @filesFound, files_added = @filesAdded, 
                                    files_updated = @filesUpdated, errors = @errors 
                                    WHERE id = @taskId";
            using (var updateTaskCmd = new SqliteCommand(updateTaskSql, conn))
            {
                updateTaskCmd.Parameters.Add(new SqliteParameter("@completedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
                updateTaskCmd.Parameters.Add(new SqliteParameter("@filesFound", totalFilesFound));
                updateTaskCmd.Parameters.Add(new SqliteParameter("@filesAdded", totalFilesAdded));
                updateTaskCmd.Parameters.Add(new SqliteParameter("@filesUpdated", totalFilesUpdated));
                updateTaskCmd.Parameters.Add(new SqliteParameter("@errors", errors.Any() ? JsonSerializer.Serialize(errors) : (object)DBNull.Value));
                updateTaskCmd.Parameters.Add(new SqliteParameter("@taskId", taskId));
                updateTaskCmd.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            UpdateScanTaskFailed(taskId, ex.Message);
            _logger.LogError(ex, "ScanAllDirectoriesAsync failed");
        }
    }

    private void ScanDirectoryAsync(string targetPath, bool recursive, int taskId)
    {
        try
        {
            using var conn = GetConnection();
            conn.Open();

            using (var cmd = new SqliteCommand("UPDATE scan_tasks SET status = 'running' WHERE id = @taskId", conn))
            {
                cmd.Parameters.Add(new SqliteParameter("@taskId", taskId));
                cmd.ExecuteNonQuery();
            }

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

            foreach (var filePath in mp4Files)
            {
                if (Path.GetFileName(filePath).StartsWith("._")) continue;

                try
                {
                    var (added, updated) = UpsertVideoFromFile(filePath, conn);
                    if (added) filesAdded++;
                    if (updated) filesUpdated++;
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
            using (var updateTaskCmd = new SqliteCommand(updateTaskSql, conn))
            {
                updateTaskCmd.Parameters.Add(new SqliteParameter("@completedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
                updateTaskCmd.Parameters.Add(new SqliteParameter("@filesFound", filesFound));
                updateTaskCmd.Parameters.Add(new SqliteParameter("@filesAdded", filesAdded));
                updateTaskCmd.Parameters.Add(new SqliteParameter("@filesUpdated", filesUpdated));
                updateTaskCmd.Parameters.Add(new SqliteParameter("@errors", errors.Any() ? JsonSerializer.Serialize(errors) : (object)DBNull.Value));
                updateTaskCmd.Parameters.Add(new SqliteParameter("@taskId", taskId));
                updateTaskCmd.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            UpdateScanTaskFailed(taskId, ex.Message);
            _logger.LogError(ex, "ScanDirectoryAsync failed");
        }
    }

    private (bool Added, bool Updated) UpsertVideoFromFile(string filePath, SqliteConnection conn)
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        var fileInfo = new FileInfo(filePath);
        var coverPath = Path.ChangeExtension(filePath, ".jpg");
        var coverExists = System.IO.File.Exists(coverPath);
        var category = DetermineCategory(filePath);

        var checkSql = "SELECT COUNT(*) FROM videos WHERE file_path = @filePath";
        using var checkCmd = new SqliteCommand(checkSql, conn);
        checkCmd.Parameters.Add(new SqliteParameter("@filePath", filePath));
        var exists = Convert.ToInt32(checkCmd.ExecuteScalar()) > 0;

        if (exists)
        {
            var updateSql = @"UPDATE videos SET 
                file_size = @fileSize, 
                cover_path = @coverPath, 
                category = @category
                WHERE file_path = @filePath";
            using var updateCmd = new SqliteCommand(updateSql, conn);
            updateCmd.Parameters.Add(new SqliteParameter("@fileSize", fileInfo.Length));
            updateCmd.Parameters.Add(new SqliteParameter("@coverPath", coverExists ? coverPath : (object)DBNull.Value));
            updateCmd.Parameters.Add(new SqliteParameter("@category", category));
            updateCmd.Parameters.Add(new SqliteParameter("@filePath", filePath));
            updateCmd.ExecuteNonQuery();
            return (false, true);
        }
        else
        {
            var id = Guid.NewGuid().ToString("N").ToUpper();
            var insertSql = @"INSERT INTO videos (id, name, category, country, file_path, file_size, cover_path, added_at) 
                            VALUES (@id, @name, @category, @country, @filePath, @fileSize, @coverPath, @addedAt)";
            using var insertCmd = new SqliteCommand(insertSql, conn);
            insertCmd.Parameters.Add(new SqliteParameter("@id", id));
            insertCmd.Parameters.Add(new SqliteParameter("@name", fileName));
            insertCmd.Parameters.Add(new SqliteParameter("@category", category));
            insertCmd.Parameters.Add(new SqliteParameter("@country", ""));
            insertCmd.Parameters.Add(new SqliteParameter("@filePath", filePath));
            insertCmd.Parameters.Add(new SqliteParameter("@fileSize", fileInfo.Length));
            insertCmd.Parameters.Add(new SqliteParameter("@coverPath", coverExists ? coverPath : (object)DBNull.Value));
            insertCmd.Parameters.Add(new SqliteParameter("@addedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
            insertCmd.ExecuteNonQuery();
            return (true, false);
        }
    }

    private void UpdateScanTaskFailed(int taskId, string error)
    {
        try
        {
            using var conn = GetConnection();
            conn.Open();
            
            var updateTaskSql = @"UPDATE scan_tasks SET status = 'failed', completed_at = @completedAt, errors = @errors 
                                    WHERE id = @taskId";
            using var updateTaskCmd = new SqliteCommand(updateTaskSql, conn);
            updateTaskCmd.Parameters.Add(new SqliteParameter("@completedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
            updateTaskCmd.Parameters.Add(new SqliteParameter("@errors", error));
            updateTaskCmd.Parameters.Add(new SqliteParameter("@taskId", taskId));
            updateTaskCmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UpdateScanTaskFailed failed");
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
    public string? Code { get; set; }
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string Country { get; set; } = "";
    public string FilePath { get; set; } = "";
    public long? FileSize { get; set; }
    public string? CoverPath { get; set; }
    public List<string>? ActorIds { get; set; }
    public string? SeriesId { get; set; }
}

public class UpdateVideoRequest
{
    public string? Code { get; set; }
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string Country { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string? CoverPath { get; set; }
    public List<string>? ActorIds { get; set; }
    public string? SeriesId { get; set; }
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

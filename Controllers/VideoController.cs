using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.Json.Serialization;
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
        [FromQuery] string? seriesId = null,
        [FromQuery] bool? hasFile = null,
        [FromQuery] int? mediaAttrFlags = null,
        [FromQuery] bool? prioritizeUnrated = null,
        [FromQuery] string? sortBy = null)
    {
        try
        {
            var offset = (pageIndex - 1) * pageSize;
            var whereClause = "WHERE 1=1";
            var parameters = new List<SqliteParameter>();

            // 排序：首页分类板块优先 media_attr_flags=0，非0同等优先级
            var orderBy = sortBy?.ToLower() switch
            {
                "code" => "v.code ASC",
                "name" => "v.name ASC",
                "likecount" => "like_count DESC",
                _ => prioritizeUnrated == true
                    ? "CASE WHEN v.media_attr_flags = 0 THEN 0 ELSE 1 END, v.ctime DESC"
                    : "v.ctime DESC"
            };

            if (!string.IsNullOrEmpty(category))
            {
                whereClause += " AND v.category = @category";
                parameters.Add(new SqliteParameter("@category", category));
            }

            if (!string.IsNullOrEmpty(keyword))
            {
                whereClause += " AND (v.name LIKE @keyword OR v.code LIKE @keyword)";
                parameters.Add(new SqliteParameter("@keyword", $"%{keyword}%"));
            }

            if (!string.IsNullOrEmpty(seriesId))
            {
                whereClause += " AND v.seriesid = @seriesId";
                parameters.Add(new SqliteParameter("@seriesId", seriesId));
            }

            if (!string.IsNullOrEmpty(country))
            {
                whereClause += " AND v.country = @country";
                parameters.Add(new SqliteParameter("@country", country));
            }

            // 根据 hasFile 参数过滤
            if (hasFile.HasValue)
            {
                if (hasFile.Value)
                {
                    whereClause += " AND v.file_size > 0";
                }
                else
                {
                    whereClause += " AND (v.file_size IS NULL OR v.file_size <= 0)";
                }
            }

            // 根据 mediaAttrFlags 过滤
            if (mediaAttrFlags.HasValue)
            {
                whereClause += " AND v.media_attr_flags = @mediaAttrFlags";
                parameters.Add(new SqliteParameter("@mediaAttrFlags", mediaAttrFlags.Value));
            }

            // 获取总数
            var countSql = $"SELECT COUNT(*) FROM videos v {whereClause}";
            var total = Convert.ToInt32(ExecuteScalar(countSql, parameters.ToArray()));

            // 获取列表
            var sql = $@"
                SELECT v.*, s.name as series_name,
                       (SELECT COUNT(*) FROM video_likes WHERE video_id = v.id AND target_type='video') as like_count,
                       (SELECT GROUP_CONCAT(a.id || '|' || a.name, ',') FROM actors a JOIN video_actors va ON a.id = va.actor_id WHERE va.video_id = v.id) as actor_names
                FROM videos v
                LEFT JOIN video_series s ON v.seriesid = s.id
                {whereClause}
                ORDER BY " + orderBy + @"
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
    [HttpGet("autocode")]
    public IActionResult GetAutoCode()
    {
        try
        {
            using var conn = GetConnection();
            conn.Open();
            // 查找最大的 AUTOCODE-xxx 编号
            var sql = "SELECT code FROM videos WHERE code LIKE 'AUTOCODE-%' ORDER BY code DESC LIMIT 1";
            using var cmd = new SqliteCommand(sql, conn);
            var result = cmd.ExecuteScalar()?.ToString();
            int next = 1;
            if (!string.IsNullOrEmpty(result))
            {
                // 提取数字部分
                var parts = result.Split('-');
                if (parts.Length >= 2 && int.TryParse(parts[1], out int num))
                {
                    next = num + 1;
                }
            }
            // 循环检查确保编号不存在
            string code;
            do
            {
                code = $"AUTOCODE-{next:D3}";
                using var checkCmd = new SqliteCommand("SELECT COUNT(*) FROM videos WHERE code = @code", conn);
                checkCmd.Parameters.Add(new SqliteParameter("@code", code));
                if (Convert.ToInt32(checkCmd.ExecuteScalar()) > 0)
                {
                    next++;
                    continue;
                }
                break;
            } while (true);
            return Ok(new { success = true, code });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "生成自动编号失败");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

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

            // 首页配置
            string homePageCategories = "", homePageCategoryCount = "12";
            using (var sCmd = new SqliteCommand("SELECT name, content FROM system_settings WHERE name IN ('homePageCategories','homePageCategoryCount')", conn))
            using (var sReader = sCmd.ExecuteReader())
            {
                while (sReader.Read())
                {
                    var n = sReader.GetString(0);
                    var v = sReader.GetString(1);
                    if (n == "homePageCategories") homePageCategories = v;
                    if (n == "homePageCategoryCount") homePageCategoryCount = v;
                }
            }

            return Ok(new { success = true, categories, countries, series, homePageCategories, homePageCategoryCount });
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
                using var likeCmd = new SqliteCommand("SELECT COUNT(*) FROM video_likes WHERE video_id = @videoId AND target_type='video'", conn);
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
    /// 获取推荐影片
    /// </summary>
    [HttpGet("{id}/recommend")]
    public IActionResult GetRecommend(string id, [FromQuery] int limit = 8)
    {
        try
        {
            using var conn = GetConnection();
            conn.Open();

            // 获取当前影片信息
            var currentVideo = new { seriesId = "", actorIds = new List<string>() };
            using (var cmd = new SqliteCommand("SELECT seriesid FROM videos WHERE id = @id", conn))
            {
                cmd.Parameters.Add(new SqliteParameter("@id", id));
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    currentVideo = new { 
                        seriesId = reader["seriesid"] == DBNull.Value ? "" : reader["seriesid"].ToString(), 
                        actorIds = new List<string>() 
                    };
                }
            }

            // 获取当前影片的演员
            using (var actorCmd = new SqliteCommand("SELECT actor_id FROM video_actors WHERE video_id = @videoId", conn))
            {
                actorCmd.Parameters.Add(new SqliteParameter("@videoId", id));
                using var actorReader = actorCmd.ExecuteReader();
                while (actorReader.Read())
                {
                    currentVideo.actorIds.Add(actorReader["actor_id"].ToString());
                }
            }

            var recommendList = new List<object>();
            var excludeIds = new HashSet<string> { id };

            // 1. 优先获取同系列影片（不限制数量）
            if (!string.IsNullOrEmpty(currentVideo.seriesId))
            {
                var seriesSql = @"
                    SELECT v.id, v.code, v.name, v.category, v.country, v.cover_path, v.file_path, v.file_size, v.seriesid, v.ctime, v.media_attr_flags,
                        s.name as series_name,
                        (SELECT COUNT(*) FROM video_likes vl WHERE vl.video_id = v.id AND vl.target_type='video') as like_count
                    FROM videos v
                    LEFT JOIN video_series s ON v.seriesid = s.id
                    WHERE v.seriesid = @seriesId AND v.id != @currentId AND v.file_size > 0
                    ORDER BY RANDOM()
                    LIMIT 20";
                
                using var seriesCmd = new SqliteCommand(seriesSql, conn);
                seriesCmd.Parameters.Add(new SqliteParameter("@seriesId", currentVideo.seriesId));
                seriesCmd.Parameters.Add(new SqliteParameter("@currentId", id));
                using var seriesReader = seriesCmd.ExecuteReader();
                while (seriesReader.Read())
                {
                    var vid = seriesReader["id"].ToString();
                    if (!excludeIds.Contains(vid))
                    {
                        recommendList.Add(ReadVideoFromReader(seriesReader));
                        excludeIds.Add(vid);
                    }
                }
            }

            // 2. 如果同系列影片不够，获取同演员影片
            if (recommendList.Count < limit && currentVideo.actorIds.Count > 0)
            {
                var actorIdsParam = string.Join(",", currentVideo.actorIds.Select(a => $"'{a}'"));
                var actorSql = $@"
                    SELECT DISTINCT v.id, v.code, v.name, v.category, v.country, v.cover_path, v.file_path, v.file_size, v.seriesid, v.ctime, v.media_attr_flags,
                        s.name as series_name,
                        (SELECT COUNT(*) FROM video_likes vl WHERE vl.video_id = v.id AND vl.target_type='video') as like_count
                    FROM videos v
                    INNER JOIN video_actors va ON v.id = va.video_id
                    LEFT JOIN video_series s ON v.seriesid = s.id
                    WHERE va.actor_id IN ({actorIdsParam}) AND v.file_size > 0
                    ORDER BY RANDOM()
                    LIMIT 50";
                
                using var actorCmd = new SqliteCommand(actorSql, conn);
                using var actorReader = actorCmd.ExecuteReader();
                while (actorReader.Read() && recommendList.Count < limit)
                {
                    var vid = actorReader["id"].ToString();
                    if (!excludeIds.Contains(vid))
                    {
                        recommendList.Add(ReadVideoFromReader(actorReader));
                        excludeIds.Add(vid);
                    }
                }
            }

            // 3. 如果还不够，随机获取其他影片
            if (recommendList.Count < limit)
            {
                var randomSql = @"
                    SELECT v.id, v.code, v.name, v.category, v.country, v.cover_path, v.file_path, v.file_size, v.seriesid, v.ctime, v.media_attr_flags,
                        s.name as series_name,
                        (SELECT COUNT(*) FROM video_likes vl WHERE vl.video_id = v.id AND vl.target_type='video') as like_count
                    FROM videos v
                    LEFT JOIN video_series s ON v.seriesid = s.id
                    WHERE v.file_size > 0
                    ORDER BY RANDOM()
                    LIMIT 50";
                
                using var randomCmd = new SqliteCommand(randomSql, conn);
                using var randomReader = randomCmd.ExecuteReader();
                while (randomReader.Read() && recommendList.Count < limit)
                {
                    var vid = randomReader["id"].ToString();
                    if (!excludeIds.Contains(vid))
                    {
                        recommendList.Add(ReadVideoFromReader(randomReader));
                        excludeIds.Add(vid);
                    }
                }
            }

            return Ok(new { success = true, data = recommendList });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取推荐影片失败");
            return Ok(new { success = false, message = "获取推荐影片失败" });
        }
    }

    private object ReadVideoFromReader(SqliteDataReader reader)
    {
        return new
        {
            id = reader["id"].ToString(),
            code = reader["code"] == DBNull.Value ? null : reader["code"].ToString(),
            name = reader["name"].ToString(),
            category = reader["category"] == DBNull.Value ? null : reader["category"].ToString(),
            country = reader["country"] == DBNull.Value ? null : reader["country"].ToString(),
            coverPath = reader["cover_path"] == DBNull.Value ? null : reader["cover_path"].ToString(),
            filePath = reader["file_path"] == DBNull.Value ? null : reader["file_path"].ToString(),
            fileSize = reader["file_size"] == DBNull.Value ? 0 : Convert.ToInt64(reader["file_size"]),
            seriesId = reader["seriesid"] == DBNull.Value ? null : reader["seriesid"].ToString(),
            seriesName = reader["series_name"] == DBNull.Value ? null : reader["series_name"].ToString(),
            likeCount = reader["like_count"] == DBNull.Value ? 0 : Convert.ToInt32(reader["like_count"]),
            ctime = reader["ctime"].ToString()
        };
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
            using (var insertCmd = new SqliteCommand("INSERT INTO video_likes (video_id, liked_at, target_type) VALUES (@videoId, @likedAt, 'video')", conn))
            {
                insertCmd.Parameters.Add(new SqliteParameter("@videoId", id));
                insertCmd.Parameters.Add(new SqliteParameter("@likedAt", likedAt));
                insertCmd.ExecuteNonQuery();
            }

            // 统计点赞数
            int likeCount = 0;
            using (var countCmd = new SqliteCommand("SELECT COUNT(*) FROM video_likes WHERE video_id = @videoId AND target_type='video'", conn))
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
                INSERT INTO videos (id, code, name, category, country, file_path, file_size, cover_path, ctime, seriesid)
                VALUES (@id, @code, @name, @category, @country, @filePath, @fileSize, @coverPath, @addedAt, @seriesId)";
            
            using var conn = GetConnection();
            conn.Open();
            
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.Add(new SqliteParameter("@id", id));
            cmd.Parameters.Add(new SqliteParameter("@code", (object?)req.Code ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@name", req.Name));
            cmd.Parameters.Add(new SqliteParameter("@category", req.Category));
            cmd.Parameters.Add(new SqliteParameter("@country", req.Country ?? ""));
            cmd.Parameters.Add(new SqliteParameter("@filePath", req.FilePath ?? (object)DBNull.Value));
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
                    var relSql = "INSERT OR IGNORE INTO video_actors (video_id, actor_id) VALUES (@videoId, @actorId)";
                    using var relCmd = new SqliteCommand(relSql, conn);
                    relCmd.Parameters.AddWithValue("@videoId", id);
                    relCmd.Parameters.AddWithValue("@actorId", actorId);
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
                    var relSql = "INSERT OR IGNORE INTO video_actors (video_id, actor_id) VALUES (@videoId, @actorId)";
                    using var relCmd = new SqliteCommand(relSql, conn);
                    relCmd.Parameters.AddWithValue("@videoId", id);
                    relCmd.Parameters.AddWithValue("@actorId", actorId);
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
            string? videoName = null, videoCode = null, videoCategory = null;
            string? currentFilePath = null, currentCoverPath = null;
            long currentFileSize = 0;
            using (var getCmd = new SqliteCommand(
                "SELECT name, code, category, file_path, cover_path, file_size FROM videos WHERE id = @id", conn))
            {
                getCmd.Parameters.Add(new SqliteParameter("@id", id));
                using var reader = getCmd.ExecuteReader();
                if (!reader.Read())
                    return NotFound(new { success = false, message = "视频不存在" });
                videoName = reader["name"]?.ToString();
                videoCode = reader["code"]?.ToString();
                videoCategory = reader["category"]?.ToString();
                var rawFp = reader["file_path"] == DBNull.Value ? null : reader["file_path"]?.ToString();
                currentFilePath = rawFp;
                currentCoverPath = reader["cover_path"] == DBNull.Value ? null : reader["cover_path"]?.ToString();
                currentFileSize = reader["file_size"] == DBNull.Value ? 0 : Convert.ToInt64(reader["file_size"]);
            }

            // 获取扫描目录（含分类）
            var scanDirs = new List<(string path, string category)>();
            using (var dirCmd = new SqliteCommand(
                "SELECT path, category FROM scan_directories", conn))
            using (var reader = dirCmd.ExecuteReader())
            {
                while (reader.Read())
                    scanDirs.Add((reader.GetString(0), reader["category"]?.ToString() ?? ""));
            }

            // 获取 scanType 扩展名
            var scanType = "";
            using (var stCmd = new SqliteCommand(
                "SELECT content FROM system_settings WHERE name = 'scanType'", conn))
                scanType = stCmd.ExecuteScalar()?.ToString() ?? "";
            var extensions = ParseScanType(scanType);

            var newFilePath = currentFilePath;
            long? newFileSize = null;
            string? newCoverPath = currentCoverPath;
            var messages = new List<string>();

            // ===== 1. 处理 file_path =====
            if (!string.IsNullOrEmpty(currentFilePath))
            {
                // 有路径 → 检查文件是否存在，重算大小
                if (System.IO.File.Exists(currentFilePath))
                {
                    var fi = new System.IO.FileInfo(currentFilePath);
                    newFileSize = fi.Length;
                    using var updCmd = new SqliteCommand("UPDATE videos SET file_size = @fs WHERE id = @id", conn);
                    updCmd.Parameters.Add(new SqliteParameter("@fs", fi.Length));
                    updCmd.Parameters.Add(new SqliteParameter("@id", id));
                    updCmd.ExecuteNonQuery();
                    messages.Add($"文件大小已更新: {FormatFileSize(newFileSize.Value)}");
                }
                else
                {
                    messages.Add("文件路径存在但文件不存在: " + (currentFilePath ?? "(空)"));
                    // 尝试找回
                    var found = TryFindVideoFile(conn, id, videoName, videoCode, scanDirs, extensions, ref newFilePath, ref newFileSize);
                    if (found) messages.Add("已在扫描目录中找到并更新文件路径");
                    else messages.Add("在扫描目录中未找到匹配文件");
                }
            }
            else
            {
                // 无路径 → 在同分类扫描目录中搜索
                messages.Add("未配置文件路径，开始搜索同分类目录...");
                var found = TryFindVideoFile(conn, id, videoName, videoCode, scanDirs, extensions, ref newFilePath, ref newFileSize);
                if (found) messages.Add("在扫描目录中找到匹配文件");
                else messages.Add("在扫描目录中未找到匹配文件");
            }

            // ===== 2. 处理 cover_path =====
            if (string.IsNullOrEmpty(newCoverPath))
            {
                var coverFound = TryFindCover(conn, id, videoName, videoCode, newFilePath, scanDirs, ref newCoverPath);
                if (!string.IsNullOrEmpty(newCoverPath))
                    messages.Add("封面已找回");
                else
                    messages.Add("未找到封面");
            }

            // 只有 file_size 发生变化时才更新 ctime，使其排在前面；同时重置 media_attr_flags 为 0
            long finalFileSize = newFileSize ?? 0;
            bool sizeChanged = finalFileSize != currentFileSize;
            if (sizeChanged)
            {
                using var timeCmd = new SqliteCommand("UPDATE videos SET ctime = @ctime, media_attr_flags = 0 WHERE id = @id", conn);
                timeCmd.Parameters.Add(new SqliteParameter("@ctime", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
                timeCmd.Parameters.Add(new SqliteParameter("@id", id));
                timeCmd.ExecuteNonQuery();
            }
            else
            {
                // file_size 未变化，只重置 media_attr_flags
                using var flagsCmd = new SqliteCommand("UPDATE videos SET media_attr_flags = 0 WHERE id = @id", conn);
                flagsCmd.Parameters.Add(new SqliteParameter("@id", id));
                flagsCmd.ExecuteNonQuery();
            }

            return Ok(new { success = true, data = new { filePath = newFilePath, fileSize = newFileSize, coverPath = newCoverPath }, message = string.Join("；", messages) });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ResetFileSize failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
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
            return StatusCode(500, new { success = false, message = ex.Message });
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
            return StatusCode(500, new { success = false, message = ex.Message });
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
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 在同分类扫描目录中搜索匹配的视频文件
    /// </summary>
    private bool TryFindVideoFile(SqliteConnection conn, string videoId, string? name, string? code,
        List<(string path, string category)> scanDirs, List<string> extensions,
        ref string? outFilePath, ref long? outFileSize)
    {
        // 过滤非法路径字符，防止 Directory.GetFiles 崩溃
        var invalidChars = System.IO.Path.GetInvalidFileNameChars();
        var searchKeys = new List<string>();
        if (!string.IsNullOrEmpty(code)) {
            var clean = new string(code.Where(c => !invalidChars.Contains(c)).ToArray());
            if (!string.IsNullOrWhiteSpace(clean)) searchKeys.Add(clean);
        }
        if (!string.IsNullOrEmpty(name)) {
            var clean = new string(name.Where(c => !invalidChars.Contains(c)).ToArray());
            if (!string.IsNullOrWhiteSpace(clean)) searchKeys.Add(clean);
        }
        if (searchKeys.Count == 0) return false;

        foreach (var dir in scanDirs)
        {
            if (!Directory.Exists(dir.path)) continue;
            var enumOpts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (var ext in extensions)
            {
                foreach (var key in searchKeys)
                {
                    var searchPattern = key + ext;
                    foreach (var found in Directory.GetFiles(dir.path, searchPattern, enumOpts))
                    {
                        if (Path.GetFileName(found).StartsWith("._")) continue;
                        var fi = new System.IO.FileInfo(found);
                        outFilePath = found;
                        outFileSize = fi.Length;

                        // 直接更新（UNIQUE 约束兜底；同一视频重复执行则为幂等操作）
                        using var updCmd = new SqliteCommand(
                            "UPDATE videos SET file_path = @fp, file_size = @fs WHERE id = @id", conn);
                        updCmd.Parameters.Add(new SqliteParameter("@fp", found));
                        updCmd.Parameters.Add(new SqliteParameter("@fs", fi.Length));
                        updCmd.Parameters.Add(new SqliteParameter("@id", videoId));
                        updCmd.ExecuteNonQuery();
                        return true;
                    }
                }
            }
        }
        return false;
    }

    /// <summary>
    /// 搜索封面路径：同目录 + /cover/ 目录
    /// </summary>
    private bool TryFindCover(SqliteConnection conn, string videoId, string? name, string? code,
        string? filePath, List<(string path, string category)> scanDirs, ref string? outCoverPath)
    {
        var invalidChars = System.IO.Path.GetInvalidFileNameChars();
        var searchKeys = new List<string>();
        if (!string.IsNullOrEmpty(code)) {
            var clean = new string(code.Where(c => !invalidChars.Contains(c)).ToArray());
            if (!string.IsNullOrWhiteSpace(clean)) searchKeys.Add(clean);
        }
        if (!string.IsNullOrEmpty(name)) {
            var clean = new string(name.Where(c => !invalidChars.Contains(c)).ToArray());
            if (!string.IsNullOrWhiteSpace(clean)) searchKeys.Add(clean);
        }
        if (searchKeys.Count == 0) return false;

        // 1. 同目录 .jpg
        if (!string.IsNullOrEmpty(filePath))
        {
            try
            {
                var dir = Path.GetDirectoryName(filePath) ?? "";
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    foreach (var key in searchKeys)
                    {
                        var cover = Path.Combine(dir, key + ".jpg");
                        if (System.IO.File.Exists(cover)) { outCoverPath = cover; UpdateCover(conn, videoId, cover); return true; }
                    }
                    foreach (var f in Directory.GetFiles(dir, "*.jpg", SearchOption.TopDirectoryOnly))
                    {
                        try {
                            var fn = Path.GetFileNameWithoutExtension(f);
                            if (searchKeys.Any(k => fn.Equals(k, StringComparison.OrdinalIgnoreCase) || fn.Contains(k, StringComparison.OrdinalIgnoreCase)))
                            { outCoverPath = f; UpdateCover(conn, videoId, f); return true; }
                        } catch { continue; }
                    }
                }
            } catch { /* ignore */ }
        }

        // 2. 磁盘根目录 /cover/ 文件夹
        foreach (var key in searchKeys)
        {
            foreach (var dir in scanDirs)
            {
                string? mountPoint = null;
                try {
                    if (dir.path.StartsWith("/Volumes/")) {
                        var parts = dir.path.Split('/');
                        if (parts.Length >= 3) mountPoint = "/" + parts[1] + "/" + parts[2];
                    } else {
                        mountPoint = Directory.GetDirectoryRoot(dir.path);
                    }
                } catch { continue; }
                if (string.IsNullOrEmpty(mountPoint)) continue;
                var coverDir = Path.Combine(mountPoint, "cover");
                if (Directory.Exists(coverDir))
                {
                    foreach (var coverFile in Directory.GetFiles(coverDir, "*.jpg", SearchOption.TopDirectoryOnly))
                    {
                        var fn = Path.GetFileNameWithoutExtension(coverFile);
                        if (fn.Equals(key, StringComparison.OrdinalIgnoreCase))
                        {
                            outCoverPath = coverFile; UpdateCover(conn, videoId, coverFile); return true;
                        }
                    }
                }
            }
        }
        return false;
    }

    private void UpdateCover(SqliteConnection conn, string videoId, string coverPath)
    {
        using var updCmd = new SqliteCommand("UPDATE videos SET cover_path = @cp WHERE id = @id", conn);
        updCmd.Parameters.Add(new SqliteParameter("@cp", coverPath));
        updCmd.Parameters.Add(new SqliteParameter("@id", videoId));
        updCmd.ExecuteNonQuery();
    }

    private string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        int order = 0; double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1) { order++; size /= 1024; }
        return Math.Round(size, 2) + " " + sizes[order];
    }

    /// <summary>
    /// 删除视频
    /// </summary>
    [HttpDelete("{id}")]
    public IActionResult DeleteVideo(string id, [FromQuery] bool deleteFiles = true)
    {
        try
        {
            // 先查询记录，获取文件路径
            string? filePath = null;
            string? coverPath = null;
            using (var conn = GetConnection())
            {
                conn.Open();
                using var queryCmd = new SqliteCommand("SELECT file_path, cover_path FROM videos WHERE id = @id", conn);
                queryCmd.Parameters.Add(new SqliteParameter("@id", id));
                using var reader = queryCmd.ExecuteReader();
                if (reader.Read())
                {
                    filePath = reader["file_path"]?.ToString();
                    coverPath = reader["cover_path"]?.ToString();
                }
            }

            using var conn2 = GetConnection();
            conn2.Open();

            // 删除演员关联
            using var delRelCmd = new SqliteCommand("DELETE FROM video_actors WHERE video_id = @videoId", conn2);
            delRelCmd.Parameters.Add(new SqliteParameter("@videoId", id));
            delRelCmd.ExecuteNonQuery();

            // 删除点赞记录
            using var delLikesCmd = new SqliteCommand("DELETE FROM video_likes WHERE video_id = @videoId", conn2);
            delLikesCmd.Parameters.Add(new SqliteParameter("@videoId", id));
            delLikesCmd.ExecuteNonQuery();

            // 删除视频
            using var delCmd = new SqliteCommand("DELETE FROM videos WHERE id = @id", conn2);
            delCmd.Parameters.Add(new SqliteParameter("@id", id));
            var rows = delCmd.ExecuteNonQuery();

            if (rows == 0)
                return NotFound(new { success = false, message = "视频不存在" });

            // 根据参数决定是否删除文件
            var deletedFiles = new List<string>();
            if (deleteFiles)
            {
                if (!string.IsNullOrEmpty(filePath) && System.IO.File.Exists(filePath))
                {
                    try
                    {
                        System.IO.File.Delete(filePath);
                        deletedFiles.Add(filePath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "删除视频文件失败: {FilePath}", filePath);
                    }
                }
                if (!string.IsNullOrEmpty(coverPath) && System.IO.File.Exists(coverPath))
                {
                    try
                    {
                        System.IO.File.Delete(coverPath);
                        deletedFiles.Add(coverPath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "删除封面文件失败: {CoverPath}", coverPath);
                    }
                }
            }

            return Ok(new { 
                success = true, 
                message = deleteFiles ? "删除成功" : "记录已删除，文件已保留",
                deletedFiles = deletedFiles,
                filesPreserved = !deleteFiles
            });
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
    public IActionResult BatchDeleteVideos([FromBody] BatchDeleteRequest req, [FromQuery] bool deleteFiles = true)
    {
        try
        {
            if (req.Ids == null || req.Ids.Count == 0)
                return BadRequest(new { success = false, message = "ids 不能为空" });

            // 先查询所有要删除的记录的文件路径
            var filesToDelete = new List<(string id, string? filePath, string? coverPath)>();
            using (var conn = GetConnection())
            {
                conn.Open();
                var ids = string.Join(",", req.Ids.Select(id => $"'{id}'"));
                using var queryCmd = new SqliteCommand($"SELECT id, file_path, cover_path FROM videos WHERE id IN ({ids})", conn);
                using var reader = queryCmd.ExecuteReader();
                while (reader.Read())
                {
                    filesToDelete.Add((
                        reader["id"].ToString()!,
                        reader["file_path"]?.ToString(),
                        reader["cover_path"]?.ToString()
                    ));
                }
            }

            using var conn2 = GetConnection();
            conn2.Open();

            using var transaction = conn2.BeginTransaction();
            var deleted = 0;
            var failed = 0;

            foreach (var id in req.Ids)
            {
                try
                {
                    // 删除演员关联
                    using var delRelCmd = new SqliteCommand("DELETE FROM video_actors WHERE video_id = @videoId", conn2, transaction);
                    delRelCmd.Parameters.Add(new SqliteParameter("@videoId", id));
                    delRelCmd.ExecuteNonQuery();

                    // 删除点赞记录
                    using var delLikesCmd = new SqliteCommand("DELETE FROM video_likes WHERE video_id = @videoId", conn2, transaction);
                    delLikesCmd.Parameters.Add(new SqliteParameter("@videoId", id));
                    delLikesCmd.ExecuteNonQuery();

                    // 删除视频
                    using var delCmd = new SqliteCommand("DELETE FROM videos WHERE id = @id", conn2, transaction);
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

            // 根据参数决定是否删除文件
            var deletedFiles = new List<string>();
            if (deleteFiles)
            {
                foreach (var (id, filePath, coverPath) in filesToDelete)
                {
                    if (!string.IsNullOrEmpty(filePath) && System.IO.File.Exists(filePath))
                    {
                        try
                        {
                            System.IO.File.Delete(filePath);
                            deletedFiles.Add(filePath);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "删除视频文件失败: {FilePath}", filePath);
                        }
                    }
                    if (!string.IsNullOrEmpty(coverPath) && System.IO.File.Exists(coverPath))
                    {
                        try
                        {
                            System.IO.File.Delete(coverPath);
                            deletedFiles.Add(coverPath);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "删除封面文件失败: {CoverPath}", coverPath);
                        }
                    }
                }
            }

            return Ok(new
            {
                success = true,
                data = new
                {
                    deleted = deleted,
                    failed = failed,
                    deletedFiles = deletedFiles
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

            // 读取扫描类型配置
            var scanType = "";
            using (var stCmd = new SqliteCommand("SELECT content FROM system_settings WHERE name = 'scanType'", conn))
            {
                var result = stCmd.ExecuteScalar();
                scanType = result?.ToString() ?? "";
            }

            if (string.IsNullOrEmpty(scanType))
                return Ok(new { success = false, message = "请先在基本信息中配置扫描类型" });

            // 读取扫描目录（含分类属性）
            var dirs = new List<(string path, string category, bool recursive, bool autoCreateSeries)>();
            using (var dirCmd = new SqliteCommand("SELECT path, category, recursive, auto_create_series FROM scan_directories", conn))
            using (var reader = dirCmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    dirs.Add((
                        reader["path"].ToString()!,
                        reader["category"] == DBNull.Value ? "" : reader["category"].ToString()!,
                        Convert.ToInt32(reader["recursive"]) == 1,
                        reader["auto_create_series"] == DBNull.Value ? false : Convert.ToInt32(reader["auto_create_series"]) == 1));
                }
            }

            if (dirs.Count == 0)
                return Ok(new { success = false, message = "没有配置扫描目录" });

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
            _ = Task.Run(() => ScanAllDirectoriesAsync(dirs, scanType, taskId));

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

            // 读取扫描类型配置
            var scanType = "";
            using (var stCmd = new SqliteCommand("SELECT content FROM system_settings WHERE name = 'scanType'", conn))
            {
                var result = stCmd.ExecuteScalar();
                scanType = result?.ToString() ?? "";
            }

            if (string.IsNullOrEmpty(scanType))
                return Ok(new { success = false, message = "请先在基本信息中配置扫描类型" });

            // 读取该目录的分类和自动创建系列设置
            var category = "";
            var autoCreateSeries = false;
            using (var catCmd = new SqliteCommand("SELECT category, auto_create_series FROM scan_directories WHERE path = @path", conn))
            {
                catCmd.Parameters.Add(new SqliteParameter("@path", req.TargetPath));
                using var reader = catCmd.ExecuteReader();
                if (reader.Read())
                {
                    category = reader["category"] == DBNull.Value ? "" : reader["category"].ToString();
                    autoCreateSeries = reader["auto_create_series"] != DBNull.Value && Convert.ToInt32(reader["auto_create_series"]) == 1;
                }
            }

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
            _ = Task.Run(() => ScanSingleDirectoryAsync(req.TargetPath, req.Recursive, category, autoCreateSeries, scanType, taskId));

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

    /// <summary>
    /// 检查字幕是否存在
    /// </summary>
    [HttpGet("{id}/subtitle/check")]
    public IActionResult CheckSubtitle(string id)
    {
        try
        {
            var sql = "SELECT file_path, name FROM videos WHERE id = @id";
            using var conn = GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.Add(new SqliteParameter("@id", id));
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return NotFound(new { success = false, message = "视频不存在" });
            var filePath = reader["file_path"]?.ToString();
            var videoName = reader["name"]?.ToString() ?? "";
            if (string.IsNullOrEmpty(filePath))
                return Ok(new { success = true, hasSubtitle = false });

            var subPath = FindSubtitleFile(filePath);
            if (subPath != null)
            {
                var ext = Path.GetExtension(subPath).ToLower();
                var ct = ext == ".vtt" ? "text/vtt" : "text/plain";
                return Ok(new { success = true, hasSubtitle = true, url = $"/api/video/{id}/subtitle", contentType = ct, ext });
            }
            return Ok(new { success = true, hasSubtitle = false });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CheckSubtitle failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 字幕流代理
    /// </summary>
    [HttpGet("{id}/subtitle")]
    public IActionResult GetSubtitle(string id)
    {
        try
        {
            var sql = "SELECT file_path FROM videos WHERE id = @id";
            using var conn = GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.Add(new SqliteParameter("@id", id));
            var filePath = cmd.ExecuteScalar()?.ToString();
            if (string.IsNullOrEmpty(filePath))
                return NotFound(new { success = false, message = "视频不存在" });

            var subPath = FindSubtitleFile(filePath);
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
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 根据视频路径查找字幕文件：../subtitle/同名.*
    /// </summary>
    private string? FindSubtitleFile(string videoFilePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(videoFilePath);
            if (string.IsNullOrEmpty(dir)) return null;
            // 父目录的兄弟目录 subtitle
            var parentDir = Path.GetDirectoryName(dir);
            if (string.IsNullOrEmpty(parentDir)) return null;
            var subDir = Path.Combine(parentDir, "subtitle");
            if (!Directory.Exists(subDir)) return null;

            var baseName = Path.GetFileNameWithoutExtension(videoFilePath);
            var extensions = new[] { ".srt", ".ass", ".ssa", ".vtt", ".sub" };
            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(subDir, baseName + ext);
                if (System.IO.File.Exists(candidate)) return candidate;
            }
            // 也尝试不带扩展名的同名文件
            var direct = Path.Combine(subDir, baseName);
            if (System.IO.File.Exists(direct)) return direct;
            return null;
        }
        catch { return null; }
    }


    #region 私有方法

    private List<string> ParseScanType(string scanType)
    {
        return scanType.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim().ToLowerInvariant())
            .Where(t => t.Length > 0)
            .Select(t => t.StartsWith(".") ? t : "." + t)
            .ToList();
    }

    private string ExtractVideoName(string filePath)
    {
        // 取文件名中第一个.前的部分
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        var dotIndex = fileName.IndexOf('.');
        if (dotIndex > 0)
            return fileName.Substring(0, dotIndex);
        return fileName;
    }

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
            ["addedAt"] = reader["ctime"].ToString(),
            ["seriesId"] = reader["seriesid"] == DBNull.Value ? null : reader["seriesid"].ToString(),
            ["mediaAttrFlags"] = HasColumn(reader, "media_attr_flags") ? (reader["media_attr_flags"] == DBNull.Value ? 0 : Convert.ToInt32(reader["media_attr_flags"])) : 0
        };

        if (HasColumn(reader, "like_count"))
        {
            result["likeCount"] = reader["like_count"] == DBNull.Value ? 0 : Convert.ToInt32(reader["like_count"]);
        }

        if (withSeriesName)
        {
            result["seriesName"] = reader["series_name"] == DBNull.Value ? null : reader["series_name"].ToString();
        }

        if (HasColumn(reader, "actor_names") && reader["actor_names"] != DBNull.Value)
        {
            result["actorNames"] = reader["actor_names"].ToString();
        }

        return result;
    }

    private bool HasColumn(SqliteDataReader reader, string columnName)
    {
        for (int i = 0; i < reader.FieldCount; i++)
        {
            if (reader.GetName(i).Equals(columnName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private object? ExecuteScalar(string sql, SqliteParameter[] parameters)
    {
        using var conn = GetConnection();
        conn.Open();
        
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddRange(parameters);
        
        return cmd.ExecuteScalar();
    }

    private void ScanAllDirectoriesAsync(List<(string path, string category, bool recursive, bool autoCreateSeries)> dirs, 
        string scanType, int taskId)
    {
        try
        {
            var totalFilesFound = 0;
            var totalFilesAdded = 0;
            var totalFilesCleared = 0;
            var errors = new List<string>();

            var extensions = ParseScanType(scanType);
            if (extensions.Count == 0)
            {
                UpdateScanTaskFailed(taskId, "扫描类型为空");
                return;
            }

            using var conn = GetConnection();
            conn.Open();

            using (var cmd = new SqliteCommand("UPDATE scan_tasks SET status = 'running' WHERE id = @taskId", conn))
            {
                cmd.Parameters.Add(new SqliteParameter("@taskId", taskId));
                cmd.ExecuteNonQuery();
            }

            // 第一遍：收集所有文件路径及对应的目录信息
            var pathToInfo = new Dictionary<string, (string category, bool autoCreateSeries)>();
            foreach (var dir in dirs)
            {
                if (!Directory.Exists(dir.path)) continue;

                var enumOpts = new EnumerationOptions
                {
                    RecurseSubdirectories = dir.recursive,
                    IgnoreInaccessible = true
                };

                foreach (var ext in extensions)
                {
                    try
                    {
                        var files = Directory.GetFiles(dir.path, $"*{ext}", enumOpts)
                            .Where(f => !Path.GetFileName(f).StartsWith("._"));
                        foreach (var file in files)
                            pathToInfo[file] = (dir.category, dir.autoCreateSeries);
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"扫描目录 {dir.path} 扩展名 {ext} 失败: {ex.Message}");
                    }
                }
            }

            totalFilesFound = pathToInfo.Count;

            // 第二遍：先清空不在扫描目录的旧记录，再插入/更新
            totalFilesCleared = ClearMissingPaths(conn, pathToInfo.Keys.ToHashSet());

            foreach (var kvp in pathToInfo)
            {
                try
                {
                    if (UpsertVideoFromFile(kvp.Key, kvp.Value.category, kvp.Value.autoCreateSeries, conn))
                        totalFilesAdded++;
                }
                catch (Exception ex)
                {
                    errors.Add($"{kvp.Key}: {ex.Message}");
                }
            }

            var updateTaskSql = @"UPDATE scan_tasks SET status = 'completed', completed_at = @completedAt, 
                                    files_found = @filesFound, files_added = @filesAdded, 
                                    files_updated = @filesCleared, errors = @errors 
                                    WHERE id = @taskId";
            using (var updateTaskCmd = new SqliteCommand(updateTaskSql, conn))
            {
                updateTaskCmd.Parameters.Add(new SqliteParameter("@completedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
                updateTaskCmd.Parameters.Add(new SqliteParameter("@filesFound", totalFilesFound));
                updateTaskCmd.Parameters.Add(new SqliteParameter("@filesAdded", totalFilesAdded));
                updateTaskCmd.Parameters.Add(new SqliteParameter("@filesCleared", totalFilesCleared));
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

    private void ScanSingleDirectoryAsync(string targetPath, bool recursive, string category, bool autoCreateSeries, string scanType, int taskId)
    {
        try
        {
            var extensions = ParseScanType(scanType);
            if (extensions.Count == 0)
            {
                UpdateScanTaskFailed(taskId, "扫描类型为空");
                return;
            }

            using var conn = GetConnection();
            conn.Open();

            using (var cmd = new SqliteCommand("UPDATE scan_tasks SET status = 'running' WHERE id = @taskId", conn))
            {
                cmd.Parameters.Add(new SqliteParameter("@taskId", taskId));
                cmd.ExecuteNonQuery();
            }

            var filesFound = 0;
            var filesAdded = 0;
            var errors = new List<string>();

            var enumOpts = new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                IgnoreInaccessible = true
            };

            var allFiles = new List<string>();
            foreach (var ext in extensions)
            {
                try
                {
                    allFiles.AddRange(Directory.GetFiles(targetPath, $"*{ext}", enumOpts));
                }
                catch (Exception ex)
                {
                    errors.Add($"扫描目录失败: {ex.Message}");
                }
            }
            allFiles = allFiles.Distinct().ToList();
            filesFound = allFiles.Count;

            foreach (var filePath in allFiles)
            {
                if (Path.GetFileName(filePath).StartsWith("._")) continue;

                try
                {
                    if (UpsertVideoFromFile(filePath, category, autoCreateSeries, conn))
                        filesAdded++;
                }
                catch (Exception ex)
                {
                    errors.Add($"{filePath}: {ex.Message}");
                }
            }

            var updateTaskSql = @"UPDATE scan_tasks SET status = 'completed', completed_at = @completedAt, 
                                    files_found = @filesFound, files_added = @filesAdded, 
                                    files_updated = 0, errors = @errors 
                                    WHERE id = @taskId";
            using (var updateTaskCmd = new SqliteCommand(updateTaskSql, conn))
            {
                updateTaskCmd.Parameters.Add(new SqliteParameter("@completedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
                updateTaskCmd.Parameters.Add(new SqliteParameter("@filesFound", filesFound));
                updateTaskCmd.Parameters.Add(new SqliteParameter("@filesAdded", filesAdded));
                updateTaskCmd.Parameters.Add(new SqliteParameter("@errors", errors.Any() ? JsonSerializer.Serialize(errors) : (object)DBNull.Value));
                updateTaskCmd.Parameters.Add(new SqliteParameter("@taskId", taskId));
                updateTaskCmd.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            UpdateScanTaskFailed(taskId, ex.Message);
            _logger.LogError(ex, "ScanSingleDirectoryAsync failed");
        }
    }

    /// <summary>
    /// 插入或更新视频记录，返回 true 表示新增
    /// </summary>
    private bool UpsertVideoFromFile(string filePath, string dirCategory, bool autoCreateSeries, SqliteConnection conn)
    {
        var videoName = ExtractVideoName(filePath);
        var fileInfo = new FileInfo(filePath);
        
        // 查询是否存在 name 或 code 与文件名一致的记录
        var checkSql = @"SELECT id FROM videos WHERE name = @videoName OR code = @videoName";
        string? existingId = null;
        using (var checkCmd = new SqliteCommand(checkSql, conn))
        {
            checkCmd.Parameters.Add(new SqliteParameter("@videoName", videoName));
            var result = checkCmd.ExecuteScalar();
            if (result != null && result != DBNull.Value)
            {
                existingId = result.ToString();
            }
        }
        
        // 计算 cover_path
        var coverPath = FindCoverPath(filePath, videoName);
        
        if (existingId != null)
        {
            // 已存在记录，更新 file_path、file_size、cover_path
            var updateSql = coverPath != null 
                ? @"UPDATE videos SET 
                    file_path = @filePath, 
                    file_size = @fileSize, 
                    cover_path = @coverPath
                    WHERE id = @id"
                : @"UPDATE videos SET 
                    file_path = @filePath, 
                    file_size = @fileSize
                    WHERE id = @id";
            using var updateCmd = new SqliteCommand(updateSql, conn);
            updateCmd.Parameters.Add(new SqliteParameter("@filePath", filePath));
            updateCmd.Parameters.Add(new SqliteParameter("@fileSize", fileInfo.Length));
            if (coverPath != null)
            {
                updateCmd.Parameters.Add(new SqliteParameter("@coverPath", coverPath));
            }
            updateCmd.Parameters.Add(new SqliteParameter("@id", existingId));
            updateCmd.ExecuteNonQuery();
            return false; // 更新而非新增
        }
        else
        {
            // 不存在记录，新增
            var id = Guid.NewGuid().ToString("N").ToUpper();
            
            // 自动创建系列
            string? seriesId = null;
            if (autoCreateSeries && !string.IsNullOrEmpty(videoName))
            {
                seriesId = GetOrCreateSeries(videoName, conn);
            }
            
            var insertSql = @"INSERT INTO videos (id, name, category, country, file_path, file_size, cover_path, seriesid, ctime) 
                            VALUES (@id, @name, @category, @country, @filePath, @fileSize, @coverPath, @seriesid, @addedAt)";
            using var insertCmd = new SqliteCommand(insertSql, conn);
            insertCmd.Parameters.Add(new SqliteParameter("@id", id));
            insertCmd.Parameters.Add(new SqliteParameter("@name", videoName));
            insertCmd.Parameters.Add(new SqliteParameter("@category", dirCategory));
            insertCmd.Parameters.Add(new SqliteParameter("@country", ""));
            insertCmd.Parameters.Add(new SqliteParameter("@filePath", filePath));
            insertCmd.Parameters.Add(new SqliteParameter("@fileSize", fileInfo.Length));
            insertCmd.Parameters.Add(new SqliteParameter("@coverPath", coverPath != null ? coverPath : (object)DBNull.Value));
            insertCmd.Parameters.Add(new SqliteParameter("@seriesid", seriesId != null ? seriesId : (object)DBNull.Value));
            insertCmd.Parameters.Add(new SqliteParameter("@addedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
            insertCmd.ExecuteNonQuery();
            return true; // 新增成功
        }
    }

    /// <summary>
    /// 获取或创建系列，返回系列ID
    /// </summary>
    private string? GetOrCreateSeries(string seriesName, SqliteConnection conn)
    {
        // 先查找是否存在
        var checkSql = "SELECT id FROM video_series WHERE name = @name";
        using (var checkCmd = new SqliteCommand(checkSql, conn))
        {
            checkCmd.Parameters.Add(new SqliteParameter("@name", seriesName));
            var result = checkCmd.ExecuteScalar();
            if (result != null && result != DBNull.Value)
            {
                return result.ToString();
            }
        }

        // 不存在则创建
        var id = Guid.NewGuid().ToString("N").ToUpper();
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var insertSql = "INSERT INTO video_series (id, name, ctime, utime) VALUES (@id, @name, @ctime, @utime)";
        using var insertCmd = new SqliteCommand(insertSql, conn);
        insertCmd.Parameters.Add(new SqliteParameter("@id", id));
        insertCmd.Parameters.Add(new SqliteParameter("@name", seriesName));
        insertCmd.Parameters.Add(new SqliteParameter("@ctime", now));
        insertCmd.Parameters.Add(new SqliteParameter("@utime", now));
        insertCmd.ExecuteNonQuery();
        
        return id;
    }

    /// <summary>
    /// 查找封面路径
    /// 优先级：1. 同目录下同名.jpg  2. 磁盘根目录/cover/{filename}.jpg
    /// </summary>
    private string? FindCoverPath(string filePath, string videoName)
    {
        // 1. 同目录下同名.jpg
        var sameDirCover = Path.ChangeExtension(filePath, ".jpg");
        if (System.IO.File.Exists(sameDirCover))
        {
            return sameDirCover;
        }
        
        // 2. 磁盘根目录/cover/{filename}.jpg
        // macOS: /Volumes/diskname/... → 提取 /Volumes/diskname
        try
        {
            string? mountPoint = null;
            if (filePath.StartsWith("/Volumes/"))
            {
                // 提取 /Volumes/xxx
                var parts = filePath.Split('/');
                if (parts.Length >= 3)
                {
                    mountPoint = "/" + parts[1] + "/" + parts[2]; // /Volumes/diskname
                }
            }
            else
            {
                // 非 /Volumes 路径，使用文件系统根目录
                mountPoint = Directory.GetDirectoryRoot(filePath);
            }
            
            if (!string.IsNullOrEmpty(mountPoint))
            {
                var coverDir = Path.Combine(mountPoint, "cover");
                if (Directory.Exists(coverDir))
                {
                    var coverFile = Path.Combine(coverDir, videoName + ".jpg");
                    if (System.IO.File.Exists(coverFile))
                    {
                        return coverFile;
                    }
                }
            }
        }
        catch
        {
            // 忽略路径解析错误
        }
        
        return null;
    }

    /// <summary>
    /// 清空不在扫描目录中的视频路径，返回清除数量
    /// </summary>
    private int ClearMissingPaths(SqliteConnection conn, HashSet<string> allFoundPaths)
    {
        // 读取所有扫描目录路径
        var scanDirPaths = new List<string>();
        using (var dirCmd = new SqliteCommand("SELECT path FROM scan_directories", conn))
        using (var reader = dirCmd.ExecuteReader())
        {
            while (reader.Read())
                scanDirPaths.Add(reader.GetString(0));
        }

        // 查询所有有文件路径的视频
        var videosWithPath = new List<(string id, string filePath)>();
        using (var videoCmd = new SqliteCommand("SELECT id, file_path FROM videos WHERE file_path IS NOT NULL AND file_path != ''", conn))
        using (var reader = videoCmd.ExecuteReader())
        {
            while (reader.Read())
                videosWithPath.Add((reader.GetString(0), reader.GetString(1)));
        }

        var cleared = 0;
        foreach (var (id, filePath) in videosWithPath)
        {
            // 跳过手动添加的占位路径
            if (string.IsNullOrEmpty(filePath)) continue;

            // 统一转小写比较路径
            var filePathLower = filePath.ToLowerInvariant();
            var inScanDir = scanDirPaths.Any(scanPath => 
                filePathLower.StartsWith(scanPath.ToLowerInvariant()));

            if (!inScanDir)
            {
                // 文件路径不在任何扫描目录下，清空路径
                using var clearCmd = new SqliteCommand(
                    "UPDATE videos SET file_path = NULL, file_size = 0 WHERE id = @id", conn);
                clearCmd.Parameters.Add(new SqliteParameter("@id", id));
                clearCmd.ExecuteNonQuery();
                cleared++;
            }
            else if (!allFoundPaths.Contains(filePath))
            {
                // 文件路径在扫描目录下但文件不存在了，清空路径
                using var clearCmd = new SqliteCommand(
                    "UPDATE videos SET file_path = NULL, file_size = 0 WHERE id = @id", conn);
                clearCmd.Parameters.Add(new SqliteParameter("@id", id));
                clearCmd.ExecuteNonQuery();
                cleared++;
            }
        }

        return cleared;
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

    // 今日推荐内存缓存：按天缓存，refresh=true 清除
    private static string? _dailyRecommendDate = null;
    private static List<object>? _dailyRecommendCache = null;
    private static readonly object _dailyRecommendLock = new();

    /// <summary>
    /// 首页 - 今日推荐（内存缓存，优先 media_attr_flags=0）
    /// </summary>
    [HttpGet("daily-recommend")]
    public IActionResult GetDailyRecommend([FromQuery] int count = 12, [FromQuery] bool refresh = false)
    {
        try
        {
            var today = DateTime.Now.ToString("yyyy-MM-dd");

            // 非刷新模式且日期一致 → 直接返回缓存
            if (!refresh && _dailyRecommendCache != null && _dailyRecommendDate == today)
            {
                return Ok(new { success = true, data = _dailyRecommendCache, cached = true });
            }

            using var conn = GetConnection();
            conn.Open();

            // 获取有文件的视频总数
            using var countCmd = new SqliteCommand(
                "SELECT COUNT(*) FROM videos WHERE file_size > 0", conn);
            int total = Convert.ToInt32(countCmd.ExecuteScalar());
            if (total == 0)
                return Ok(new { success = true, data = new List<object>() });

            // 随机种子
            int seed = (int)(DateTime.Now.Ticks % int.MaxValue);
            var rng = new Random(seed);

            // 单条 SQL：CASE WHEN 二分排序，优先 media_attr_flags=0，非0同等优先级
            int poolSize = Math.Min(total, Math.Max(count * 3, (int)(total * 0.6)));

            var sql = @"
                SELECT v.id, v.code, v.name, v.category, v.country, v.cover_path, v.file_path, v.file_size,
                       v.seriesid, v.ctime, v.media_attr_flags,
                       (SELECT COUNT(*) FROM video_likes WHERE video_id = v.id AND target_type='video') AS like_count,
                       s.name AS series_name,
                       (SELECT GROUP_CONCAT(a.id || '|' || a.name, ',') FROM actors a JOIN video_actors va ON a.id = va.actor_id WHERE va.video_id = v.id) as actor_names
                FROM videos v
                LEFT JOIN video_series s ON v.seriesid = s.id
                WHERE v.file_size > 0
                ORDER BY CASE WHEN v.media_attr_flags = 0 THEN 0 ELSE 1 END, like_count ASC, v.id
                LIMIT @limit";
            using var poolCmd = new SqliteCommand(sql, conn);
            poolCmd.Parameters.AddWithValue("@limit", poolSize);
            var pool = new List<object>();
            using (var reader = poolCmd.ExecuteReader())
            {
                while (reader.Read()) pool.Add(ReadVideoRow(reader, withSeriesName: true));
            }

            // 从候选池中随机选 count 条
            var indices = Enumerable.Range(0, pool.Count).OrderBy(_ => rng.Next()).Take(Math.Min(count, pool.Count)).ToList();
            var selected = new List<object>();
            foreach (var i in indices) selected.Add(pool[i]);

            // 写入内存缓存
            lock (_dailyRecommendLock)
            {
                _dailyRecommendDate = today;
                _dailyRecommendCache = selected;
            }

            return Ok(new { success = true, data = selected, cached = false });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetDailyRecommend failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 首页 - 最近点赞（同一视频多次点赞只取最新那次）
    /// </summary>
    [HttpGet("recently-liked")]
    public IActionResult GetRecentlyLiked([FromQuery] int count = 12)
    {
        try
        {
            using var conn = GetConnection();
            conn.Open();
            var sql = @"
                SELECT v.id, v.code, v.name, v.category, v.country, v.cover_path, v.file_path, v.file_size,
                       v.seriesid, v.ctime, v.media_attr_flags,
                       (SELECT COUNT(*) FROM video_likes WHERE video_id = v.id AND target_type='video') AS like_count,
                       s.name AS series_name,
                       (SELECT GROUP_CONCAT(a.id || '|' || a.name, ',') FROM actors a JOIN video_actors va ON a.id = va.actor_id WHERE va.video_id = v.id) as actor_names
                FROM video_likes vl
                JOIN videos v ON vl.video_id = v.id
                LEFT JOIN video_series s ON v.seriesid = s.id
                WHERE v.file_size > 0
                GROUP BY v.id
                ORDER BY MAX(vl.liked_at) DESC
                LIMIT @limit";
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@limit", count);
            var list = new List<object>();
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read()) list.Add(ReadVideoRow(reader, withSeriesName: true));
            }
            return Ok(new { success = true, data = list });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetRecentlyLiked failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 首页 - 高赞影片（点赞数相同取点赞时间最新的）
    /// </summary>
    [HttpGet("top-liked")]
    public IActionResult GetTopLiked([FromQuery] int count = 12)
    {
        try
        {
            using var conn = GetConnection();
            conn.Open();
            var sql = @"
                SELECT v.id, v.code, v.name, v.category, v.country, v.cover_path, v.file_path, v.file_size,
                       v.seriesid, v.ctime, v.media_attr_flags,
                       (SELECT COUNT(*) FROM video_likes WHERE video_id = v.id AND target_type='video') AS like_count,
                       s.name AS series_name,
                       (SELECT GROUP_CONCAT(a.id || '|' || a.name, ',') FROM actors a JOIN video_actors va ON a.id = va.actor_id WHERE va.video_id = v.id) as actor_names
                FROM video_likes vl
                JOIN videos v ON vl.video_id = v.id
                LEFT JOIN video_series s ON v.seriesid = s.id
                WHERE v.file_size > 0
                GROUP BY v.id
                ORDER BY like_count DESC, MAX(vl.liked_at) DESC
                LIMIT @limit";
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@limit", count);
            var list = new List<object>();
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read()) list.Add(ReadVideoRow(reader, withSeriesName: true));
            }
            return Ok(new { success = true, data = list });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetTopLiked failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 获取点赞日历统计
    /// </summary>
    [HttpGet("likes/stats")]
    public IActionResult GetLikeStats([FromQuery] int? year, [FromQuery] int? month)
    {
        try
        {
            var now = DateTime.Now;
            int targetYear = year ?? now.Year;
            int targetMonth = month ?? now.Month;

            var startDate = new DateTime(targetYear, targetMonth, 1).ToString("yyyy-MM-dd");
            var endDate = new DateTime(targetYear, targetMonth, 1).AddMonths(1).AddDays(-1).ToString("yyyy-MM-dd");

            using var conn = GetConnection();
            conn.Open();

            // 获取当月每日点赞数
            var dailySql = @"
                SELECT DATE(liked_at) as like_date, COUNT(*) as cnt
                FROM video_likes
                WHERE DATE(liked_at) >= @startDate AND DATE(liked_at) <= @endDate
                GROUP BY DATE(liked_at)
                ORDER BY like_date";
            using var dailyCmd = new SqliteCommand(dailySql, conn);
            dailyCmd.Parameters.Add(new SqliteParameter("@startDate", startDate));
            dailyCmd.Parameters.Add(new SqliteParameter("@endDate", endDate));

            var dailyStats = new Dictionary<string, int>();
            using (var reader = dailyCmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    dailyStats[reader.GetString(0)] = reader.GetInt32(1);
                }
            }

            // 当月总点赞数
            var monthTotalSql = "SELECT COUNT(*) FROM video_likes WHERE DATE(liked_at) >= @s AND DATE(liked_at) <= @e";
            using var monthTotalCmd = new SqliteCommand(monthTotalSql, conn);
            monthTotalCmd.Parameters.Add(new SqliteParameter("@s", startDate));
            monthTotalCmd.Parameters.Add(new SqliteParameter("@e", endDate));
            int monthTotal = Convert.ToInt32(monthTotalCmd.ExecuteScalar());

            // 历史总点赞数
            using var totalCmd = new SqliteCommand("SELECT COUNT(*) FROM video_likes", conn);
            int total = Convert.ToInt32(totalCmd.ExecuteScalar());

            // 最后一次点赞日期
            using var lastCmd = new SqliteCommand("SELECT MAX(DATE(liked_at)) FROM video_likes", conn);
            var lastLikeDate = lastCmd.ExecuteScalar()?.ToString();

            // 最近12个月每月统计
            var monthlyStats = new List<object>();
            for (int i = 11; i >= 0; i--)
            {
                var m = now.AddMonths(-i);
                var mStart = new DateTime(m.Year, m.Month, 1).ToString("yyyy-MM-dd");
                var mEnd = new DateTime(m.Year, m.Month, 1).AddMonths(1).AddDays(-1).ToString("yyyy-MM-dd");
                var mSql = "SELECT COUNT(*) FROM video_likes WHERE DATE(liked_at) >= @s AND DATE(liked_at) <= @e";
                using var mCmd = new SqliteCommand(mSql, conn);
                mCmd.Parameters.Add(new SqliteParameter("@s", mStart));
                mCmd.Parameters.Add(new SqliteParameter("@e", mEnd));
                monthlyStats.Add(new { year = m.Year, month = m.Month, count = Convert.ToInt32(mCmd.ExecuteScalar()) });
            }

            return Ok(new {
                success = true,
                year = targetYear,
                month = targetMonth,
                daily = dailyStats,
                monthTotal,
                monthDays = DateTime.DaysInMonth(targetYear, targetMonth),
                total,
                lastLikeDate,
                monthly = monthlyStats
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetLikeStats failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }


    #endregion
}

#region 请求模型

public class AddVideoRequest
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
    [JsonPropertyName("category")]
    public string Category { get; set; } = "";
    [JsonPropertyName("country")]
    public string Country { get; set; } = "";
    [JsonPropertyName("filePath")]
    public string FilePath { get; set; } = "";
    [JsonPropertyName("fileSize")]
    public long? FileSize { get; set; }
    [JsonPropertyName("coverPath")]
    public string? CoverPath { get; set; }
    [JsonPropertyName("actorIds")]
    public List<string>? ActorIds { get; set; }
    [JsonPropertyName("seriesId")]
    public string? SeriesId { get; set; }
}

public class UpdateVideoRequest
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
    [JsonPropertyName("category")]
    public string Category { get; set; } = "";
    [JsonPropertyName("country")]
    public string Country { get; set; } = "";
    [JsonPropertyName("filePath")]
    public string FilePath { get; set; } = "";
    [JsonPropertyName("coverPath")]
    public string? CoverPath { get; set; }
    [JsonPropertyName("actorIds")]
    public List<string>? ActorIds { get; set; }
    [JsonPropertyName("seriesId")]
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

public class UpdateFileInfoRequest
{
    public string? FilePath { get; set; }
    public string? CoverPath { get; set; }
}

public class UpdateMediaFlagsRequest
{
    [JsonPropertyName("flags")]
    public int Flags { get; set; }
}

#endregion

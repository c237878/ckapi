using ckapi.Services;
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
            pageIndex = Utils.Paging.ClampPage(pageIndex);
            pageSize = Utils.Paging.ClampSize(pageSize);
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
            // 并列行按 id 收尾：否则 like_count/ctime 相同的影片在翻页时来回换位
            orderBy += ", v.id ASC";

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

            using var conn = GetConnection();
            conn.Open();

            // 总数（原先这里另开了一条连接，与列表查询各一次握手）
            var countSql = $"SELECT COUNT(*) FROM videos v {whereClause}";
            int total;
            using (var countCmd = new SqliteCommand(countSql, conn))
            {
                foreach (var p in parameters) countCmd.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
                total = Convert.ToInt32(countCmd.ExecuteScalar());
            }

            // 获取列表
            var sql = $@"
                SELECT {VideoCardQuery.ColumnsWithSeries}
                FROM videos v
                LEFT JOIN video_series s ON v.seriesid = s.id
                {whereClause}
                ORDER BY " + orderBy + @"
                LIMIT @pageSize OFFSET @offset";

            parameters.Add(new SqliteParameter("@pageSize", pageSize));
            parameters.Add(new SqliteParameter("@offset", offset));

            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddRange(parameters.ToArray());

            using var reader = cmd.ExecuteReader();

            var videos = new List<object>();
            while (reader.Read())
            {
                videos.Add(VideoCardQuery.Map(reader));
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
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
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
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    [HttpGet("meta")]
    public IActionResult GetMeta()
    {
        try
        {
            using var conn = GetConnection();
            conn.Open();

            // 地区与分类来自设置里的规范列表，不再从本表 DISTINCT——
            // 否则各页面选项数量不一致，且没记录过的取值选不出来
            var categories = Utils.Options.CommaList(conn, Utils.Options.Categories);
            var countries = Utils.Options.CommaList(conn, Utils.Options.Countries);
            var series = new List<object>();

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
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>
    /// 首页分类板块：一次请求返回全部板块。
    /// 之前首页按分类各发一次 /video/list（每个请求还要开两条连接跑 COUNT），6 个分类就是 6 次往返。
    /// 这里在同一个连接上按分类各取一次，只走索引定位 + LIMIT。
    /// </summary>
    [HttpGet("home-sections")]
    public IActionResult GetHomeSections()
    {
        try
        {
            using var conn = GetConnection();
            conn.Open();

            string homePageCategories = "";
            int count = 12;
            using (var sCmd = new SqliteCommand(
                "SELECT name, content FROM system_settings WHERE name IN ('homePageCategories','homePageCategoryCount')", conn))
            using (var sReader = sCmd.ExecuteReader())
            {
                while (sReader.Read())
                {
                    var name = sReader.GetString(0);
                    var value = sReader.IsDBNull(1) ? "" : sReader.GetString(1);
                    if (name == "homePageCategories") homePageCategories = value;
                    if (name == "homePageCategoryCount" && int.TryParse(value, out var parsed)) count = parsed;
                }
            }

            var categories = homePageCategories
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct()
                .ToList();

            // 未配置时退回"全部分类"
            if (categories.Count == 0)
            {
                using var catCmd = new SqliteCommand("SELECT DISTINCT category FROM videos WHERE category != '' ORDER BY category", conn);
                using var catReader = catCmd.ExecuteReader();
                while (catReader.Read()) categories.Add(catReader.GetString(0));
            }

            count = Utils.Paging.ClampCount(count, 12, 50);

            const string sectionSql = $@"
                SELECT {VideoCardQuery.ColumnsWithSeries}
                FROM videos v
                LEFT JOIN video_series s ON v.seriesid = s.id
                WHERE v.category = @category AND v.file_size > 0
                ORDER BY CASE WHEN v.media_attr_flags = 0 THEN 0 ELSE 1 END, v.ctime DESC, v.id ASC
                LIMIT @limit";

            var sections = new List<object>();
            foreach (var category in categories)
            {
                var videos = new List<object>();
                using var cmd = new SqliteCommand(sectionSql, conn);
                cmd.Parameters.Add(new SqliteParameter("@category", category));
                cmd.Parameters.Add(new SqliteParameter("@limit", count));
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read()) videos.Add(VideoCardQuery.Map(reader));
                }
                sections.Add(new { category, videos });
            }

            return Ok(new { success = true, data = sections, count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetHomeSections failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
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

            var video = VideoCardQuery.Map(reader);

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
                    country = actorReader["country"] == DBNull.Value ? null : actorReader["country"].ToString()
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
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
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

            // 建表统一由 DataService 负责；此处原先还留着一份缺 target_type 的旧 CREATE，
            // 一旦真的按它建表，下面的 INSERT 就会因无该列而失败。

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
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
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
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
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
            using var conn = GetConnection();
            conn.Open();

            // 查询旧记录（获取旧番号和文件路径）
            string? oldCode = null;
            string? oldFilePath = null;
            string? oldCoverPath = null;
            using (var queryCmd = new SqliteCommand("SELECT code, file_path, cover_path FROM videos WHERE id = @id", conn))
            {
                queryCmd.Parameters.Add(new SqliteParameter("@id", id));
                using var qReader = queryCmd.ExecuteReader();
                if (!qReader.Read())
                    return NotFound(new { success = false, message = "视频不存在" });
                oldCode = qReader["code"]?.ToString();
                oldFilePath = qReader["file_path"]?.ToString();
                oldCoverPath = qReader["cover_path"]?.ToString();
            }

            // 番号变化时同步重命名文件
            var newFilePath = req.FilePath;
            var newCoverPath = req.CoverPath;
            var renameInfo = new { videoRenamed = false, coverRenamed = false, oldFile = "", newFile = "", oldCover = "", newCover = "" };
            
            if (!string.IsNullOrEmpty(req.Code) && req.Code != oldCode)
            {
                // 检查旧文件是否被其他影片使用
                bool videoUsedByOthers = false;
                bool coverUsedByOthers = false;
                if (!string.IsNullOrEmpty(oldFilePath))
                {
                    using var checkVideoCmd = new SqliteCommand("SELECT COUNT(*) FROM videos WHERE file_path = @fp AND id != @id", conn);
                    checkVideoCmd.Parameters.Add(new SqliteParameter("@fp", oldFilePath));
                    checkVideoCmd.Parameters.Add(new SqliteParameter("@id", id));
                    videoUsedByOthers = Convert.ToInt32(checkVideoCmd.ExecuteScalar()) > 0;
                }
                if (!string.IsNullOrEmpty(oldCoverPath))
                {
                    using var checkCoverCmd = new SqliteCommand("SELECT COUNT(*) FROM videos WHERE cover_path = @cp AND id != @id", conn);
                    checkCoverCmd.Parameters.Add(new SqliteParameter("@cp", oldCoverPath));
                    checkCoverCmd.Parameters.Add(new SqliteParameter("@id", id));
                    coverUsedByOthers = Convert.ToInt32(checkCoverCmd.ExecuteScalar()) > 0;
                }

                // 只有文件没有被其他影片使用时才重命名
                // 重命名视频文件
                if (!videoUsedByOthers && !string.IsNullOrEmpty(newFilePath) && System.IO.File.Exists(newFilePath))
                {
                    var dir = Path.GetDirectoryName(newFilePath)!;
                    var ext = Path.GetExtension(newFilePath);
                    var currentName = Path.GetFileNameWithoutExtension(newFilePath);
                    if (currentName != req.Code)
                    {
                        var targetPath = Path.Combine(dir, req.Code + ext);
                        if (!System.IO.File.Exists(targetPath) || targetPath == newFilePath)
                        {
                            try
                            {
                                System.IO.File.Move(newFilePath, targetPath);
                                newFilePath = targetPath;
                                renameInfo = new { videoRenamed = true, coverRenamed = renameInfo.coverRenamed, oldFile = oldFilePath ?? "", newFile = targetPath, oldCover = renameInfo.oldCover, newCover = renameInfo.newCover };
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "编辑时重命名视频文件失败: {FilePath}", newFilePath);
                            }
                        }
                    }
                }

                // 重命名封面文件
                if (!coverUsedByOthers && !string.IsNullOrEmpty(newCoverPath) && System.IO.File.Exists(newCoverPath))
                {
                    var dir = Path.GetDirectoryName(newCoverPath)!;
                    var ext = Path.GetExtension(newCoverPath);
                    var currentName = Path.GetFileNameWithoutExtension(newCoverPath);
                    if (currentName != req.Code)
                    {
                        var targetPath = Path.Combine(dir, req.Code + ext);
                        if (!System.IO.File.Exists(targetPath) || targetPath == newCoverPath)
                        {
                            try
                            {
                                System.IO.File.Move(newCoverPath, targetPath);
                                newCoverPath = targetPath;
                                renameInfo = new { videoRenamed = renameInfo.videoRenamed, coverRenamed = true, oldFile = renameInfo.oldFile, newFile = renameInfo.newFile, oldCover = oldCoverPath ?? "", newCover = targetPath };
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "编辑时重命名封面文件失败: {CoverPath}", newCoverPath);
                            }
                        }
                    }
                }
            }

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

            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.Add(new SqliteParameter("@id", id));
            cmd.Parameters.Add(new SqliteParameter("@code", (object?)req.Code ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@name", req.Name));
            cmd.Parameters.Add(new SqliteParameter("@category", req.Category));
            cmd.Parameters.Add(new SqliteParameter("@country", req.Country ?? ""));
            cmd.Parameters.Add(new SqliteParameter("@filePath", newFilePath));
            cmd.Parameters.Add(new SqliteParameter("@coverPath", string.IsNullOrEmpty(newCoverPath) ? (object)DBNull.Value : newCoverPath));
            cmd.Parameters.Add(new SqliteParameter("@seriesId", (object?)req.SeriesId ?? DBNull.Value));
            cmd.ExecuteNonQuery();

            // 更新演员关联
            if (req.ActorIds != null)
            {
                using var delCmd = new SqliteCommand("DELETE FROM video_actors WHERE video_id = @videoId", conn);
                delCmd.Parameters.Add(new SqliteParameter("@videoId", id));
                delCmd.ExecuteNonQuery();

                foreach (var actorId in req.ActorIds)
                {
                    var relSql = "INSERT OR IGNORE INTO video_actors (video_id, actor_id) VALUES (@videoId, @actorId)";
                    using var relCmd = new SqliteCommand(relSql, conn);
                    relCmd.Parameters.AddWithValue("@videoId", id);
                    relCmd.Parameters.AddWithValue("@actorId", actorId);
                    relCmd.ExecuteNonQuery();
                }
            }

            return Ok(new { success = true, message = "更新成功", renameInfo });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UpdateVideo failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>
    /// 删除视频
    /// </summary>
    [HttpDelete("{id}")]
    public IActionResult DeleteVideo(string id, [FromQuery] bool deleteFiles = false)
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
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>
    /// 批量删除视频
    /// </summary>
    [HttpDelete("batch")]
    public IActionResult BatchDeleteVideos([FromBody] BatchDeleteRequest req, [FromQuery] bool deleteFiles = false)
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
                // 参数化 IN 子句，避免字符串拼接造成 SQL 注入
                var idParams = req.Ids
                    .Select((id, i) => new SqliteParameter($"@delId{i}", id))
                    .ToArray();
                var idPlaceholders = string.Join(",", idParams.Select(p => p.ParameterName));
                using var queryCmd = new SqliteCommand($"SELECT id, file_path, cover_path FROM videos WHERE id IN ({idPlaceholders})", conn);
                queryCmd.Parameters.AddRange(idParams);
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
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    #region 私有方法

    // 今日推荐内存缓存：按天缓存，refresh=true 清除。
    // 只缓存影片 id —— 番号/片名/文件大小/演员都会变，缓存整行会让首页一整天显示旧数据。
    private static string? _dailyRecommendDate = null;
    private static List<string>? _dailyRecommendIds = null;

    /// 缓存是"为某个 count 生成的一份随机结果"，所以设置里的数量一改就该失效重建，
    /// 不能拿旧列表截断凑数（那会让新数量整天不生效）。
    private static int _dailyRecommendForCount = 0;
    private static readonly object _dailyRecommendLock = new();

    /// <summary>
    /// 首页 - 今日推荐：优先还没看过的（media_attr_flags = 0），
    /// 只有未看过的不够数时才掺入看过的。id 列表按天缓存，卡片字段每次现查。
    /// </summary>
    [HttpGet("daily-recommend")]
    public IActionResult GetDailyRecommend([FromQuery] int count = 12, [FromQuery] bool refresh = false)
    {
        try
        {
            count = Utils.Paging.ClampCount(count);
            var today = DateTime.Now.ToString("yyyy-MM-dd");

            List<string>? ids = null;
            var fromCache = false;
            if (!refresh)
            {
                lock (_dailyRecommendLock)
                {
                    if (_dailyRecommendIds != null && _dailyRecommendDate == today
                        && _dailyRecommendForCount == count)
                    {
                        ids = _dailyRecommendIds.ToList();
                        fromCache = true;
                    }
                }
            }

            using var conn = GetConnection();
            conn.Open();

            if (ids == null)
            {
                ids = PickDailyRecommendIds(conn, count);
                lock (_dailyRecommendLock)
                {
                    _dailyRecommendDate = today;
                    _dailyRecommendIds = ids.ToList();
                    _dailyRecommendForCount = count;
                }
            }

            // 只缓存 id，字段现查：中途被改过番号/片名/大小或已删除的影片不会带着旧数据出现
            var data = LoadCardsByIds(conn, ids);
            return Ok(new { success = true, data, cached = fromCache });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetDailyRecommend failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>
    /// 挑今日推荐的 id：未看过（media_attr_flags 为 0 或未设置）的先占满名额，
    /// 不够 count 才从看过的里补。
    ///
    /// 旧实现是先取"全库 60%"做候选池再在池子里均匀随机，
    /// ORDER BY 的优先级只决定谁进池、进池后就不起作用了，
    /// 所以只要未看过的数量小于池子大小，看过的会被按比例抽回来——不是"优先未看过"。
    /// </summary>
    private static List<string> PickDailyRecommendIds(SqliteConnection conn, int count)
    {
        var picked = new List<string>(count);

        // 两个查询的 WHERE 互斥，补位时不必再排除已选 id
        using (var cmd = new SqliteCommand(@"
            SELECT v.id FROM videos v
            WHERE v.file_size > 0 AND (v.media_attr_flags IS NULL OR v.media_attr_flags = 0)
            ORDER BY RANDOM()
            LIMIT @count", conn))
        {
            cmd.Parameters.AddWithValue("@count", count);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) picked.Add(reader.GetString(0));
        }

        var missing = count - picked.Count;
        if (missing > 0)
        {
            using (var cmd = new SqliteCommand(@"
                SELECT v.id FROM videos v
                WHERE v.file_size > 0 AND v.media_attr_flags IS NOT NULL AND v.media_attr_flags <> 0
                ORDER BY RANDOM()
                LIMIT @missing", conn))
            {
                cmd.Parameters.AddWithValue("@missing", missing);
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) picked.Add(reader.GetString(0));
            }
        }

        return picked;
    }

    /// <summary>
    /// 按 id 现查卡片字段，并保持 id 原本的顺序。
    /// 缓存 id 之后信息变更、或影片在缓存期间被删除，都在这里得到纠正。
    /// </summary>
    private static List<Dictionary<string, object?>> LoadCardsByIds(SqliteConnection conn, List<string> ids)
    {
        var result = new List<Dictionary<string, object?>>(ids.Count);
        if (ids.Count == 0) return result;

        var names = ids.Select((_, i) => "@i" + i).ToArray();
        var sql = $@"
            SELECT {VideoCardQuery.ColumnsWithSeries}
            FROM videos v
            LEFT JOIN video_series s ON v.seriesid = s.id
            WHERE v.id IN ({string.Join(", ", names)})";

        using var cmd = new SqliteCommand(sql, conn);
        for (var i = 0; i < ids.Count; i++) cmd.Parameters.AddWithValue(names[i], ids[i]);

        var byId = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var card = VideoCardQuery.Map(reader);
                byId[card["id"]?.ToString() ?? ""] = card;
            }
        }

        foreach (var id in ids)
        {
            if (byId.TryGetValue(id, out var card)) result.Add(card);
        }

        return result;
    }

    /// <summary>
    /// 首页 - 最近点赞（同一视频多次点赞只取最新那次）
    /// </summary>
    [HttpGet("recently-liked")]
    public IActionResult GetRecentlyLiked([FromQuery] int count = 12)
    {
        try
        {
            count = Utils.Paging.ClampCount(count);
            using var conn = GetConnection();
            conn.Open();
            var sql = $@"
                SELECT {VideoCardQuery.ColumnsWithSeries}
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
                while (reader.Read()) list.Add(VideoCardQuery.Map(reader));
            }
            return Ok(new { success = true, data = list });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetRecentlyLiked failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
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
            count = Utils.Paging.ClampCount(count);
            using var conn = GetConnection();
            conn.Open();
            var sql = $@"
                SELECT {VideoCardQuery.ColumnsWithSeries}
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
                while (reader.Read()) list.Add(VideoCardQuery.Map(reader));
            }
            return Ok(new { success = true, data = list });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetTopLiked failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
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

            // 用日期区间比较而不是 DATE(liked_at)：对列套函数会让 idx_video_likes_time 失效
            var monthStart = new DateTime(targetYear, targetMonth, 1);
            var startDate = monthStart.ToString("yyyy-MM-dd");
            var nextMonthStart = monthStart.AddMonths(1).ToString("yyyy-MM-dd");

            using var conn = GetConnection();
            conn.Open();

            // 获取当月每日点赞数
            var dailySql = @"
                SELECT substr(liked_at, 1, 10) as like_date, COUNT(*) as cnt
                FROM video_likes
                WHERE liked_at >= @startDate AND liked_at < @nextMonthStart
                GROUP BY like_date
                ORDER BY like_date";
            using var dailyCmd = new SqliteCommand(dailySql, conn);
            dailyCmd.Parameters.Add(new SqliteParameter("@startDate", startDate));
            dailyCmd.Parameters.Add(new SqliteParameter("@nextMonthStart", nextMonthStart));

            var dailyStats = new Dictionary<string, int>();
            using (var reader = dailyCmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    dailyStats[reader.GetString(0)] = reader.GetInt32(1);
                }
            }

            // 当月总数直接由日聚合求和，省掉一次查询
            int monthTotal = dailyStats.Values.Sum();

            var statMonth = new DateTime(now.Year, now.Month, 1);

            // 历史总数与最后点赞日期合并成一次查询
            int total;
            string? lastLikeDate;
            using (var sumCmd = new SqliteCommand(
                "SELECT COUNT(*), substr(MAX(liked_at), 1, 10) FROM video_likes", conn))
            using (var sumReader = sumCmd.ExecuteReader())
            {
                sumReader.Read();
                total = Convert.ToInt32(sumReader[0]);
                lastLikeDate = sumReader[1] == DBNull.Value ? null : sumReader[1].ToString();
            }

            // 最近 12 个月：一次 GROUP BY 取回，之前是 12 次串行 COUNT
            var monthlyCounts = new Dictionary<string, int>();
            using (var mCmd = new SqliteCommand(@"
                SELECT substr(liked_at, 1, 7) AS ym, COUNT(*) AS cnt
                FROM video_likes
                WHERE liked_at >= @s AND liked_at < @e
                GROUP BY ym", conn))
            {
                mCmd.Parameters.Add(new SqliteParameter("@s", statMonth.AddMonths(-11).ToString("yyyy-MM-dd")));
                mCmd.Parameters.Add(new SqliteParameter("@e", statMonth.AddMonths(1).ToString("yyyy-MM-dd")));
                using var mReader = mCmd.ExecuteReader();
                while (mReader.Read()) monthlyCounts[mReader.GetString(0)] = mReader.GetInt32(1);
            }

            var monthlyStats = new List<object>();
            for (var i = 11; i >= 0; i--)
            {
                var m = statMonth.AddMonths(-i);
                monthlyCounts.TryGetValue(m.ToString("yyyy-MM"), out var cnt);
                monthlyStats.Add(new { year = m.Year, month = m.Month, count = cnt });
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
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
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

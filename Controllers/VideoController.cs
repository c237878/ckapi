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

    /// <summary>顺带补了几部同系列的，数字要出现在提示里——批量写入不该悄悄发生</summary>
    private static string FilledNote(int filled, string baseline) =>
        filled > 0 ? $"{baseline}（同系列另外 {filled} 部日本 av 也补上了片商）" : baseline;

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
        [FromQuery] string? subtitle = null,
        [FromQuery] string? watermark = null,
        [FromQuery] string? resolution = null,
        [FromQuery] string? studio = null,
        [FromQuery] string? typeId = null,
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

            // 排序：首页分类板块优先"没给过片源结论"的（两维都还是 unknown），给过的同等优先级。
            // 这个口径可靠的前提是扫描不碰这两个状态（只写分辨率），否则一扫完全站都成"有结论"。
            var orderBy = sortBy?.ToLower() switch
            {
                "code" => "v.code ASC",
                "name" => "v.name ASC",
                "likecount" => "like_count DESC",
                _ => prioritizeUnrated == true
                    ? $"CASE WHEN {Utils.SourceStates.Unrated} THEN 0 ELSE 1 END, v.ctime DESC"
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

            // 片源两维 / 分辨率档 / 有没有文件：与系列页、演员页共用一套口径
            VideoCardQuery.AppendCommonFilters(ref whereClause, parameters, new VideoCardQuery.SourceFilter
            {
                Subtitle = subtitle, Watermark = watermark, Resolution = resolution,
                StudioId = studio, HasFile = hasFile
            });

            // 按版本类型筛（设置里「看这一类型下有哪些影片」走这里）：df 从"默认那一版"
            // 换成"该类型那一版"。一部片同一个类型只能有一条（v12），所以仍是一对一，
            // 卡片上的大小/分辨率/两维状态也跟着变成这一版的——筛的就是它，数字该说它的
            var fileJoin = VideoCardQuery.FileJoin;
            if (!string.IsNullOrEmpty(typeId))
            {
                fileJoin = "LEFT JOIN video_files df ON df.video_id = v.id AND df.type_id = @typeId";
                whereClause += " AND df.type_id = @typeId";
                parameters.Add(new SqliteParameter("@typeId", typeId));
            }

            using var conn = GetConnection();
            conn.Open();

            // 总数（原先这里另开了一条连接，与列表查询各一次握手）
            // 挂默认版本行：文件层筛选（字幕/水印/分辨率/有无文件）看的是它，不挂就没有 df 这个别名
            var countSql = $"SELECT COUNT(*) FROM videos v {fileJoin} {whereClause}";
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
                {fileJoin}
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

            // 版本类型词表：详情页的版本下拉与「加一版」对话框共用这一份，
            // 与地区/分类同一个入口拿，免得前端再发一次请求
            var versionTypes = VideoFiles.Types(conn);

            return Ok(new { success = true, categories, countries, series, versionTypes, homePageCategories, homePageCategoryCount });
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
                {VideoCardQuery.FileJoin}
                LEFT JOIN video_series s ON v.seriesid = s.id
                WHERE v.category = @category AND df.file_size > 0
                ORDER BY CASE WHEN {Utils.SourceStates.Unrated} THEN 0 ELSE 1 END, v.ctime DESC, v.id ASC
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
            using var conn = GetConnection();
            conn.Open();

            Dictionary<string, object?> video;
            // 文件层那几列在默认版本行上（v11），卡片映射统一按 df.* 取，这里显式列出别名，
            // 不用 df.*：它的 id / ctime 会与影片层的同名列撞车
            var sql = @"
                SELECT v.*, df.id AS file_id, df.file_path, df.file_size, df.res_w, df.res_h,
                       df.subtitle_state, df.watermark_state, df.scan_time, df.code AS file_code,
                       df.label AS file_label, df.type_id AS file_type_id,
                       (SELECT vt.name FROM version_types vt WHERE vt.id = df.type_id) AS file_type_name,
                       s.name as series_name, st.name as studio_name, st.link as studio_link
                FROM videos v
                LEFT JOIN video_files df ON df.video_id = v.id AND df.is_default = 1
                LEFT JOIN video_series s ON v.seriesid = s.id
                LEFT JOIN studios st ON st.id = v.studioid
                WHERE v.id = @id";
            using (var cmd = new SqliteCommand(sql, conn))
            {
                cmd.Parameters.Add(new SqliteParameter("@id", id));
                using var reader = cmd.ExecuteReader();
                if (!reader.Read())
                    return NotFound(new { success = false, message = "视频不存在" });

                video = VideoCardQuery.Map(reader);
            }

            // 版本清单：详情页的下拉框、按版本显示实际分辨率、切换默认版本都要它
            var versions = VideoFiles.OfMovie(conn, id);
            video["versions"] = versions;
            video["versionCount"] = versions.Count;
            // 影片层不再有"这一版叫什么"的概念，但详情页标题旁要显示当前版本，所以把默认版的名字带出去
            var current = versions.FirstOrDefault(v => v["isDefault"] is true);
            video["defaultLabel"] = current?["displayName"];

            // 外链直接并进 video 对象：前端只有一份影片状态，不用分几个 ref 去同步。
            // 上面那个 reader 必须先关掉再发这些查询——Microsoft.Data.Sqlite 不支持多个活动结果集。
            // 片商不在这里：它现在是 videos.studioid，上面那条 SELECT 已经把 id 与名字一起带出来了。
            video["links"] = VideoMeta.Links(conn, id);

            // 获取演员列表
            // birthdate 是给详情页算「发行时年龄」用的，没有它前端只能显示名字
            var actorSql = @"
                SELECT a.id, a.name, a.country, a.birthdate FROM actors a
                INNER JOIN video_actors va ON a.id = va.actor_id
                WHERE va.video_id = @videoId
                ORDER BY a.name";
            
            using var actorCmd = new SqliteCommand(actorSql, conn);
            actorCmd.Parameters.Add(new SqliteParameter("@videoId", id));
            
            using var actorReader = actorCmd.ExecuteReader();
            var actors = new List<object>();
            while (actorReader.Read())
            {
                actors.Add(new
                {
                    id = actorReader.GetString(0),
                    name = actorReader.GetString(1),
                    country = actorReader.IsDBNull(2) ? null : actorReader.GetString(2),
                    birthdate = actorReader.IsDBNull(3) ? null : actorReader.GetString(3)
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
    /// 点赞：记的是"这一部片的哪一版"（v11 定，点赞针对视频层）。
    /// 详情页带当前选中的版本；列表卡片没这个概念，不传就落在默认版本那一行上。
    /// file_id 与 video_id 都存：前者用于榜单按版本分条，后者用于"这部片一共被赞几次"。
    /// </summary>
    [HttpPost("{id}/like")]
    public IActionResult LikeVideo(string id, [FromQuery] string? fileId = null)
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

            var targetFileId = string.IsNullOrWhiteSpace(fileId) ? VideoFiles.DefaultId(conn, id) : fileId;
            if (!string.IsNullOrWhiteSpace(targetFileId))
            {
                using var ownerCmd = new SqliteCommand("SELECT video_id FROM video_files WHERE id = @f", conn);
                ownerCmd.Parameters.Add(new SqliteParameter("@f", targetFileId));
                if (ownerCmd.ExecuteScalar()?.ToString() != id)
                    return Ok(new { success = false, message = "这一版不属于这部片" });
            }

            // 插入点赞记录
            var likedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            using (var insertCmd = new SqliteCommand(
                       "INSERT INTO video_likes (video_id, liked_at, target_type, file_id) VALUES (@videoId, @likedAt, 'video', @fileId)", conn))
            {
                insertCmd.Parameters.Add(new SqliteParameter("@videoId", id));
                insertCmd.Parameters.Add(new SqliteParameter("@likedAt", likedAt));
                insertCmd.Parameters.Add(new SqliteParameter("@fileId", (object?)targetFileId ?? DBNull.Value));
                insertCmd.ExecuteNonQuery();
            }

            // 统计点赞数：影片层给总和（卡片与详情页显示的就是它），再带上这一版自己的数
            int likeCount = 0;
            using (var countCmd = new SqliteCommand("SELECT COUNT(*) FROM video_likes WHERE video_id = @videoId AND target_type='video'", conn))
            {
                countCmd.Parameters.Add(new SqliteParameter("@videoId", id));
                likeCount = Convert.ToInt32(countCmd.ExecuteScalar());
            }

            int fileLikeCount = 0;
            if (!string.IsNullOrWhiteSpace(targetFileId))
            {
                using var fileCountCmd = new SqliteCommand(
                    "SELECT COUNT(*) FROM video_likes WHERE file_id = @f AND target_type='video'", conn);
                fileCountCmd.Parameters.Add(new SqliteParameter("@f", targetFileId));
                fileLikeCount = Convert.ToInt32(fileCountCmd.ExecuteScalar());
            }

            return Ok(new { success = true, likeCount, fileId = targetFileId, versionLikeCount = fileLikeCount });
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
            // 留空是合法输入（大部分片子根本不知道发行日），只有填了又不成格式才拦下来；
            // 前端空值传的是 ""，所以判空不能判 is null
            var rawRelease = (req.ReleaseDate ?? "").Trim();
            string? release = null;
            if (rawRelease.Length > 0)
            {
                release = VideoMeta.NormalizeReleaseDate(rawRelease);
                if (release is null)
                    return Ok(new { success = false, message = "发行日期只收 2024 / 2024-03 / 2024-03-15 三种写法，留空表示不知道" });
            }

            // 影片层不再存文件（v11）：filePath / fileSize 落到下面那条原版行上
            var sql = @"
                INSERT INTO videos (id, code, name, category, country, cover_path, ctime, seriesid,
                                    original_name, release_date, studioid)
                VALUES (@id, @code, @name, @category, @country, @coverPath, @addedAt, @seriesId,
                        @originalName, @releaseDate, @studioId)";
            
            using var conn = GetConnection();
            conn.Open();

            // 片商是一部片的一个值，与 seriesid 同形；只认词表里已有的 id，认不出就报错别静默丢
            var (studioId, _) = VideoMeta.ResolveStudio(conn, req.StudioId, null);

            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.Add(new SqliteParameter("@id", id));
            cmd.Parameters.Add(new SqliteParameter("@code", (object?)req.Code ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@name", req.Name));
            cmd.Parameters.Add(new SqliteParameter("@category", req.Category));
            cmd.Parameters.Add(new SqliteParameter("@country", req.Country ?? ""));
            cmd.Parameters.Add(new SqliteParameter("@coverPath", req.CoverPath ?? (object)DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@addedAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
            cmd.Parameters.Add(new SqliteParameter("@seriesId", (object?)req.SeriesId ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@originalName",
                string.IsNullOrWhiteSpace(req.OriginalName) ? (object)DBNull.Value : req.OriginalName.Trim()));
            cmd.Parameters.Add(new SqliteParameter("@releaseDate", (object?)release ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@studioId", (object?)studioId ?? DBNull.Value));
            
            cmd.ExecuteNonQuery();

            // 每部片恰好一条默认版本行是全站筛选与统计的前提，建片时就一起建出来（原版，番号即行级标识）
            VideoFiles.AddOriginal(conn, id, req.Code?.Trim(), req.FilePath, req.FileSize ?? 0);

            // 同系列还没填片商的一起补上：一个系列基本就是同一家在做。
            // 勾不勾是这一趟的事，界面上那颗复选框决定；没传（老脚本、别的调用方）按补算
            var filled = (req.SyncSeriesStudio ?? true) ? VideoMeta.FillSeriesStudios(conn, id, studioId) : 0;

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

            return Ok(new { success = true, data = new { id = id }, message = FilledNote(filled, "添加成功") });
        }
        catch (VideoMeta.MetaException ex)
        {
            // 片商 id 认不出这类是调用方可以自救的错，报原文别丢进 500
            return Ok(new { success = false, message = ex.Message });
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

            // 查询旧记录：番号与封面在影片层；文件路径在默认版本行上（v11），要用得另取
            string? oldCode = null;
            string? oldCoverPath = null;
            using (var queryCmd = new SqliteCommand("SELECT code, cover_path FROM videos WHERE id = @id", conn))
            {
                queryCmd.Parameters.Add(new SqliteParameter("@id", id));
                using var qReader = queryCmd.ExecuteReader();
                if (!qReader.Read())
                    return NotFound(new { success = false, message = "视频不存在" });
                oldCode = qReader["code"]?.ToString();
                oldCoverPath = qReader["cover_path"]?.ToString();
            }

            var newCoverPath = req.CoverPath;
            var renameInfo = new { coverRenamed = false, oldCover = "", newCover = "",
                versionRenamed = 0, versionSkipped = 0, versionDetails = new List<Dictionary<string, object?>>() };

            if (!string.IsNullOrEmpty(req.Code) && req.Code != oldCode)
            {
                // 封面是一部片的一份，跟着新番号改名。先确认这张封面没被别的影片共用，
                // 不然改一部片会把另一部片的封面文件搬走（沿用原来的那道闸门）
                bool coverUsedByOthers = false;
                if (!string.IsNullOrEmpty(oldCoverPath))
                {
                    using var checkCoverCmd = new SqliteCommand("SELECT COUNT(*) FROM videos WHERE cover_path = @cp AND id != @id", conn);
                    checkCoverCmd.Parameters.Add(new SqliteParameter("@cp", oldCoverPath));
                    checkCoverCmd.Parameters.Add(new SqliteParameter("@id", id));
                    coverUsedByOthers = Convert.ToInt32(checkCoverCmd.ExecuteScalar()) > 0;
                }

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
                                renameInfo = new { renameInfo.coverRenamed, oldCover = oldCoverPath ?? "", newCover = targetPath,
                                    renameInfo.versionRenamed, renameInfo.versionSkipped, renameInfo.versionDetails };
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "编辑时重命名封面文件失败: {CoverPath}", newCoverPath);
                            }
                        }
                    }
                }
            }

            // 文件路径是"默认那一版"的属性：传了才动，没传保持原样（与原名/发行日期同一口径，
            // 编辑框以外还有别的调用方整份 PUT 这条记录，缺字段不该把已登记的文件抹掉）
            var defaultFileId = VideoFiles.DefaultId(conn, id);
            if (req.FilePath is not null && defaultFileId is not null)
            {
                var path = req.FilePath.Trim();
                long size = 0;
                if (path.Length > 0 && System.IO.File.Exists(path)) size = new FileInfo(path).Length;
                using var fileCmd = new SqliteCommand(
                    "UPDATE video_files SET file_path = @p, file_size = @s WHERE id = @id", conn);
                fileCmd.Parameters.Add(new SqliteParameter("@p", path));
                fileCmd.Parameters.Add(new SqliteParameter("@s", size));
                fileCmd.Parameters.Add(new SqliteParameter("@id", defaultFileId));
                fileCmd.ExecuteNonQuery();
            }

            var sql = @"
                UPDATE videos SET
                    code = @code,
                    name = @name,
                    category = @category,
                    country = @country,
                    cover_path = @coverPath,
                    seriesid = @seriesId
                WHERE id = @id";

            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.Add(new SqliteParameter("@id", id));
            cmd.Parameters.Add(new SqliteParameter("@code", (object?)req.Code ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@name", req.Name));
            cmd.Parameters.Add(new SqliteParameter("@category", req.Category));
            cmd.Parameters.Add(new SqliteParameter("@country", req.Country ?? ""));
            cmd.Parameters.Add(new SqliteParameter("@coverPath", string.IsNullOrEmpty(newCoverPath) ? (object)DBNull.Value : newCoverPath));
            cmd.Parameters.Add(new SqliteParameter("@seriesId", (object?)req.SeriesId ?? DBNull.Value));
            cmd.ExecuteNonQuery();

            // 番号变了，这一部片的所有版本行跟着改盘上的文件名（只换前缀、只跟随合规命名，
            // 逐行改成功才更新该行路径）——见 VideoFiles.CascadeRename 的注释
            var newCode = string.IsNullOrWhiteSpace(req.Code) ? oldCode ?? "" : req.Code.Trim();
            var cascade = VideoFiles.CascadeRename(conn, id, oldCode ?? "", newCode);
            if (cascade.Renamed > 0 || cascade.Skipped > 0)
            {
                _logger.LogInformation("影片 {Code} 改番号：{Renamed} 个版本文件跟随改名，{Skipped} 个未跟随",
                    newCode, cascade.Renamed, cascade.Skipped);
            }
            renameInfo = new { renameInfo.coverRenamed, renameInfo.oldCover, renameInfo.newCover,
                versionRenamed = cascade.Renamed, versionSkipped = cascade.Skipped, versionDetails = cascade.Details };

            // 原名与发行日期：传了才动，没传保持原样——编辑框以外还有别的调用方整份 PUT 这条记录。
            // 发行日期先过格式校验，不合法就明确报出来，不静默丢掉（静默丢会让人以为已经存上了）。
            if (req.OriginalName is not null)
            {
                using var cmd2 = new SqliteCommand("UPDATE videos SET original_name = @v WHERE id = @id", conn);
                cmd2.Parameters.Add(new SqliteParameter("@v", req.OriginalName.Trim().Length == 0 ? (object)DBNull.Value : req.OriginalName.Trim()));
                cmd2.Parameters.Add(new SqliteParameter("@id", id));
                cmd2.ExecuteNonQuery();
            }
            if (req.ReleaseDate is not null)
            {
                var raw = req.ReleaseDate.Trim();
                if (raw.Length > 0 && VideoMeta.NormalizeReleaseDate(raw) is null)
                    return Ok(new { success = false, message = "发行日期只收 2024 / 2024-03 / 2024-03-15 三种写法，留空表示不知道" });

                using var cmd3 = new SqliteCommand("UPDATE videos SET release_date = @v WHERE id = @id", conn);
                cmd3.Parameters.Add(new SqliteParameter("@v", raw.Length == 0 ? (object)DBNull.Value : raw));
                cmd3.Parameters.Add(new SqliteParameter("@id", id));
                cmd3.ExecuteNonQuery();
            }

            // 片商是一部片的一个值：null 表示这次不动，空串表示清空。
            // 填上之后同系列还没填的顺手补上（已有片商的不动）。
            var filled = 0;
            if (req.StudioId is not null)
            {
                var (studioId, _) = VideoMeta.ResolveStudio(conn, req.StudioId, null);
                VideoMeta.SetStudio(conn, id, studioId);
                filled = (req.SyncSeriesStudio ?? true) ? VideoMeta.FillSeriesStudios(conn, id, studioId) : 0;
            }

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

            return Ok(new { success = true, message = FilledNote(filled, "更新成功"), renameInfo });
        }
        catch (VideoMeta.MetaException ex)
        {
            return Ok(new { success = false, message = ex.Message });
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
            // 先查询记录，获取文件路径：封面在影片层只有一份，视频文件按版本一行（v11），
            // 删片时这一部片的所有版本文件都要跟着走，否则盘上留下没人认领的孤儿文件
            string? coverPath = null;
            var filePaths = new List<string>();
            using (var conn = GetConnection())
            {
                conn.Open();
                using (var queryCmd = new SqliteCommand("SELECT cover_path FROM videos WHERE id = @id", conn))
                {
                    queryCmd.Parameters.Add(new SqliteParameter("@id", id));
                    using var reader = queryCmd.ExecuteReader();
                    if (reader.Read()) coverPath = reader["cover_path"]?.ToString();
                }

                using var filesCmd = new SqliteCommand(
                    "SELECT file_path FROM video_files WHERE video_id = @id AND IFNULL(file_path, '') <> ''", conn);
                filesCmd.Parameters.Add(new SqliteParameter("@id", id));
                using var fileReader = filesCmd.ExecuteReader();
                while (fileReader.Read()) filePaths.Add(fileReader.GetString(0));
            }

            using var conn2 = GetConnection();
            conn2.Open();

            // 删除演员关联
            using var delRelCmd = new SqliteCommand("DELETE FROM video_actors WHERE video_id = @videoId", conn2);
            delRelCmd.Parameters.Add(new SqliteParameter("@videoId", id));
            delRelCmd.ExecuteNonQuery();

            // 外链、版本行、点赞记录都归 PurgeVideo 清：外键没开，漏一张就留下点不动的幽灵关系
            VideoMeta.PurgeVideo(conn2, id);

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
                foreach (var filePath in filePaths)
                {
                    if (!System.IO.File.Exists(filePath)) continue;
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
                message = deleteFiles
                    ? deletedFiles.Count == 0 ? "记录已删除（没有文件可删）" : $"已删除 {deletedFiles.Count} 个文件"
                    : "记录已删除，文件已保留",
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

            // 先查询所有要删除的记录的文件路径：封面一部一张，视频文件按版本一行（v11）
            var filesToDelete = new List<string>();
            using (var conn = GetConnection())
            {
                conn.Open();
                // 参数化 IN 子句，避免字符串拼接造成 SQL 注入
                var idParams = req.Ids
                    .Select((id, i) => new SqliteParameter($"@delId{i}", id))
                    .ToArray();
                var idPlaceholders = string.Join(",", idParams.Select(p => p.ParameterName));

                using (var queryCmd = new SqliteCommand(
                           $"SELECT cover_path FROM videos WHERE id IN ({idPlaceholders})", conn))
                {
                    queryCmd.Parameters.AddRange(idParams);
                    using var reader = queryCmd.ExecuteReader();
                    while (reader.Read() && reader[0] != DBNull.Value)
                    {
                        var p = reader.GetString(0);
                        if (p.Length > 0) filesToDelete.Add(p);
                    }
                }

                using (var fileCmd = new SqliteCommand(
                           $"SELECT file_path FROM video_files WHERE video_id IN ({idPlaceholders}) AND IFNULL(file_path, '') <> ''", conn))
                {
                    fileCmd.Parameters.AddRange(idParams);
                    using var reader = fileCmd.ExecuteReader();
                    while (reader.Read()) filesToDelete.Add(reader.GetString(0));
                }
            }

            filesToDelete = filesToDelete.Distinct(StringComparer.Ordinal).ToList();

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

                    // 外链、版本行、点赞记录都归 PurgeVideo 清，漏一张就是点不动的幽灵关系
                    VideoMeta.PurgeVideo(conn2, id, transaction);

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

            // 根据参数决定是否删除文件（封面与各版本文件已在前面按影片一次取全）
            var deletedFiles = new List<string>();
            if (deleteFiles)
            {
                foreach (var path in filesToDelete)
                {
                    if (!System.IO.File.Exists(path)) continue;
                    try
                    {
                        System.IO.File.Delete(path);
                        deletedFiles.Add(path);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "删除文件失败: {Path}", path);
                    }
                }
            }

            return Ok(new
            {
                success = true,
                message = deleteFiles
                    ? $"已删除 {deleted} 条记录、{deletedFiles.Count} 个文件"
                    : $"已删除 {deleted} 条记录，文件已保留",
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
    /// 首页 - 今日推荐：优先还没看过片的（两维都还没给结论），
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
    /// 挑今日推荐的 id：没给过片源结论的（两维都还是 unknown）先占满名额，
    /// 不够 count 才从有结论的里补。
    ///
    /// 旧实现是先取"全库 60%"做候选池再在池子里均匀随机，
    /// ORDER BY 的优先级只决定谁进池、进池后就不起作用了，
    /// 所以只要未看过的数量小于池子大小，看过的会被按比例抽回来——不是"优先未看过"。
    /// </summary>
    private static List<string> PickDailyRecommendIds(SqliteConnection conn, int count)
    {
        var picked = new List<string>(count);
        var unrated = Utils.SourceStates.Unrated;

        // 两个查询的 WHERE 互斥，补位时不必再排除已选 id
        using (var cmd = new SqliteCommand($@"
            SELECT v.id FROM videos v
            {VideoCardQuery.FileJoin}
            WHERE df.file_size > 0 AND {unrated}
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
            using (var cmd = new SqliteCommand($@"
                SELECT v.id FROM videos v
                {VideoCardQuery.FileJoin}
                WHERE df.file_size > 0 AND NOT {unrated}
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
            {VideoCardQuery.FileJoin}
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
    /// 首页 - 最近点赞：**一条点赞记录一张卡，不按影片去重**（2026-09-29 定）。
    ///
    /// 去重会让"中间穿插了别的影片"时的顺序失真——你实际赞的顺序就是榜单的顺序。
    /// 每张卡带出这一次赞的是哪一版（likedFileId / likedVersion），前端在卡片上打标；
    /// 赞的正好是默认版本时不显示标识，免得满屏都是"原版"。
    /// 卡片上的文件字段仍走默认版本（口径只有一处：VideoCardQuery.Columns）。
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
                SELECT {VideoCardQuery.ColumnsWithSeries},
                       vl.liked_at AS like_time, lf.id AS liked_file_id, {VersionNameSql} AS liked_version
                FROM video_likes vl
                JOIN videos v ON vl.video_id = v.id
                {VideoCardQuery.FileJoin}
                LEFT JOIN video_files lf ON lf.id = vl.file_id
                LEFT JOIN video_series s ON v.seriesid = s.id
                WHERE vl.target_type = 'video' AND df.file_size > 0
                ORDER BY vl.liked_at DESC, vl.id DESC
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

    /// <summary>版本名的 SQL 口径：**类型名 → 版本名称 → 原版**，与 VideoFiles.DisplayName 同一套优先级
    /// （类型名是"湿姐"这种短名，label 往往是整条解说片的长标题，不适合当标识）。
    /// 别名固定 lf，给最近点赞与高赞榜两条查询共用。</summary>
    private const string VersionNameSql =
        "COALESCE((SELECT vt.name FROM version_types vt WHERE vt.id = lf.type_id), NULLIF(TRIM(lf.label), ''), '原版')";

    /// <summary>
    /// 首页 - 高赞影片：**按版本各算一条，不去重**（2026-09-29 定）。
    ///
    /// 一部片三个版本各被赞 2 次，榜上就是三条，各自显示自己那 2 次，靠版本标识区分给谁点的赞；
    /// 卡片上的 likeCount 仍是这部片的总数（求和），两个数各说各的事，别混成一个。
    /// 排序用那一版自己的赞数，赞数相同取最近一次点赞时间。
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
                SELECT {VideoCardQuery.ColumnsWithSeries},
                       COUNT(vl.id) AS version_like_count, MAX(vl.liked_at) AS like_time,
                       lf.id AS liked_file_id, {VersionNameSql} AS liked_version
                FROM video_likes vl
                JOIN videos v ON vl.video_id = v.id
                {VideoCardQuery.FileJoin}
                LEFT JOIN video_files lf ON lf.id = vl.file_id
                LEFT JOIN video_series s ON v.seriesid = s.id
                WHERE vl.target_type = 'video' AND df.file_size > 0
                GROUP BY vl.video_id, lf.id
                ORDER BY version_like_count DESC, like_time DESC, v.id ASC
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
    /// <summary>日文原名。null 表示这次不动它（与 "" 清空区分开）</summary>
    [JsonPropertyName("originalName")]
    public string? OriginalName { get; set; }
    /// <summary>发行日期，只收 YYYY / YYYY-MM / YYYY-MM-DD</summary>
    [JsonPropertyName("releaseDate")]
    public string? ReleaseDate { get; set; }
    /// <summary>片商 id：一部片只有一家，与 seriesId 同形。null 表示这次不动，空串表示清空</summary>
    [JsonPropertyName("studioId")]
    public string? StudioId { get; set; }
    /// <summary>设片商时要不要顺手补到同系列还没填片商的影片。不传按 true 算</summary>
    [JsonPropertyName("syncSeriesStudio")]
    public bool? SyncSeriesStudio { get; set; }
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
    /// <summary>日文原名。null 表示这次不动它（与 "" 清空区分开）</summary>
    [JsonPropertyName("originalName")]
    public string? OriginalName { get; set; }
    /// <summary>发行日期，只收 YYYY / YYYY-MM / YYYY-MM-DD</summary>
    [JsonPropertyName("releaseDate")]
    public string? ReleaseDate { get; set; }
    /// <summary>片商 id：一部片只有一家，与 seriesId 同形。null 表示这次不动，空串表示清空</summary>
    [JsonPropertyName("studioId")]
    public string? StudioId { get; set; }
    /// <summary>设片商时要不要顺手补到同系列还没填片商的影片。不传按 true 算</summary>
    [JsonPropertyName("syncSeriesStudio")]
    public bool? SyncSeriesStudio { get; set; }
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

#endregion

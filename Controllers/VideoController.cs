using Microsoft.AspNetCore.Mvc;
using ckapi.Models;
using ckapi.Services;
using ckapi.Utils;
using ckapi.Results;
using Microsoft.Data.Sqlite;

namespace ckapi.Controllers;

/// <summary>
/// 影片控制器
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class VideoController : ControllerBase
{
    private readonly SQLiteHelper _db;
    private readonly ILogger<VideoController> _logger;
    private readonly Services.SambaService _sambaService;

    public VideoController(SQLiteHelper db, ILogger<VideoController> logger, Services.SambaService sambaService)
    {
        _db = db;
        _logger = logger;
        _sambaService = sambaService;
    }

    /// <summary>
    /// 获取视频列表
    /// </summary>
    [HttpGet("list")]
    public IActionResult GetList(
        [FromQuery] int pageIndex = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? country = null,
        [FromQuery] string? seriesId = null)
    {
        try
        {
            var page = pageIndex;
            var offset = (page - 1) * pageSize;
            var whereClause = "WHERE 1=1";
            var parameters = new List<SqliteParameter>();

            if (!string.IsNullOrEmpty(country))
            {
                whereClause += " AND v.country = @country";
                parameters.Add(new SqliteParameter("@country", country));
            }

            if (!string.IsNullOrEmpty(seriesId))
            {
                whereClause += " AND v.seriesid = @seriesId";
                parameters.Add(new SqliteParameter("@seriesId", seriesId));
            }

            // 获取总数
            var countSql = $"SELECT COUNT(*) FROM Video v {whereClause}";
            var total = Convert.ToInt32(_db.ExecuteScalar(countSql, parameters.ToArray()));

            // 获取列表
            var sql = $@"
                SELECT v.* FROM Video v
                {whereClause}
                ORDER BY v.sortorder ASC, v.ctime DESC
                LIMIT @pageSize OFFSET @offset";
            parameters.Add(new SqliteParameter("@pageSize", pageSize));
            parameters.Add(new SqliteParameter("@offset", offset));

            var dt = _db.ExecuteDataTable(sql, parameters.ToArray());

            var videos = new List<Video>();
            foreach (System.Data.DataRow row in dt.Rows)
            {
                var video = new Video
                {
                    Id = row["id"]?.ToString(),
                    Code = row["code"]?.ToString(),
                    Name = row["name"]?.ToString(),
                    Country = row["country"]?.ToString(),
                    CoverUrl = row["coverurl"]?.ToString(),
                    VideoUrl = row["videourl"]?.ToString(),
                    VideoSize = row["videosize"] != DBNull.Value ? Convert.ToInt64(row["videosize"]) : 0,
                    Quality = row["quality"]?.ToString(),
                    SeriesId = row["seriesid"]?.ToString(),
                    SortOrder = row["sortorder"] != DBNull.Value ? Convert.ToInt32(row["sortorder"]) : 0,
                    CTime = row["ctime"]?.ToString(),
                    UTime = row["utime"]?.ToString(),
                    Actors = new List<Actor>()
                };
                
                // 获取该视频的演员列表
                var actorSql = @"
                    SELECT a.* FROM Actor a 
                    INNER JOIN VideoActor va ON a.id = va.actorid 
                    WHERE va.videoid = @videoId
                    ORDER BY a.name";
                var actorDt = _db.ExecuteDataTable(actorSql, 
                    new SqliteParameter("@videoId", video.Id));
                
                foreach (System.Data.DataRow actorRow in actorDt.Rows)
                {
                    video.Actors.Add(new Actor
                    {
                        Id = actorRow["id"]?.ToString(),
                        Name = actorRow["name"]?.ToString(),
                        Country = actorRow["country"]?.ToString()
                    });
                }
                
                videos.Add(video);
            }

            return Ok(new { success = true, data = new { list = videos, total, page, pageSize } });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取视频列表失败");
            return Ok(new { success = false, message = "获取视频列表失败" });
        }
    }

    /// <summary>
    /// 获取视频详情
    /// </summary>
    [HttpGet("{id}")]
    public IActionResult GetVideo(string id)
    {
        try
        {
            var sql = "SELECT * FROM Video WHERE id = @id";
            var dt = _db.ExecuteDataTable(sql, new SqliteParameter("@id", id));
            if (dt.Rows.Count == 0)
            {
                return Ok(new { success = false, message = "视频不存在" });
            }

            var row = dt.Rows[0];
            var video = new Video
            {
                Id = row["id"]?.ToString(),
                Code = row["code"]?.ToString(),
                Name = row["name"]?.ToString(),
                Country = row["country"]?.ToString(),
                CoverUrl = row["coverurl"]?.ToString(),
                VideoUrl = row["videourl"]?.ToString(),
                VideoSize = row["videosize"] != DBNull.Value ? Convert.ToInt64(row["videosize"]) : 0,
                Quality = row["quality"]?.ToString(),
                SeriesId = row["seriesid"]?.ToString(),
                SortOrder = row["sortorder"] != DBNull.Value ? Convert.ToInt32(row["sortorder"]) : 0,
                CTime = row["ctime"]?.ToString(),
                UTime = row["utime"]?.ToString(),
                Actors = new List<Actor>()
            };

            // 获取关联的演员列表
            var actorSql = @"
                SELECT a.* FROM Actor a
                INNER JOIN VideoActor va ON a.id = va.actorid
                WHERE va.videoid = @videoId
                ORDER BY a.name";
            var actorDt = _db.ExecuteDataTable(actorSql, new SqliteParameter("@videoId", id));

            var actors = new List<Actor>();
            foreach (System.Data.DataRow actorRow in actorDt.Rows)
            {
                actors.Add(new Actor
                {
                    Id = actorRow["id"]?.ToString(),
                    Name = actorRow["name"]?.ToString(),
                    Alias = actorRow["alias"]?.ToString(),
                    Country = actorRow["country"]?.ToString(),
                    CTime = actorRow["ctime"]?.ToString(),
                    UTime = actorRow["utime"]?.ToString()
                });
            }

            // 获取所属系列
            object seriesData = null;
            if (!string.IsNullOrEmpty(video.SeriesId))
            {
                var seriesSql = "SELECT * FROM VideoSeries WHERE id = @seriesId";
                var seriesDt = _db.ExecuteDataTable(seriesSql, new SqliteParameter("@seriesId", video.SeriesId));
                if (seriesDt.Rows.Count > 0)
                {
                    var sRow = seriesDt.Rows[0];
                    seriesData = new
                    {
                        id = sRow["id"]?.ToString(),
                        name = sRow["name"]?.ToString()
                    };
                }
            }

            return Ok(new { success = true, data = video, actors, series = seriesData });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取视频详情失败");
            return Ok(new { success = false, message = "获取视频详情失败" });
        }
    }

    /// <summary>
    /// 获取今日推荐
    /// </summary>
    [HttpGet("recommend")]
    public IActionResult GetRecommend([FromQuery] string videoId = "", [FromQuery] int limit = 10)
    {
        try
        {
            // 确保limit为偶数且至少8
            if (limit < 8) limit = 8;
            if (limit % 2 != 0) limit++;

            List<string> videoIds = new List<string>();

            if (!string.IsNullOrEmpty(videoId))
            {
                // 获取当前视频所属系列ID
                var seriesSql = "SELECT seriesid FROM Video WHERE id = @id";
                var seriesDt = _db.ExecuteDataTable(seriesSql, new SqliteParameter("@id", videoId));
                string seriesId = seriesDt.Rows.Count > 0 ? seriesDt.Rows[0]["seriesid"]?.ToString() : null;

                // 优先推荐同系列影片
                if (!string.IsNullOrEmpty(seriesId))
                {
                    var sameSeriesSql = "SELECT id FROM Video WHERE seriesid = @seriesId AND id != @id ORDER BY ctime DESC";
                    var sameSeriesDt = _db.ExecuteDataTable(sameSeriesSql,
                        new SqliteParameter("@seriesId", seriesId),
                        new SqliteParameter("@id", videoId));
                    foreach (System.Data.DataRow r in sameSeriesDt.Rows)
                    {
                        videoIds.Add(r["id"].ToString());
                    }
                }

                // 其次推荐同演员影片
                var sameActorSql = @"
                    SELECT v.id FROM Video v
                    INNER JOIN VideoActor va ON v.id = va.videoid
                    INNER JOIN VideoActor va2 ON va.actorid = va2.actorid
                    WHERE va2.videoid = @id AND v.id != @id
                    ORDER BY v.ctime DESC";
                var sameActorDt = _db.ExecuteDataTable(sameActorSql, new SqliteParameter("@id", videoId));
                foreach (System.Data.DataRow r in sameActorDt.Rows)
                {
                    var vid = r["id"].ToString();
                    if (!videoIds.Contains(vid)) videoIds.Add(vid);
                }

                // 如果不够，随机推荐
                if (videoIds.Count < limit)
                {
                    var idList = string.Join(",", videoIds.Select((_, i) => $"@eid{i}"));
                    var excludeSql = string.IsNullOrEmpty(idList)
                        ? "SELECT id FROM Video WHERE id != @id ORDER BY RANDOM()"
                        : $"SELECT id FROM Video WHERE id != @id AND id NOT IN ({idList}) ORDER BY RANDOM()";
                    var excludeParams = new List<SqliteParameter>
                    {
                        new SqliteParameter("@id", videoId)
                    };
                    for (int i = 0; i < videoIds.Count; i++)
                    {
                        excludeParams.Add(new SqliteParameter($"@eid{i}", videoIds[i]));
                    }
                    var remain = limit - videoIds.Count;
                    var randomDt = _db.ExecuteDataTable(excludeSql, excludeParams.ToArray());
                    foreach (System.Data.DataRow r in randomDt.Rows)
                    {
                        videoIds.Add(r["id"].ToString());
                        remain--;
                        if (remain <= 0) break;
                    }
                }

                // 限制数量并取前limit个
                videoIds = videoIds.Take(limit).ToList();
            }
            else
            {
                // 无videoId时随机推荐
                var randomSql = "SELECT id FROM Video ORDER BY RANDOM() LIMIT @limit";
                var randomDt = _db.ExecuteDataTable(randomSql, new SqliteParameter("@limit", limit));
                foreach (System.Data.DataRow r in randomDt.Rows)
                {
                    videoIds.Add(r["id"].ToString());
                }
            }

            if (videoIds.Count == 0)
            {
                return Ok(new { success = true, data = new List<Video>() });
            }

            // 批量查询视频详情
            var idsStr = string.Join(",", videoIds.Select((_, i) => $"@vid{i}"));
            var fetchSql = $"SELECT * FROM Video WHERE id IN ({idsStr})";
            var fetchParams = new List<SqliteParameter>();
            for (int i = 0; i < videoIds.Count; i++)
            {
                fetchParams.Add(new SqliteParameter($"@vid{i}", videoIds[i]));
            }
            var dt = _db.ExecuteDataTable(fetchSql, fetchParams.ToArray());

            var videos = new List<Video>();
            foreach (System.Data.DataRow row in dt.Rows)
            {
                var video = new Video
                {
                    Id = row["id"]?.ToString(),
                    Code = row["code"]?.ToString(),
                    Name = row["name"]?.ToString(),
                    Country = row["country"]?.ToString(),
                    CoverUrl = row["coverurl"]?.ToString(),
                    VideoUrl = row["videourl"]?.ToString(),
                    VideoSize = row["videosize"] != DBNull.Value ? Convert.ToInt64(row["videosize"]) : 0,
                    Quality = row["quality"]?.ToString(),
                    SeriesId = row["seriesid"]?.ToString(),
                    SortOrder = row["sortorder"] != DBNull.Value ? Convert.ToInt32(row["sortorder"]) : 0,
                    CTime = row["ctime"]?.ToString(),
                    UTime = row["utime"]?.ToString(),
                    Actors = new List<Actor>()
                };

                // 获取该视频的演员列表
                var actorSql = @"
                    SELECT a.* FROM Actor a 
                    INNER JOIN VideoActor va ON a.id = va.actorid 
                    WHERE va.videoid = @videoId
                    ORDER BY a.name";
                var actorDt = _db.ExecuteDataTable(actorSql, new SqliteParameter("@videoId", video.Id));
                foreach (System.Data.DataRow actorRow in actorDt.Rows)
                {
                    video.Actors.Add(new Actor
                    {
                        Id = actorRow["id"]?.ToString(),
                        Name = actorRow["name"]?.ToString(),
                        Country = actorRow["country"]?.ToString()
                    });
                }

                videos.Add(video);
            }

            // 保持推荐顺序
            return Ok(new { success = true, data = videos.OrderBy(v => videoIds.IndexOf(v.Id)).ToList() });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取推荐视频失败");
            return Ok(new { success = false, message = "获取推荐视频失败" });
        }
    }


    /// <summary>
    /// 获取最新上映
    /// </summary>
    [HttpGet("latest")]
    public IActionResult GetLatest([FromQuery] int limit = 10)
    {
        try
        {
            if (limit <= 0) limit = 10;
            var sql = @"
                SELECT v.* FROM Video v
                ORDER BY v.ctime DESC
                LIMIT @limit";
            var dt = _db.ExecuteDataTable(sql, new SqliteParameter("@limit", limit));
            var videos = new List<Video>();
            foreach (System.Data.DataRow row in dt.Rows)
            {
                videos.Add(new Video
                {
                    Id = row["id"]?.ToString(),
                    Code = row["code"]?.ToString(),
                    Name = row["name"]?.ToString(),
                    Country = row["country"]?.ToString(),
                    CoverUrl = row["coverurl"]?.ToString(),
                    VideoUrl = row["videourl"]?.ToString(),
                    VideoSize = row["videosize"] != DBNull.Value ? Convert.ToInt64(row["videosize"]) : 0,
                    Quality = row["quality"]?.ToString(),
                    SeriesId = row["seriesid"]?.ToString(),
                    SortOrder = row["sortorder"] != DBNull.Value ? Convert.ToInt32(row["sortorder"]) : 0,
                    CTime = row["ctime"]?.ToString(),
                    UTime = row["utime"]?.ToString(),
                    Actors = new List<Actor>()
                });
            }
            return Ok(new { success = true, data = videos });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取最新视频失败");
            return Ok(new { success = false, message = "获取最新视频失败" });
        }
    }


    /// <summary>
    /// 获取最受喜爱
    /// </summary>
    [HttpGet("most-liked")]
    public IActionResult GetMostLiked([FromQuery] int limit = 10)
    {
        try
        {
            if (limit <= 0) limit = 10;
            var sql = @"
                SELECT v.* FROM Video v
                INNER JOIN LikeRecord lr ON v.id = lr.videoid
                GROUP BY v.id
                ORDER BY COUNT(lr.id) DESC
                LIMIT @limit";
            var dt = _db.ExecuteDataTable(sql, new SqliteParameter("@limit", limit));
            var videos = new List<Video>();
            foreach (System.Data.DataRow row in dt.Rows)
            {
                videos.Add(new Video
                {
                    Id = row["id"]?.ToString(),
                    Code = row["code"]?.ToString(),
                    Name = row["name"]?.ToString(),
                    Country = row["country"]?.ToString(),
                    CoverUrl = row["coverurl"]?.ToString(),
                    VideoUrl = row["videourl"]?.ToString(),
                    VideoSize = row["videosize"] != DBNull.Value ? Convert.ToInt64(row["videosize"]) : 0,
                    Quality = row["quality"]?.ToString(),
                    SeriesId = row["seriesid"]?.ToString(),
                    SortOrder = row["sortorder"] != DBNull.Value ? Convert.ToInt32(row["sortorder"]) : 0,
                    CTime = row["ctime"]?.ToString(),
                    UTime = row["utime"]?.ToString(),
                    Actors = new List<Actor>()
                });
            }
            return Ok(new { success = true, data = videos });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取最受喜爱视频失败");
            return Ok(new { success = false, message = "获取最受喜爱视频失败" });
        }
    }

    /// <summary>
    /// 添加视频
    /// </summary>
    [HttpPost]
    public IActionResult AddVideo([FromBody] Video video)
    {
        try
        {
            if (string.IsNullOrEmpty(video.Name))
            {
                return Ok(new { success = false, message = "视频名称不能为空" });
            }

            video.Id = Guid.NewGuid().ToString();
            video.CTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            video.UTime = video.CTime;

            var sql = @"
                INSERT INTO Video (id, code, name, country, coverurl, videourl, videosize, quality, seriesid, sortorder, ctime, utime)
                VALUES (@id, @code, @name, @country, @coverurl, @videourl, @videosize, @quality, @seriesid, @sortorder, @ctime, @utime)";

            _db.ExecuteNonQuery(sql,
                new SqliteParameter("@id", video.Id),
                new SqliteParameter("@code", video.Code ?? ""),
                new SqliteParameter("@name", video.Name),
                new SqliteParameter("@country", video.Country ?? ""),
                new SqliteParameter("@coverurl", video.CoverUrl ?? ""),
                new SqliteParameter("@videourl", video.VideoUrl ?? ""),
                new SqliteParameter("@videosize", video.VideoSize),
                new SqliteParameter("@quality", video.Quality ?? ""),
                new SqliteParameter("@seriesid", video.SeriesId ?? ""),
                new SqliteParameter("@sortorder", video.SortOrder),
                new SqliteParameter("@ctime", video.CTime),
                new SqliteParameter("@utime", video.UTime)
            );

            return Ok(new { success = true, data = video, message = "添加成功" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "添加视频失败");
            return Ok(new { success = false, message = "添加视频失败: " + ex.Message });
        }
    }

    /// <summary>
    /// 更新视频
    /// </summary>
    [HttpPut("{id}")]
    public IActionResult UpdateVideo(string id, [FromBody] Video video)
    {
        try
        {
            video.UTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            var sql = @"
                UPDATE Video 
                SET code = @code, 
                    name = @name, 
                    country = @country,
                    coverurl = @coverurl,
                    videourl = @videourl,
                    videosize = @videosize, 
                    quality = @quality, 
                    seriesid = @seriesid, 
                    sortorder = @sortorder, 
                    utime = @utime
                WHERE id = @id";

            var rows = _db.ExecuteNonQuery(sql,
                new SqliteParameter("@code", video.Code ?? ""),
                new SqliteParameter("@name", video.Name ?? ""),
                new SqliteParameter("@country", video.Country ?? ""),
                new SqliteParameter("@coverurl", video.CoverUrl ?? ""),
                new SqliteParameter("@videourl", video.VideoUrl ?? ""),
                new SqliteParameter("@videosize", video.VideoSize),
                new SqliteParameter("@quality", video.Quality ?? ""),
                new SqliteParameter("@seriesid", video.SeriesId ?? ""),
                new SqliteParameter("@sortorder", video.SortOrder),
                new SqliteParameter("@utime", video.UTime),
                new SqliteParameter("@id", id)
            );

            if (rows > 0)
            {
                return Ok(new { success = true, message = "更新成功" });
            }
            return Ok(new { success = false, message = "视频不存在" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新视频失败");
            return Ok(new { success = false, message = "更新视频失败" });
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
            var rows = _db.ExecuteNonQuery(
                "DELETE FROM Video WHERE id = @id",
                new SqliteParameter("@id", id));

            if (rows > 0)
            {
                return Ok(new { success = true, message = "删除成功" });
            }
            return Ok(new { success = false, message = "视频不存在" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除视频失败");
            return Ok(new { success = false, message = "删除视频失败" });
        }
    }

    /// <summary>
    /// 视频流接口 - 从 Samba 读取视频文件并流式返回
    /// 支持 HTTP Range 请求（视频拖拽）
    /// </summary>
    [HttpGet("stream/{id}")]
    public async Task<IActionResult> GetVideoStream(string id)
    {
        try
        {
            // 1. 查询视频记录
            var sql = "SELECT videourl, coverurl, name FROM Video WHERE id = @id";
            var dt = _db.ExecuteDataTable(sql, new SqliteParameter("@id", id));
            if (dt.Rows.Count == 0)
                return NotFound(new { success = false, message = "视频不存在" });

            var videoUrl = dt.Rows[0]["videourl"]?.ToString() ?? "";
            var videoName = dt.Rows[0]["name"]?.ToString() ?? "video";

            // 2. 解析真实文件路径
            string? filePath = null;

            if (_sambaService.IsEnabled && !string.IsNullOrWhiteSpace(_sambaService.GetVideoLocalPath(videoUrl)))
            {
                // Samba 模式：使用挂载路径
                filePath = _sambaService.GetVideoLocalPath(SambaService.ExtractRelativePath(videoUrl));
            }
            else
            {
                // HTTP 模式：后端代理读取，避免 Redirect 非 ASCII 字符崩溃
                if (videoUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    return await ProxyHttpFile(videoUrl, GetContentType(videoUrl));
                }
                // 本地文件模式
                filePath = videoUrl;
            }

            if (string.IsNullOrWhiteSpace(filePath) || !System.IO.File.Exists(filePath))
            {
                _logger.LogWarning("视频文件不存在: {Path}", filePath);
                return NotFound(new { success = false, message = "视频文件不存在" });
            }

            // 3. 支持 Range 请求（视频拖拽）
            var contentType = GetContentType(filePath);
            return PhysicalFile(filePath, contentType, enableRangeProcessing: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "视频流读取失败: {VideoId}", id);
            return StatusCode(500, new { success = false, message = "视频流读取失败" });
        }
    }

    /// <summary>
    /// 封面图接口 - 从 Samba 读取封面文件
    /// </summary>
    [HttpGet("cover/{id}")]
    public async Task<IActionResult> GetCoverImage(string id)
    {
        try
        {
            var sql = "SELECT coverurl FROM Video WHERE id = @id";
            var dt = _db.ExecuteDataTable(sql, new SqliteParameter("@id", id));
            if (dt.Rows.Count == 0)
                return NotFound();

            var coverUrl = dt.Rows[0]["coverurl"]?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(coverUrl))
                return NotFound();

            string? filePath = null;
            if (_sambaService.IsEnabled && !string.IsNullOrWhiteSpace(_sambaService.GetCoverLocalPath(coverUrl)))
            {
                filePath = _sambaService.GetCoverLocalPath(SambaService.ExtractRelativePath(coverUrl));
            }
            else
            {
                // HTTP 模式：后端代理读取，避免 Redirect 非 ASCII 字符崩溃
                if (coverUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    return await ProxyHttpFile(coverUrl, GetContentType(coverUrl));
                }
                filePath = coverUrl;
            }

            if (string.IsNullOrWhiteSpace(filePath) || !System.IO.File.Exists(filePath))
                return NotFound();

            var contentType = GetContentType(filePath);
            return PhysicalFile(filePath, contentType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "封面图读取失败: {VideoId}", id);
            return StatusCode(500);
        }
    }

    /// <summary>
    /// 根据文件扩展名获取 Content-Type
    /// </summary>
    private static string GetContentType(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".ogg" => "video/ogg",
            ".mov" => "video/quicktime",
            ".mkv" => "video/x-matroska",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => "application/octet-stream"
        };
    }

    private static readonly HttpClient _proxyHttpClient;

    static VideoController()
    {
        var handler = new HttpClientHandler();
        // 强制 HTTP/1.1，避免上游不支持 HTTP/2 导致 502
        handler.SslProtocols = System.Security.Authentication.SslProtocols.None;
        _proxyHttpClient = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        _proxyHttpClient.DefaultRequestHeaders.Connection.ParseAdd("close");
        _proxyHttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; CKAPI/1.0)");
        _proxyHttpClient.DefaultRequestHeaders.Accept.ParseAdd("image/*, video/*, */*");
    }

    /// <summary>
    /// 代理读取 HTTP 文件并返回 FileStreamResult（避免 Redirect 非 ASCII 字符崩溃）
    /// </summary>
    private async Task<IActionResult> ProxyHttpFile(string url, string? contentType = null)
    {
        try
        {
            // 对 URL 中的非 ASCII 字符做 percent-encoding
            var encodedUrl = EncodeNonAsciiUrl(url);

            var response = await _proxyHttpClient.GetAsync(encodedUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            // 流式转发到浏览器（不缓冲整个文件）
            contentType ??= response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            return new StreamForwardResult(response.Content, contentType, response);
        }
        catch (HttpRequestException ex) when (ex.StatusCode.HasValue)
        {
            _logger.LogError(ex, "代理读取文件失败 (上游返回 {StatusCode}): {Url}", ex.StatusCode, url);
            return StatusCode((int)ex.StatusCode.Value, new { success = false, message = $"视频源服务器返回错误: {ex.StatusCode}" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "代理读取文件失败: {Url}", url);
            return StatusCode(502, new { success = false, message = "视频源服务器不可用" });
        }
    }

    /// <summary>
    /// 对 URL 路径中的非 ASCII 字符进行 percent-encoding，保留斜杠不编码
    /// 避免使用 UriBuilder.Uri / new Uri(...) 避免重编码导致双重编码
    /// </summary>
    private static string EncodeNonAsciiUrl(string url)
    {
        var uri = new Uri(url);
        var sb = new System.Text.StringBuilder();
        foreach (var c in uri.AbsolutePath)
        {
            if (c >= 0x80)
                sb.Append(Uri.EscapeDataString(c.ToString()));
            else if (c == '/')
                sb.Append('/');
            else
                sb.Append(c);
        }
        var port = uri.Port;
        var portStr = (port > 0 && port != 80) ? $":{port}" : "";
        return $"{uri.Scheme}://{uri.Host}{portStr}{sb}";
    }
}

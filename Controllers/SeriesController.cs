using ckapi.Models;
using ckapi.Utils;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace ckapi.Controllers;

/// <summary>
/// 影视系列相关接口
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class SeriesController : ControllerBase
{
    private readonly ILogger<SeriesController> _logger;
    private readonly SQLiteHelper _db;
    private readonly IConfiguration _config;

    public SeriesController(ILogger<SeriesController> logger, SQLiteHelper db, IConfiguration config)
    {
        _logger = logger;
        _db = db;
        _config = config;
    }

    private SqliteConnection GetConnection()
    {
        return new SqliteConnection(_config.GetConnectionString("DefaultConnection"));
    }

    /// <summary>
    /// 获取系列列表
    /// </summary>
    [HttpGet]
    public IActionResult GetSeriesList([FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] string? country = null, [FromQuery] string? keyword = null,
        [FromQuery] string? sortBy = null)
    {
        try
        {
            var offset = (page - 1) * pageSize;
            var whereClause = "WHERE 1=1";
            var parameters = new List<SqliteParameter>();

            // 排序
            var orderBy = sortBy?.ToLower() switch
            {
                "name" => "s.name ASC",
                "likecount" => "like_count DESC",
                "videocount" => "video_count DESC",
                _ => "s.ctime DESC"
            };

            if (!string.IsNullOrEmpty(country))
            {
                whereClause += " AND country = @country";
                parameters.Add(new SqliteParameter("@country", country));
            }

            if (!string.IsNullOrEmpty(keyword))
            {
                whereClause += " AND (name LIKE @keyword OR alias LIKE @keyword)";
                parameters.Add(new SqliteParameter("@keyword", $"%{keyword}%"));
            }

            var countSql = $"SELECT COUNT(*) FROM video_series s {whereClause}";
            var total = Convert.ToInt32(_db.ExecuteScalar(countSql, parameters.ToArray()));

            var sql = $@"
                SELECT s.*, 
                       (SELECT COUNT(*) FROM videos v WHERE v.seriesid = s.id) as video_count,
                       (SELECT COUNT(*) FROM video_likes vl JOIN videos v ON vl.video_id = v.id WHERE v.seriesid = s.id) as like_count
                FROM video_series s
                {whereClause}
                ORDER BY " + orderBy + @"
                LIMIT @pageSize OFFSET @offset";
            parameters.Add(new SqliteParameter("@pageSize", pageSize));
            parameters.Add(new SqliteParameter("@offset", offset));

            var dt = _db.ExecuteDataTable(sql, parameters.ToArray());
            var series = new List<VideoSeries>();
            foreach (System.Data.DataRow row in dt.Rows)
            {
                series.Add(new VideoSeries
                {
                    Id = row["id"]?.ToString(),
                    Name = row["name"]?.ToString(),
                    Alias = row["alias"]?.ToString(),
                    Link = row["link"]?.ToString(),
                    Country = row["country"]?.ToString(),
                    CTime = row["ctime"]?.ToString(),
                    UTime = row["utime"]?.ToString(),
                    VideoCount = row["video_count"] != DBNull.Value ? Convert.ToInt32(row["video_count"]) : 0,
                    LikeCount = row["like_count"] != DBNull.Value ? Convert.ToInt32(row["like_count"]) : 0
                });
            }

            return Ok(new { success = true, data = series, total, page, pageSize });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取系列列表失败");
            return Ok(new { success = false, message = "获取系列列表失败" });
        }
    }

    /// <summary>
    /// 获取系列详情
    /// </summary>
    [HttpGet("{id}")]
    public IActionResult GetSeries(string id)
    {
        try
        {
            var sql = @"SELECT s.*, 
                        (SELECT COUNT(*) FROM video_likes vl JOIN videos v ON vl.video_id = v.id WHERE v.seriesid = s.id) as like_count
                        FROM video_series s WHERE s.id = @id";
            var dt = _db.ExecuteDataTable(sql, new SqliteParameter("@id", id));
            if (dt.Rows.Count == 0)
            {
                return Ok(new { success = false, message = "系列不存在" });
            }

            var row = dt.Rows[0];
            var series = new VideoSeries
            {
                Id = row["id"]?.ToString(),
                Name = row["name"]?.ToString(),
                Alias = row["alias"]?.ToString(),
                Link = row["link"]?.ToString(),
                Country = row["country"]?.ToString(),
                CTime = row["ctime"]?.ToString(),
                UTime = row["utime"]?.ToString(),
                LikeCount = row["like_count"] != DBNull.Value ? Convert.ToInt32(row["like_count"]) : 0
            };

            return Ok(new { success = true, data = series });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取系列详情失败");
            return Ok(new { success = false, message = "获取系列详情失败" });
        }
    }

    /// <summary>
    /// 获取系列下的影片
    /// </summary>
    [HttpGet("{id}/videos")]
    public IActionResult GetSeriesVideos(string id, [FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        try
        {
            var offset = (page - 1) * pageSize;
            using var conn = GetConnection();
            conn.Open();

            var countSql = "SELECT COUNT(*) FROM videos WHERE seriesid = @seriesid";
            using (var countCmd = new SqliteCommand(countSql, conn))
            {
                countCmd.Parameters.Add(new SqliteParameter("@seriesid", id));
                var total = Convert.ToInt32(countCmd.ExecuteScalar());

                var sql = @"
                    SELECT v.id, v.code, v.name, v.category, v.country, v.cover_path, v.file_path, v.file_size, v.seriesid, v.ctime,
                        (SELECT COUNT(*) FROM video_likes vl WHERE vl.video_id = v.id) as like_count,
                        (SELECT GROUP_CONCAT(a.id || '|' || a.name) FROM actors a 
                         INNER JOIN video_actors va ON a.id = va.actor_id 
                         WHERE va.video_id = v.id) as actor_names
                    FROM videos v 
                    WHERE v.seriesid = @seriesid
                    ORDER BY v.ctime DESC
                    LIMIT @pageSize OFFSET @offset";

                using var cmd = new SqliteCommand(sql, conn);
                cmd.Parameters.Add(new SqliteParameter("@seriesid", id));
                cmd.Parameters.Add(new SqliteParameter("@pageSize", pageSize));
                cmd.Parameters.Add(new SqliteParameter("@offset", offset));

                using var reader = cmd.ExecuteReader();
                var videos = new List<object>();
                while (reader.Read())
                {
                    videos.Add(new
                    {
                        Id = reader["id"].ToString(),
                        Code = reader["code"]?.ToString(),
                        Name = reader["name"].ToString(),
                        Category = reader["category"]?.ToString(),
                        Country = reader["country"] == DBNull.Value ? "" : reader["country"].ToString(),
                        CoverPath = reader["cover_path"] == DBNull.Value ? null : reader["cover_path"].ToString(),
                        FilePath = reader["file_path"]?.ToString(),
                        FileSize = reader["file_size"] == DBNull.Value ? 0 : Convert.ToInt64(reader["file_size"]),
                        SeriesId = reader["seriesid"]?.ToString(),
                        Ctime = reader["ctime"]?.ToString(),
                        LikeCount = reader["like_count"] == DBNull.Value ? 0 : Convert.ToInt32(reader["like_count"]),
                        ActorNames = reader["actor_names"]?.ToString()
                    });
                }

                return Ok(new { success = true, data = videos, total, page, pageSize });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取系列影片失败");
            return Ok(new { success = false, message = "获取系列影片失败" });
        }
    }

    /// <summary>
    /// 添加系列
    /// </summary>
    [HttpPost]
    public IActionResult AddSeries([FromBody] VideoSeries series)
    {
        try
        {
            if (string.IsNullOrEmpty(series.Name))
            {
                return Ok(new { success = false, message = "系列名称不能为空" });
            }

            if (string.IsNullOrEmpty(series.Id))
            {
                series.Id = Guid.NewGuid().ToString("N").ToUpper();
            }

            var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            series.CTime = now;
            series.UTime = now;

            var sql = @"
                INSERT INTO video_series (id, name, alias, link, country, ctime, utime)
                VALUES (@id, @name, @alias, @link, @country, @ctime, @utime)";

            _db.ExecuteNonQuery(sql,
                new SqliteParameter("@id", series.Id),
                new SqliteParameter("@name", series.Name),
                new SqliteParameter("@alias", series.Alias ?? ""),
                new SqliteParameter("@link", series.Link ?? ""),
                new SqliteParameter("@country", series.Country ?? ""),
                new SqliteParameter("@ctime", series.CTime),
                new SqliteParameter("@utime", series.UTime)
            );

            return Ok(new { success = true, data = series });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "添加系列失败");
            return Ok(new { success = false, message = "添加系列失败" });
        }
    }

    /// <summary>
    /// 更新系列
    /// </summary>
    [HttpPut("{id}")]
    public IActionResult UpdateSeries(string id, [FromBody] VideoSeries series)
    {
        try
        {
            var sql = @"
                UPDATE video_series SET 
                    name = @name,
                    alias = @alias,
                    link = @link,
                    country = @country,
                    utime = @utime
                WHERE id = @id";

            var result = _db.ExecuteNonQuery(sql,
                new SqliteParameter("@id", id),
                new SqliteParameter("@name", series.Name ?? ""),
                new SqliteParameter("@alias", series.Alias ?? ""),
                new SqliteParameter("@link", series.Link ?? ""),
                new SqliteParameter("@country", series.Country ?? ""),
                new SqliteParameter("@utime", series.UTime)
            );

            if (result > 0)
                return Ok(new { success = true, message = "更新成功" });
            else
                return Ok(new { success = false, message = "系列不存在" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新系列失败");
            return Ok(new { success = false, message = "更新系列失败" });
        }
    }

    /// <summary>
    /// 删除系列
    /// </summary>
    [HttpDelete("{id}")]
    public IActionResult DeleteSeries(string id)
    {
        try
        {
            // 将系列下影片的seriesid置为空
            _db.ExecuteNonQuery("UPDATE videos SET seriesid = NULL WHERE seriesid = @seriesid",
                new SqliteParameter("@seriesid", id));

            // 删除系列
            var result = _db.ExecuteNonQuery("DELETE FROM video_series WHERE id = @id",
                new SqliteParameter("@id", id));

            if (result > 0)
                return Ok(new { success = true, message = "删除成功" });
            else
                return Ok(new { success = false, message = "系列不存在" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除系列失败");
            return Ok(new { success = false, message = "删除系列失败" });
        }
    }
}

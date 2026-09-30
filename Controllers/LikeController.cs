using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace ckapi.Controllers;

/// <summary>
/// 点赞记录管理
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class LikeController : ControllerBase
{
    private readonly IConfiguration _config;
    private readonly ILogger<LikeController> _logger;

    public LikeController(IConfiguration config, ILogger<LikeController> logger)
    {
        _config = config;
        _logger = logger;
    }

    private SqliteConnection GetConnection()
    {
        return new SqliteConnection(_config.GetConnectionString("DefaultConnection"));
    }

    /// <summary>
    /// 查询点赞记录（分页）
    /// </summary>
    [HttpGet("list")]
    public IActionResult GetList([FromQuery] int pageIndex = 1, [FromQuery] int pageSize = 20,
        [FromQuery] string? targetType = null, [FromQuery] string? keyword = null,
        [FromQuery] string? startDate = null, [FromQuery] string? endDate = null)
    {
        try
        {
            pageIndex = Utils.Paging.ClampPage(pageIndex);
            pageSize = Utils.Paging.ClampSize(pageSize);
            var offset = (pageIndex - 1) * pageSize;
            var whereClause = "WHERE 1=1";
            var parameters = new List<SqliteParameter>();

            if (!string.IsNullOrEmpty(targetType))
            {
                whereClause += " AND vl.target_type = @targetType";
                parameters.Add(new SqliteParameter("@targetType", targetType));
            }

            if (!string.IsNullOrEmpty(keyword))
            {
                whereClause += " AND (v.name LIKE @keyword OR v.code LIKE @keyword OR c.name LIKE @keyword)";
                parameters.Add(new SqliteParameter("@keyword", "%" + keyword + "%"));
            }

            if (!string.IsNullOrEmpty(startDate))
            {
                whereClause += " AND DATE(vl.liked_at) >= @startDate";
                parameters.Add(new SqliteParameter("@startDate", startDate));
            }

            if (!string.IsNullOrEmpty(endDate))
            {
                whereClause += " AND DATE(vl.liked_at) <= @endDate";
                parameters.Add(new SqliteParameter("@endDate", endDate));
            }

            var countSql = $@"SELECT COUNT(*) FROM video_likes vl
                LEFT JOIN videos v ON vl.video_id = v.id AND vl.target_type = 'video'
                LEFT JOIN comics c ON vl.video_id = c.id AND vl.target_type = 'comic'
                {whereClause}";
            int total;
            using (var conn = GetConnection())
            {
                conn.Open();
                using var countCmd = new SqliteCommand(countSql, conn);
                foreach (var p in parameters) countCmd.Parameters.Add(p);
                total = Convert.ToInt32(countCmd.ExecuteScalar());

                var sql = $@"
                    SELECT vl.id, vl.video_id, vl.liked_at, vl.target_type, vl.file_id, vl.play_time,
                           v.name as video_name, v.code as video_code, v.cover_path as video_cover,
                           lf.code as version_code,
                           (SELECT vt.name FROM version_types vt WHERE vt.id = lf.type_id) as version_type,
                           NULLIF(TRIM(lf.label), '') as version_label,
                           c.name as comic_name, c.cover_path as comic_cover
                    FROM video_likes vl
                    LEFT JOIN videos v ON vl.video_id = v.id AND vl.target_type = 'video'
                    LEFT JOIN video_files lf ON lf.id = vl.file_id
                    LEFT JOIN comics c ON vl.video_id = c.id AND vl.target_type = 'comic'
                    {whereClause}
                    ORDER BY vl.liked_at DESC
                    LIMIT @pageSize OFFSET @offset";
                using var cmd = new SqliteCommand(sql, conn);
                foreach (var p in parameters) cmd.Parameters.Add(p);
                cmd.Parameters.Add(new SqliteParameter("@pageSize", pageSize));
                cmd.Parameters.Add(new SqliteParameter("@offset", offset));

                var list = new List<object>();
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var isComic = reader["target_type"]?.ToString() == "comic";
                        var name = isComic
                            ? (reader["comic_name"] == DBNull.Value ? null : reader["comic_name"].ToString())
                            : (reader["video_name"] == DBNull.Value ? null : reader["video_name"].ToString());
                        var cover = isComic
                            ? (reader["comic_cover"] == DBNull.Value ? null : reader["comic_cover"].ToString())
                            : (reader["video_cover"] == DBNull.Value ? null : reader["video_cover"].ToString());

                        list.Add(new
                        {
                            id = reader["id"],
                            videoId = reader["video_id"],
                            likedAt = reader["liked_at"],
                            targetType = reader["target_type"],
                            name = name,
                            code = isComic ? null : (reader["video_code"] == DBNull.Value ? null : reader["video_code"].ToString()),
                            coverPath = cover,
                            // 点赞落在哪一版（v11）：这一列让它看得出来，原版行没有类型名，用影片番号本身表示
                            fileId = Str(reader, "file_id"),
                            versionCode = Str(reader, "version_code"),
                            // 类型名优先（湿姐），没有类型才用版本名称，与 VideoFiles.DisplayName 同一口径
                            versionName = Str(reader, "version_type") ?? Str(reader, "version_label"),
                            // 点赞那一刻的播放位置（秒，v12）。老记录没有就是 null，
                            // 前端显示"—"而不是 0：0 会被读成"片头就点了赞"
                            playTime = reader["play_time"] == DBNull.Value ? (double?)null : Convert.ToDouble(reader["play_time"])
                        });
                    }
                }

                return Ok(new { success = true, data = new { list, total } });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetLikeList failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>列不存在或为 NULL 都返回 null：版本信息是后加的，旧记录不该因此整页 500</summary>
    private static string? Str(SqliteDataReader reader, string column)
    {
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (reader.GetName(i).Equals(column, StringComparison.OrdinalIgnoreCase))
                return reader.IsDBNull(i) ? null : reader.GetValue(i)?.ToString();
        }
        return null;
    }

    /// <summary>
    /// 删除单条点赞记录
    /// </summary>
    [HttpDelete("{id}")]
    public IActionResult Delete(int id)
    {
        try
        {
            using var conn = GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand("DELETE FROM video_likes WHERE id = @id", conn);
            cmd.Parameters.Add(new SqliteParameter("@id", id));
            var affected = cmd.ExecuteNonQuery();
            return Ok(new { success = affected > 0 });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DeleteLike failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>
    /// 批量删除点赞记录
    /// </summary>
    [HttpPost("batch-delete")]
    public IActionResult BatchDelete([FromBody] LikeBatchDeleteRequest req)
    {
        try
        {
            if (req?.Ids == null || req.Ids.Count == 0)
                return BadRequest(new { success = false, message = "未提供ID" });

            using var conn = GetConnection();
            conn.Open();
            var deleted = 0;
            foreach (var id in req.Ids)
            {
                using var cmd = new SqliteCommand("DELETE FROM video_likes WHERE id = @id", conn);
                cmd.Parameters.Add(new SqliteParameter("@id", id));
                deleted += cmd.ExecuteNonQuery();
            }
            return Ok(new { success = true, deleted });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BatchDeleteLike failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }
}

public class LikeBatchDeleteRequest
{
    public List<int> Ids { get; set; } = new();
}

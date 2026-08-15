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
                    SELECT vl.id, vl.video_id, vl.liked_at, vl.target_type,
                           v.name as video_name, v.code as video_code, v.cover_path as video_cover,
                           c.name as comic_name, c.cover_path as comic_cover
                    FROM video_likes vl
                    LEFT JOIN videos v ON vl.video_id = v.id AND vl.target_type = 'video'
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
                            coverPath = cover
                        });
                    }
                }

                return Ok(new { success = true, data = new { list, total } });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetLikeList failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
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
            return StatusCode(500, new { success = false, message = ex.Message });
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
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }
}

public class LikeBatchDeleteRequest
{
    public List<int> Ids { get; set; } = new();
}

using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace ckapi.Controllers;

/// <summary>
/// 播放进度（v13 起存在服务端）。
///
/// 为什么值得搬进库：进度原先只在浏览器 localStorage 里，换台设备、清一次缓存就从头看起，
/// 而"看到哪儿"恰恰是最不能丢的那个状态。搬进来之后还有一层用处：点赞已经记了 play_time（v12），
/// "当前进度"和"哪些点被赞过"放在一起才做得出精彩瞬间。
///
/// 归属仍然按**版本行**而不是影片：原版 119 分钟、解说版 21 分钟，共用一个偏移必然错位
/// （这条口径从换原生播放器那轮就定了，别改回去）。
///
/// 这些接口不要求管理口令：写的是自己的观看位置，删了也不毁任何数据。
/// </summary>
[ApiController]
[Route("api/video")]
public class VideoStateController : ControllerBase
{
    private readonly ILogger<VideoStateController> _logger;
    private readonly Utils.SQLiteHelper _db;

    public VideoStateController(ILogger<VideoStateController> logger, Utils.SQLiteHelper db)
    {
        _logger = logger;
        _db = db;
    }

    /// <summary>读某一版看到哪儿了。没有记录返回 position=0，前端不需要判空。</summary>
    [HttpGet("state/{fileId}")]
    public IActionResult Get(string fileId)
    {
        try
        {
            using var conn = _db.GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand("SELECT position FROM file_state WHERE file_id = @f", conn);
            cmd.Parameters.Add(new SqliteParameter("@f", fileId));
            var raw = cmd.ExecuteScalar();
            return Ok(new
            {
                success = true,
                data = new { position = raw is null or DBNull ? 0d : Convert.ToDouble(raw) }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取播放进度失败 fileId={FileId}", fileId);
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>
    /// 批量读。列表页以后要显示"看到 43%"时用得上，一次问完而不是每张卡片一发请求。
    /// </summary>
    [HttpGet("states")]
    public IActionResult GetMany([FromQuery] string ids)
    {
        try
        {
            var list = (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct()
                .Take(200)          // 一次别超过一页的量
                .ToList();
            if (list.Count == 0) return Ok(new { success = true, data = Array.Empty<object>() });

            using var conn = _db.GetConnection();
            conn.Open();
            // id 是库里生成的 GUID，走参数化而不是拼字符串（IN 列表按参数一个个给）
            var sql = "SELECT file_id, position FROM file_state WHERE file_id IN ("
                + string.Join(",", list.Select((_, i) => "@p" + i)) + ")";
            using var cmd = new SqliteCommand(sql, conn);
            for (var i = 0; i < list.Count; i++)
                cmd.Parameters.Add(new SqliteParameter("@p" + i, list[i]));

            var data = new List<object>();
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                    data.Add(new { fileId = reader.GetString(0), position = reader.GetDouble(1) });
            }
            return Ok(new { success = true, data });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "批量读取播放进度失败");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>
    /// 记下位置。position ≤ 1 秒当"我要重看"处理：删掉记录而不是写 0——
    /// 与浏览器那边的口径一致（拖回开头后下次就该从头放）。
    /// </summary>
    [HttpPost("state")]
    public IActionResult Save([FromBody] StateRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.FileId))
                return Ok(new { success = false, message = "没说是哪一版" });

            // 只认属于某部片的版本行，避免前端传错 id 时攒出没人认领的行
            using (var conn = _db.GetConnection())
            {
                using var check = new SqliteCommand("SELECT 1 FROM video_files WHERE id = @f", conn);
                check.Parameters.Add(new SqliteParameter("@f", request.FileId));
                if (check.ExecuteScalar() is null)
                    return Ok(new { success = false, message = "这一版不存在" });
            }

            using var c = _db.GetConnection();
            c.Open();
            using (var del = new SqliteCommand("DELETE FROM file_state WHERE file_id = @f", c))
            {
                del.Parameters.Add(new SqliteParameter("@f", request.FileId));
                del.ExecuteNonQuery();
            }

            if (request.Position is double pos && double.IsFinite(pos) && pos > 1)
            {
                using var ins = new SqliteCommand(
                    "INSERT INTO file_state (file_id, position, updated_at) VALUES (@f, @p, @t)", c);
                ins.Parameters.Add(new SqliteParameter("@f", request.FileId));
                ins.Parameters.Add(new SqliteParameter("@p", Math.Round(pos, 1)));
                ins.Parameters.Add(new SqliteParameter("@t", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
                ins.ExecuteNonQuery();
            }

            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存播放进度失败 fileId={FileId}", request.FileId);
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }
    /// <summary>
    /// 某一版上被点赞的时间点（v12 的 play_time 攒出来的）。
    ///
    /// 单独一个接口而不是塞进详情：绝大多数片子一个都没有，没必要每次进详情页都带一份空数组。
    /// 排序按版本再按位置——界面上是一版一行，行内从左到右按时间。
    /// </summary>
    [HttpGet("{id}/moments")]
    public IActionResult Moments(string id)
    {
        try
        {
            const string sql = @"
                SELECT l.id, l.file_id, l.play_time, l.liked_at,
                       f.code AS file_code,
                       IFNULL((SELECT vt.name FROM version_types vt WHERE vt.id = f.type_id), NULLIF(TRIM(f.label), '')) AS version_name
                FROM video_likes l
                LEFT JOIN video_files f ON f.id = l.file_id
                WHERE l.video_id = @id AND l.target_type = 'video' AND l.play_time IS NOT NULL
                ORDER BY l.file_id, l.play_time";

            var list = new List<object>();
            using var conn = _db.GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.Add(new SqliteParameter("@id", id));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var name = reader["version_name"] == DBNull.Value ? "" : reader["version_name"].ToString();
                list.Add(new
                {
                    likeId = reader.GetInt64(0),
                    fileId = reader["file_id"].ToString(),
                    position = Math.Round(reader.GetDouble(2), 1),
                    likedAt = reader.GetString(3),
                    // 与 VideoFiles.DisplayName 同一口径：类型名优先，其次手填名称，都没有才叫原版
                    versionName = string.IsNullOrWhiteSpace(name) ? "原版" : name.Trim()
                });
            }

            return Ok(new { success = true, data = list });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取点赞时间点失败 videoId={Id}", id);
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }
}

/// <summary>写入某一位播放进度或一个时间点用</summary>
public class StateRequest
{
    [System.Text.Json.Serialization.JsonPropertyName("fileId")]
    public string? FileId { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("position")]
    public double? Position { get; set; }
}

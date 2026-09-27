using ckapi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using System.Text.Json.Serialization;

namespace ckapi.Controllers;

/// <summary>
/// 单部影片的扩展字段写入：原名 / 发行日期 / 外部档案链接。
/// 读不在这儿：详情接口 GET /api/video/{id} 一次带全，界面不必发几次请求。
/// 片商也不在这儿：它是 videos.studioid 一个值，跟着整份 PUT /api/video/{id} 一起写。
/// 路由前缀保持 api/video，与前端既有调用一致。
/// </summary>
[ApiController]
[Route("api/video")]
public class VideoMetaController : ControllerBase
{
    private readonly ILogger<VideoMetaController> _logger;
    private readonly Utils.SQLiteHelper _db;

    public VideoMetaController(ILogger<VideoMetaController> logger, Utils.SQLiteHelper db)
    {
        _logger = logger;
        _db = db;
    }

    /// <summary>
    /// 改原名与发行日期。单独一个接口而不是走 PUT /video/{id}：
    /// 那个整份更新会把 name/category/file_path/cover_path 一起按传入值写，
    /// 只想改个发行日期却漏传封面路径的话，封面就没了。
    /// </summary>
    [HttpPut("{id}/meta")]
    public IActionResult SetMeta(string id, [FromBody] MetaRequest req)
    {
        return Guarded(id, "已保存", (conn, videoId) =>
        {
            if (req.OriginalName is not null)
            {
                var v = req.OriginalName.Trim();
                using var cmd = new SqliteCommand("UPDATE videos SET original_name = @v WHERE id = @id", conn);
                cmd.Parameters.AddWithValue("@v", v.Length == 0 ? (object)DBNull.Value : v);
                cmd.Parameters.AddWithValue("@id", videoId);
                cmd.ExecuteNonQuery();
            }

            if (req.ReleaseDate is not null)
            {
                var raw = req.ReleaseDate.Trim();
                if (raw.Length > 0 && VideoMeta.NormalizeReleaseDate(raw) is null)
                    throw new VideoMeta.MetaException("发行日期只收 2024 / 2024-03 / 2024-03-15 三种写法");

                using var cmd = new SqliteCommand("UPDATE videos SET release_date = @v WHERE id = @id", conn);
                cmd.Parameters.AddWithValue("@v", raw.Length == 0 ? (object)DBNull.Value : raw);
                cmd.Parameters.AddWithValue("@id", videoId);
                cmd.ExecuteNonQuery();
            }
            return "已保存";
        });
    }

    /// <summary>整组替换外部档案链接（校验与去重共用演员外链那套规则）</summary>
    [HttpPut("{id}/links")]
    public IActionResult SetLinks(string id, [FromBody] LinksRequest req)
    {
        return Guarded(id, "档案链接已更新", (conn, _) => $"{VideoMeta.SetLinks(conn, id, req.Links)} 条链接");
    }

    // ---------------------------------------------------------------- 内部

    /// <summary>
    /// 写接口形状一样：先确认影片存在，跑一段 SQL，成功回一句人话。
    /// 抽出来是为了 MetaException 统一转成 200 + success=false——
    /// 参数不合法不是服务器故障，前端不该看到 500。
    /// </summary>
    private IActionResult Guarded(string id, string fallback, Func<SqliteConnection, string, string> work)
    {
        try
        {
            using var conn = _db.GetConnection();
            conn.Open();

            using (var check = new SqliteCommand("SELECT name FROM videos WHERE id = @id", conn))
            {
                check.Parameters.AddWithValue("@id", id);
                if (check.ExecuteScalar() is null) return NotFound(new { success = false, message = "视频不存在" });
            }

            var detail = work(conn, id);
            return Ok(new { success = true, message = detail.Length > 0 ? detail : fallback });
        }
        catch (VideoMeta.MetaException ex)
        {
            return Ok(new { success = false, message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "VideoMeta write failed for {Id}", id);
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    public sealed class LinksRequest
    {
        [JsonPropertyName("links")] public List<Utils.ActorLink>? Links { get; set; }
    }

    public sealed class MetaRequest
    {
        /// <summary>null 表示这次不动；空串表示清空</summary>
        [JsonPropertyName("originalName")] public string? OriginalName { get; set; }
        [JsonPropertyName("releaseDate")] public string? ReleaseDate { get; set; }
    }
}

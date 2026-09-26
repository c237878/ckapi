using ckapi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using System.Text.Json.Serialization;

namespace ckapi.Controllers;

/// <summary>
/// 单部影片的四个扩展块（片商 / 标签 / 关联合辑 / 外部档案链接）的写入。
/// 读不在这儿：详情接口 GET /api/video/{id} 一次带全，界面不必发四次请求。
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

    /// <summary>整组替换片商。只给 name 时自动归一（正名→别名），归不到就新建这家——手填时才这么宽松</summary>
    [HttpPut("{id}/studios")]
    public IActionResult SetStudios(string id, [FromBody] StudiosRequest req)
    {
        return Guarded(id, "片商已更新", (conn, _) =>
        {
            var (attached, created) = VideoMeta.SetStudios(conn, id, req.Items);
            return $"{attached} 家片商" + (created > 0 ? $"（新建 {created} 家）" : "");
        });
    }

    /// <summary>
    /// 整组替换标签。界面上打字允许顺手建新标签（他是词表唯一权威）；
    /// AI 走的是 tag/suggestions 队列，不经过这个接口。
    /// </summary>
    [HttpPut("{id}/tags")]
    public IActionResult SetTags(string id, [FromBody] TagsRequest req)
    {
        return Guarded(id, "标签已更新", (conn, _) =>
        {
            var (attached, unknown) = VideoMeta.SetTags(conn, id, req.Names, "manual", createMissing: true);
            return $"{attached} 个标签" + (unknown.Count > 0 ? $"，另有 {unknown.Count} 个名字没认出来" : "");
        });
    }

    /// <summary>整组替换外部档案链接（校验与去重共用演员外链那套规则）</summary>
    [HttpPut("{id}/links")]
    public IActionResult SetLinks(string id, [FromBody] LinksRequest req)
    {
        return Guarded(id, "档案链接已更新", (conn, _) => $"{VideoMeta.SetLinks(conn, id, req.Links)} 条链接");
    }

    /// <summary>把本片放进一个合辑：给 groupId 是加入已有组，给 name 是先建组再加入</summary>
    [HttpPost("{id}/groups")]
    public IActionResult JoinGroup(string id, [FromBody] GroupRequest req)
    {
        return Guarded(id, "已加入合辑", (conn, _) =>
        {
            var groupId = VideoMeta.EnsureGroup(conn, req.GroupId, req.Name ?? "");
            VideoMeta.AddToGroup(conn, groupId, id);
            return "已加入合辑";
        });
    }

    /// <summary>把本片从一个合辑里摘出来（合辑本身留着，别的成员不受影响）</summary>
    [HttpDelete("{id}/groups/{groupId}")]
    public IActionResult LeaveGroup(string id, string groupId)
    {
        return Guarded(id, "已从合辑移除", (conn, _) =>
        {
            VideoMeta.RemoveFromGroup(conn, groupId, id);
            return "已从合辑移除";
        });
    }

    /// <summary>改合辑里本片的顺序：position 越小越靠前，允许负数与空档（插中间不用整体重排）</summary>
    [HttpPut("{id}/groups/{groupId}/position")]
    public IActionResult SetPosition(string id, string groupId, [FromBody] PositionRequest req)
    {
        return Guarded(id, "顺序已更新", (conn, _) =>
        {
            using var cmd = new SqliteCommand(
                "UPDATE video_group_items SET position = @p WHERE group_id = @g AND video_id = @v", conn);
            cmd.Parameters.AddWithValue("@p", req.Position);
            cmd.Parameters.AddWithValue("@g", groupId);
            cmd.Parameters.AddWithValue("@v", id);
            if (cmd.ExecuteNonQuery() == 0) throw new VideoMeta.MetaException("这部片不在该合辑里");
            return "顺序已更新";
        });
    }

    /// <summary>合辑改名 / 删除（删组只删组，不动里面的影片）</summary>
    [HttpPut("groups/{groupId}")]
    public IActionResult RenameGroup(string groupId, [FromBody] GroupRequest req)
    {
        var name = (req.Name ?? "").Trim();
        if (name.Length == 0) return Ok(new { success = false, message = "合辑名不能为空" });

        try
        {
            using var conn = _db.GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand("UPDATE video_groups SET name = @n WHERE id = @id", conn);
            cmd.Parameters.AddWithValue("@n", name);
            cmd.Parameters.AddWithValue("@id", groupId);
            return cmd.ExecuteNonQuery() > 0
                ? Ok(new { success = true, message = "合辑已改名" })
                : NotFound(new { success = false, message = "合辑不存在" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RenameGroup failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    [HttpDelete("groups/{groupId}")]
    public IActionResult DeleteGroup(string groupId)
    {
        try
        {
            using var conn = _db.GetConnection();
            conn.Open();
            using (var tx = conn.BeginTransaction())
            {
                using (var items = new SqliteCommand("DELETE FROM video_group_items WHERE group_id = @id", conn, tx))
                {
                    items.Parameters.AddWithValue("@id", groupId);
                    items.ExecuteNonQuery();
                }
                using var del = new SqliteCommand("DELETE FROM video_groups WHERE id = @id", conn, tx);
                del.Parameters.AddWithValue("@id", groupId);
                var n = del.ExecuteNonQuery();
                tx.Commit();
                return n > 0
                    ? Ok(new { success = true, message = "合辑已删除（组内影片都还在）" })
                    : NotFound(new { success = false, message = "合辑不存在" });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DeleteGroup failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    // ---------------------------------------------------------------- 内部

    /// <summary>
    /// 四个写接口形状一样：先确认影片存在，跑一段 SQL，成功回一句人话。
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

    public sealed class StudiosRequest
    {
        [JsonPropertyName("items")] public List<VideoMeta.StudioInput>? Items { get; set; }
    }

    public sealed class TagsRequest
    {
        [JsonPropertyName("names")] public List<string>? Names { get; set; }
    }

    public sealed class LinksRequest
    {
        [JsonPropertyName("links")] public List<Utils.ActorLink>? Links { get; set; }
    }

    public sealed class GroupRequest
    {
        [JsonPropertyName("groupId")] public string? GroupId { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
    }

    public sealed class MetaRequest
    {
        /// <summary>null 表示这次不动；空串表示清空</summary>
        [JsonPropertyName("originalName")] public string? OriginalName { get; set; }
        [JsonPropertyName("releaseDate")] public string? ReleaseDate { get; set; }
    }

    public sealed class PositionRequest
    {
        [JsonPropertyName("position")] public int Position { get; set; }
    }
}

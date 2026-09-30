using ckapi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace ckapi.Controllers;

/// <summary>
/// 全库文件名对齐工具：把盘上的文件改成库里记的那个名字。
///
/// v11 起"文件"是 video_files 一行一行，所以这里的遍历对象是版本行：
/// 每一版对齐到自己的**行级番号**（原版行等于影片番号，解说行带着自己的尾巴），
/// 封面在影片层，跟着影片番号走，一部一张。
///
/// 单个版本的重置/删除/上传写回在 api/video/version/*，不在这里——那些都是"某一版"的事。
/// </summary>
[ApiController]
[Route("api/video")]
public class VideoFileController : ControllerBase
{
    private readonly ILogger<VideoFileController> _logger;
    private readonly Utils.SQLiteHelper _db;

    public VideoFileController(ILogger<VideoFileController> logger, Utils.SQLiteHelper db)
    {
        _logger = logger;
        _db = db;
    }

    /// <summary>
    /// 疑似重复：内容指纹相同、但挂在不同影片/不同版本行上的文件。
    ///
    /// 只报证据，不自动合并也不自动删——理由与演员合并那条一致：
    /// "同一个指纹"说明的是文件内容相同，至于该留哪一条（哪条的番号是对的、哪条有封面、
    /// 哪条被演员关联挂着）只有人看得清。误删一个 4GB 文件不可恢复。
    ///
    /// 指纹是扫描顺手算的（头尾 64KB + 大小），所以还没扫到的行不会出现在这里——
    /// 界面上要一起把"已算指纹 / 总行数"显示出来，否则空列表会被误读成"没有重复"。
    /// </summary>
    [HttpGet("duplicates")]
    public IActionResult Duplicates()
    {
        try
        {
            const string sql = @"
                SELECT f.fingerprint, f.id, f.video_id, f.code, f.file_path, IFNULL(f.file_size, 0),
                       v.name, v.code
                FROM video_files f
                JOIN videos v ON v.id = f.video_id
                WHERE f.fingerprint IS NOT NULL AND f.fingerprint <> ''
                  AND f.fingerprint IN (
                      SELECT fingerprint FROM video_files
                      WHERE fingerprint IS NOT NULL AND fingerprint <> ''
                      GROUP BY fingerprint HAVING COUNT(DISTINCT video_id) > 1)
                ORDER BY f.fingerprint, v.code";

            var groups = new List<Dictionary<string, object?>>();
            var seen = new Dictionary<string, Dictionary<string, object?>>();
            using (var conn = _db.GetConnection())
            {
                conn.Open();
                using var cmd = new SqliteCommand(sql, conn);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var fp = reader.GetString(0);
                    if (!seen.TryGetValue(fp, out var group))
                    {
                        group = new Dictionary<string, object?> { ["fingerprint"] = fp, ["files"] = new List<object>() };
                        seen[fp] = group;
                        groups.Add(group);
                    }
                    ((List<object>)group["files"]!).Add(new
                    {
                        fileId = reader.GetString(1),
                        videoId = reader.GetString(2),
                        fileCode = reader.GetString(3),
                        path = reader.IsDBNull(4) ? "" : reader.GetString(4),
                        size = reader.GetInt64(5),
                        videoName = reader.GetString(6),
                        videoCode = reader.GetString(7)
                    });
                }
            }

            // 覆盖率给界面当"空列表意味着什么"的说明：没扫到指纹的行不会出现在结果里，
            // 所以 0 组既可能是真没重复，也可能是还没扫
            int fingerprinted, filesWithContent;
            using (var conn2 = _db.GetConnection())
            {
                conn2.Open();
                fingerprinted = Convert.ToInt32(new SqliteCommand(
                    "SELECT COUNT(*) FROM video_files WHERE IFNULL(fingerprint, '') <> ''", conn2).ExecuteScalar() ?? 0);
                filesWithContent = Convert.ToInt32(new SqliteCommand(
                    @"SELECT COUNT(*) FROM video_files WHERE IFNULL(file_size, 0) > 0 AND file_path <> ''", conn2).ExecuteScalar() ?? 0);
            }

            return Ok(new
            {
                success = true,
                data = new
                {
                    groups,
                    groupCount = groups.Count,
                    fingerprinted,
                    filesWithContent
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "查询疑似重复失败");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>
    /// 检查并重命名文件名，使之与库里记的番号一致。
    /// 只动"文件确实在盘上、且文件名与番号不符"的行；目标名已被占用的一律跳过并说明原因。
    ///
    /// **默认是预检（dryRun=true）**：这个动作会改盘上真实文件名，一次可能涉及很多个，
    /// 所以先看清单。界面点「执行改名」时才带 ?dryRun=false。
    /// </summary>
    [Utils.AdminToken]
    [HttpPost("rename-to-code")]
    public IActionResult RenameFilesToCode([FromQuery] bool dryRun = true)
    {
        try
        {
            using var conn = _db.GetConnection();
            conn.Open();

            var results = new List<object>();
            var renamed = 0;
            var skipped = 0;
            var failed = 0;

            // 1) 版本行：按行级番号对齐
            var fileIds = new List<string>();
            using (var cmd = new SqliteCommand(@"
                SELECT f.id FROM video_files f
                WHERE f.code <> '' AND IFNULL(f.file_path, '') <> ''
                  AND f.file_path NOT LIKE '%' || f.code || '%'", conn))
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read()) fileIds.Add(reader.GetString(0));
            }
            foreach (var fileId in fileIds)
            {
                var (ok, newPath, error) = VideoFiles.AlignFileName(conn, fileId, dryRun);
                var row = VideoFiles.Find(conn, fileId);
                if (ok)
                {
                    renamed++;
                    results.Add(new { kind = "file", fileId, code = row?["code"], renamed = true, newFile = newPath });
                }
                else if (error is not null)
                {
                    failed++;
                    results.Add(new { kind = "file", fileId, code = row?["code"], renamed = false, error });
                }
                else skipped++;
            }

            // 2) 封面：影片番号就是封面文件名
            var covers = new List<(string VideoId, string Code, string CoverPath)>();
            using (var cmd = new SqliteCommand(@"
                SELECT id, code, cover_path FROM videos
                WHERE code IS NOT NULL AND code <> ''
                  AND IFNULL(cover_path, '') <> '' AND cover_path NOT LIKE '%' || code || '%'", conn))
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                    covers.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
            foreach (var (videoId, code, coverPath) in covers)
            {
                if (!System.IO.File.Exists(coverPath)) { skipped++; continue; }
                var dir = Path.GetDirectoryName(coverPath)!;
                var ext = Path.GetExtension(coverPath);
                if (Path.GetFileNameWithoutExtension(coverPath) == code) { skipped++; continue; }

                var target = Path.Combine(dir, code + ext);
                if (target != coverPath && System.IO.File.Exists(target))
                {
                    failed++;
                    results.Add(new { kind = "cover", videoId, code, renamed = false, error = $"目标名已被占用：{code}{ext}" });
                    continue;
                }
                if (dryRun)
                {
                    renamed++;
                    results.Add(new { kind = "cover", videoId, code, renamed = false, wouldRename = true, newCover = target });
                    continue;
                }
                try
                {
                    System.IO.File.Move(coverPath, target);
                }
                catch (Exception ex)
                {
                    failed++;
                    results.Add(new { kind = "cover", videoId, code, renamed = false, error = "重命名封面失败: " + ex.Message });
                    continue;
                }
                using var upd = new SqliteCommand("UPDATE videos SET cover_path = @cp WHERE id = @id", conn);
                upd.Parameters.Add(new SqliteParameter("@cp", target));
                upd.Parameters.Add(new SqliteParameter("@id", videoId));
                upd.ExecuteNonQuery();
                renamed++;
                results.Add(new { kind = "cover", videoId, code, renamed = true, newCover = target });
            }

            _logger.LogInformation("文件名对齐{Mode}：{Renamed} 处，跳过 {Skipped}，失败 {Failed}",
                dryRun ? "预检" : "执行", renamed, skipped, failed);
            return Ok(new { success = true, dryRun, message = dryRun ? "这是预检，还没动任何文件" : "已按清单改名",
                data = new { renamed, skipped, failed, details = results } });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RenameFilesToCode failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }
}

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
    /// 体积优化分析：把「编码 + 时长 + 大小」三样凑在一起，算出哪些文件还值得重编码。
    ///
    /// 为什么要三样：只按体积排会挑错对象——同样 6 GB，一小时的片码率高、还有压缩空间，
    /// 两小时的片多半已经压到骨头了。所以排序用的是"按目标码率折算能省下多少"，不是体积本身。
    ///
    /// 三条口径要说清：
    ///   · 码率是**整片平均**（文件大小 × 8 ÷ 时长），含音轨开销（一般 0.1~0.3 Mbps），
    ///     所以目标码率往下压到 2 Mbps 以下时，省的量会开始虚高；
    ///   · 只看**扫到过时长**的行（时长来自扫描读 mvhd），没扫到的不算候选、但单独报条数——
    ///     不然空列表会被读成"没有可优化的"；
    ///   · 默认只看"作为影片口径的那一版"（is_default=1），因为重复的解说版/其他版一起算
    ///     会把同一个影片的体积数翻几倍；要按每份文件看就把 scope 切成 all。
    ///
    /// 这里**只出证据，不碰任何文件**：重编码用什么工具、参数怎么调都是人的事。
    /// </summary>
    [HttpGet("optimize")]
    public IActionResult Optimize([FromQuery] double targetMbps = 2.5, [FromQuery] double minSizeGb = 1,
        [FromQuery] string scope = "default", [FromQuery] string? codec = null,
        [FromQuery] bool skipLiked = false, [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        try
        {
            // 参数一律夹到合理区间：这些值会进 SQL 的乘算，不夹的话一个负数就能让排序反过来
            var mbps = Math.Clamp(targetMbps <= 0 ? 2.5 : targetMbps, 0.25, 50);
            var minBytes = (long)(Math.Clamp(minSizeGb, 0, 200) * 1_000_000_000);
            var bps = mbps * 1_000_000;
            page = Utils.Paging.ClampPage(page);
            pageSize = Utils.Paging.ClampSize(pageSize);

            var where = "WHERE IFNULL(f.file_size, 0) >= @min AND IFNULL(f.duration, 0) > 0";
            if (scope != "all") where += " AND f.is_default = 1";
            // 编码档位走白名单拼好的条件（表别名是 f），前端传来的字符串只当字典键用
            if (!string.IsNullOrWhiteSpace(codec)
                && Utils.SourceStates.CodecClause(codec.Trim(), "f") is { } clause)
                where += $" AND ({clause})";
            if (skipLiked) where += " AND NOT EXISTS (SELECT 1 FROM video_likes l WHERE l.video_id = f.video_id)";

            const string sqlBody = @"
                SELECT f.id, f.video_id, IFNULL(f.file_size, 0), IFNULL(f.duration, 0), IFNULL(f.codec, ''),
                       IFNULL(f.res_w, 0), IFNULL(f.res_h, 0), IFNULL(f.file_path, ''), f.code,
                       IFNULL(v.code, ''), IFNULL(v.name, ''),
                       (SELECT COUNT(*) FROM video_actors va WHERE va.video_id = f.video_id) AS actor_count,
                       EXISTS(SELECT 1 FROM video_likes l2 WHERE l2.video_id = f.video_id) AS liked
                FROM video_files f
                JOIN videos v ON v.id = f.video_id
                ";
            var sql = sqlBody + where;

            var rows = new List<OptRow>();
            using (var conn = _db.GetConnection())
            {
                conn.Open();
                using var cmd = new SqliteCommand(sql, conn);
                cmd.Parameters.AddWithValue("@min", minBytes);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add(new OptRow(
                        reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt32(3),
                        reader.GetString(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetString(7),
                        reader.GetString(8), reader.GetString(9), reader.GetString(10),
                        reader.GetInt32(11), reader.GetInt64(12) == 1));
                }
            }

            // 覆盖率：有文件但还没扫出时长的行不会出现在候选里，界面必须把这条报出来，
            // 否则"0 个候选"会被读成"库里没有可压的片"
            int unscored;
            long totalAll;
            using (var conn2 = _db.GetConnection())
            {
                conn2.Open();
                using var cover = new SqliteCommand(
                    @"SELECT COUNT(*), IFNULL(SUM(IFNULL(file_size, 0)), 0) FROM video_files
                      WHERE IFNULL(file_size, 0) > 0 AND file_path <> ''
                        AND IFNULL(duration, 0) <= 0 AND (@all = 1 OR is_default = 1)", conn2);
                cover.Parameters.AddWithValue("@all", scope == "all" ? 1 : 0);
                using var r2 = cover.ExecuteReader();
                r2.Read();
                unscored = r2.GetInt32(0);
                // SUM 在空集上是 NULL 而不是 0（SQLite 的聚合语义），不兜住的话
                // "全库都扫到时长了"这种好情况反而会把整个分析接口打炸
                totalAll = r2.GetInt64(1);
            }

            var scored = rows.Select(r =>
            {
                double rate = r.Size * 8.0 / r.Duration;                       // bit/s
                double save = Math.Max(0, r.Size - r.Duration * bps / 8.0);    // 按目标码率折算能省多少字节
                return new { r, rate, save };
            }).ToList();

            var candidates = scored.Where(x => x.save > 0).OrderByDescending(x => x.save).ToList();
            var summary = new
            {
                files = rows.Count,
                totalSize = rows.Sum(x => x.Size),
                saveable = candidates.Sum(x => (long)x.save),
                candidateCount = candidates.Count,
                // 按编码档分组：这一档有多少部、多大、平均码率多少
                byCodec = scored.GroupBy(x => Bucket(x.r.Codec)).Select(g => new
                {
                    bucket = g.Key,
                    count = g.Count(),
                    size = g.Sum(x => x.r.Size),
                    avgMbps = g.Average(x => x.rate) / 1_000_000,
                    saveable = (long)g.Sum(x => x.save)
                }).OrderByDescending(x => x.size).ToList(),
                byBand = scored.Select(x => Band(x.rate / 1_000_000)).Distinct().OrderBy(x => x).Select(b => new
                {
                    band = b,
                    count = scored.Count(x => Band(x.rate / 1_000_000) == b),
                    size = scored.Where(x => Band(x.rate / 1_000_000) == b).Sum(x => x.r.Size)
                }).ToList(),
                unscored,
                unscoredSize = totalAll
            };

            var items = candidates.Skip((page - 1) * pageSize).Take(pageSize).Select(x => new
            {
                fileId = x.r.FileId,
                videoId = x.r.VideoId,
                videoCode = x.r.VideoCode,
                videoName = x.r.VideoName,
                fileCode = x.r.FileCode,
                path = x.r.Path,
                size = x.r.Size,
                duration = x.r.Duration,
                mbps = x.rate / 1_000_000,
                codec = x.r.Codec,
                resW = x.r.ResW,
                resH = x.r.ResH,
                saveable = (long)x.save,
                actorCount = x.r.ActorCount,
                liked = x.r.Liked
            }).ToList();

            return Ok(new
            {
                success = true,
                data = new
                {
                    summary,
                    items,
                    total = candidates.Count,
                    page,
                    pageSize,
                    targetMbps = mbps,
                    minSizeGb,
                    scope = scope == "all" ? "all" : "default",
                    codec = codec ?? "",
                    skipLiked
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "体积优化分析失败");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    // 一行候选。FileCode 是行级番号（这一版自己的文件名标识），与影片番号可以不同；
    // Path 给"复制清单"用，界面不显示全路径。
    private sealed record OptRow(string FileId, string VideoId, long Size, int Duration, string Codec,
        int ResW, int ResH, string Path, string FileCode, string VideoCode, string VideoName,
        int ActorCount, bool Liked);

    /// <summary>fourcc → 档位标签。认不出的原样带出，别悄悄并进"其他"里让人看不出还有别的东西</summary>
    private static string Bucket(string fourcc)
    {
        var raw = (fourcc ?? "").Trim().ToLowerInvariant();
        return raw switch
        {
            "avc1" or "avc3" => "H.264",
            "hev1" or "hvc1" or "hev2" or "hvc2" => "H.265 / HEVC",
            "av01" => "AV1",
            "vp09" => "VP9",
            "mp4v" or "xvid" or "dx50" or "divx" => "MPEG-4 系",
            "" => "未扫描",
            _ => raw
        };
    }

    /// <summary>码率分段标签，与界面那张分布表一一对应（数字前缀是给排序用的，不显示出来）</summary>
    private static string Band(double mbps) => mbps switch
    {
        < 1 => "a<1",
        < 1.5 => "b1-1.5",
        < 2.5 => "c1.5-2.5",
        < 4 => "d2.5-4",
        < 8 => "e4-8",
        _ => "f>8"
    };

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

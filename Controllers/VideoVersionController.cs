using ckapi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using System.Text.Json.Serialization;

namespace ckapi.Controllers;

/// <summary>
/// 版本（文件层）的操作：增删改、设为默认、两维标记、扫描、找回文件、删文件。
///
/// v11 起"一部片"与"这一部片的某一份文件"分开了：影片层管片名/番号/封面/归属，
/// 文件层（video_files）管路径/大小/分辨率/字幕/水印/扫描时间，每部片恰好一条 is_default=1。
/// 所以这一批接口的入参一律是**版本行 id**，不再是影片 id —— 详情页下拉切到哪一版，
/// 按钮就作用在哪一版上。影片级操作（编辑影片、封面、番号级联改名）仍在 VideoController。
/// </summary>
[ApiController]
[Route("api/video/version")]
public class VideoVersionController : ControllerBase
{
    private readonly ILogger<VideoVersionController> _logger;
    private readonly Utils.SQLiteHelper _db;
    private readonly SourceScanner _scanner;

    public VideoVersionController(
        ILogger<VideoVersionController> logger, Utils.SQLiteHelper db, SourceScanner scanner)
    {
        _logger = logger;
        _db = db;
        _scanner = scanner;
    }

    /// <summary>某一版的全部字段（详情页切换后要按版本显示分辨率与两维）</summary>
    [HttpGet("{fileId}")]
    public IActionResult GetOne(string fileId)
    {
        using var conn = _db.GetConnection();
        conn.Open();
        var row = VideoFiles.Find(conn, fileId);
        return row is null
            ? NotFound(new { success = false, message = "版本不存在" })
            : Ok(new { success = true, data = row });
    }

    /// <summary>
    /// 给一部片加一个版本（解说、剪辑…）。行级番号默认按"影片番号 + 类型后缀"建议，
    /// 界面把它做成可改的输入框：同一个类型下可能有好几个频道，让他自己确认尾巴。
    /// </summary>
    [HttpPost]
    public IActionResult Add([FromBody] AddVersionRequest req)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(req.VideoId))
                return Ok(new { success = false, message = "videoId 不能为空" });

            using var conn = _db.GetConnection();
            conn.Open();

            var movie = ReadOne(conn, "SELECT id, code FROM videos WHERE id = @id", req.VideoId);
            if (movie is null) return NotFound(new { success = false, message = "影片不存在" });

            string typeId;
            try
            {
                typeId = VideoFiles.ResolveType(conn, req.TypeId);
            }
            catch (VideoFiles.MetaException ex)
            {
                return Ok(new { success = false, message = ex.Message });
            }
            if (typeId.Length == 0)
                return Ok(new { success = false, message = "请选择版本类型；原版那一行建片时就有了，不用再补" });

            var movieCode = movie.GetValueOrDefault("code")?.ToString()?.Trim() ?? "";
            var label = req.Label?.Trim() ?? "";
            var code = req.Code?.Trim() ?? "";
            if (code.Length == 0)
            {
                var suffix = ReadOne(conn, "SELECT suffix FROM version_types WHERE id = @id", typeId)
                    ?.GetValueOrDefault("suffix")?.ToString() ?? "";
                code = movieCode + suffix;
            }

            if (code.Length == 0)
                return Ok(new { success = false, message = "这一版没有可用的文件名标识：影片还没填番号，版本类型也没配后缀" });

            // 一部片同一个类型只能有一条（库里由 idx_video_files_movie_type 兜，这里先拦下来说人话）
            var taken = VideoFiles.TypeTakenBy(conn, req.VideoId!, typeId);
            if (taken is not null)
                return Ok(new { success = false, message = $"这部片已经有「{taken}」这一版了，同一个类型不能再加第二条" });

            // 行级番号是"文件名该长什么样"的权威值，撞车了后面改名会互相覆盖，这里就拦住。
            // 自动拼出来的值最常撞：那几家解说频道的后缀都填了 C，同一部片加第二个解说版就会撞车，
            // 所以消息里要说清是哪种情况、以及两条出路（自己填一个尾巴，或给这个类型配别的后缀）
            string? clashOwner = null;
            using (var check = new SqliteCommand(@"
                SELECT IFNULL(vt.name, '原版') FROM video_files f
                LEFT JOIN version_types vt ON vt.id = f.type_id WHERE f.code = @c LIMIT 1", conn))
            {
                check.Parameters.Add(new SqliteParameter("@c", code));
                clashOwner = check.ExecuteScalar()?.ToString();
            }
            if (clashOwner is not null)
            {
                var auto = string.IsNullOrEmpty(req.Code);
                return Ok(new { success = false, message = auto
                    ? $"按「{movieCode}」加类型后缀自动得到「{code}」，那已经是{clashOwner}那一版的名字了。" +
                      "给这一版另填一个文件名标识，或者到设置「版本类型」里给这个类型配一个没被用过的后缀。"
                    : $"文件名标识「{code}」已经被{clashOwner}那一版占着" });
            }

            var filePath = req.FilePath?.Trim() ?? "";
            long size = filePath.Length > 0 && System.IO.File.Exists(filePath) ? new FileInfo(filePath).Length : 0;
            var id = VideoFiles.NewId();
            using (var cmd = new SqliteCommand(@"
                INSERT INTO video_files (id, video_id, code, type_id, label, file_path, file_size,
                                         subtitle_state, watermark_state, is_default, ctime)
                VALUES (@id, @videoId, @code, @type, @label, @path, @size, 'unknown', 'unknown', 0, @ctime)", conn))
            {
                cmd.Parameters.Add(P("@id", id));
                cmd.Parameters.Add(P("@videoId", req.VideoId));
                cmd.Parameters.Add(P("@code", code));
                cmd.Parameters.Add(P("@type", typeId));
                cmd.Parameters.Add(P("@label", label));
                cmd.Parameters.Add(P("@path", filePath));
                cmd.Parameters.Add(P("@size", size));
                cmd.Parameters.Add(P("@ctime", VideoFiles.Now()));
                cmd.ExecuteNonQuery();
            }

            _logger.LogInformation("影片 {Code} 新增版本 {VersionCode}（{Count} 字节）", movieCode, code, size);
            return Ok(new { success = true, data = VideoFiles.Find(conn, id), message = "版本已添加" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Add version failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>
    /// 改这一版：版本名称、文件名标识、文件路径与大小。
    /// 都按"传了才动"处理，与影片编辑里原名/发行日期同一口径。
    /// </summary>
    [HttpPut("{fileId}")]
    public IActionResult Update(string fileId, [FromBody] UpdateVersionRequest req)
    {
        try
        {
            using var conn = _db.GetConnection();
            conn.Open();
            var row = VideoFiles.Find(conn, fileId);
            if (row is null) return NotFound(new { success = false, message = "版本不存在" });

            if (req.TypeId is not null)
            {
                string typeId;
                try
                {
                    typeId = VideoFiles.ResolveType(conn, req.TypeId);
                }
                catch (VideoFiles.MetaException ex)
                {
                    return Ok(new { success = false, message = ex.Message });
                }
                var videoId = row["videoId"]?.ToString() ?? "";
                // 换到别的类型也要过同一道闸：这部片已有那一类就拦下来说人话
                var taken = VideoFiles.TypeTakenBy(conn, videoId, typeId, fileId);
                if (taken is not null)
                    return Ok(new { success = false, message = $"这部片已经有「{taken}」这一版了，同一个类型只能有一条" });
                NonQuery(conn, "UPDATE video_files SET type_id = @t WHERE id = @id",
                    P("@t", typeId), P("@id", fileId));
            }

            if (req.Label is not null)
                NonQuery(conn, "UPDATE video_files SET label = @l WHERE id = @id",
                    P("@l", req.Label.Trim()), P("@id", fileId));

            if (req.Code is not null)
            {
                var code = req.Code.Trim();
                if (code.Length > 0)
                {
                    var clash = Scalar(conn,
                        "SELECT id FROM video_files WHERE code = @c AND id <> @id LIMIT 1", P("@c", code), P("@id", fileId));
                    if (clash is not null)
                        return Ok(new { success = false, message = $"文件名标识「{code}」已经被别的版本占着" });
                }
                NonQuery(conn, "UPDATE video_files SET code = @c WHERE id = @id",
                    P("@c", code), P("@id", fileId));
            }

            if (req.FilePath is not null)
            {
                var path = req.FilePath.Trim();
                long size = path.Length > 0 && System.IO.File.Exists(path) ? new FileInfo(path).Length : 0;
                NonQuery(conn, "UPDATE video_files SET file_path = @p, file_size = @s WHERE id = @id",
                    P("@p", path), P("@s", size), P("@id", fileId));
            }

            return Ok(new { success = true, data = VideoFiles.Find(conn, fileId), message = "版本已更新" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update version failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>
    /// 设为影片口径：列表筛选、统计、卡片字段从此走这一版。
    /// 界面切之前会二次确认，这里只负责把同一部片的旧默认行清掉再设新的（一个事务，不留空窗）。
    /// </summary>
    [HttpPost("{fileId}/default")]
    public IActionResult SetDefault(string fileId)
    {
        try
        {
            using var conn = _db.GetConnection();
            conn.Open();
            VideoFiles.SetDefault(conn, fileId);
            var row = VideoFiles.Find(conn, fileId);
            _logger.LogInformation("版本 {Code} 已设为影片口径", row?.GetValueOrDefault("code"));
            return Ok(new { success = true, data = row, message = "已设为默认版本，列表与统计跟着这一版走" });
        }
        catch (VideoFiles.MetaException ex)
        {
            return NotFound(new { success = false, message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SetDefault failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>
    /// 删掉一版。只剩一版时不许删——那部片就没有文件层了，列表筛选与统计会当场失去依据，
    /// 要真不想要这部片，去删影片。deleteFile=true 顺手把盘上那份也删掉。
    /// </summary>
    [HttpDelete("{fileId}")]
    public IActionResult Delete(string fileId, [FromQuery] bool deleteFile = false)
    {
        try
        {
            using var conn = _db.GetConnection();
            conn.Open();
            var row = VideoFiles.Find(conn, fileId);
            if (row is null) return NotFound(new { success = false, message = "版本不存在" });

            var videoId = row["videoId"]?.ToString() ?? "";
            var siblings = Convert.ToInt32(Scalar(conn,
                "SELECT COUNT(*) FROM video_files WHERE video_id = @v", P("@v", videoId)) ?? 0);
            if (siblings <= 1)
                return Ok(new { success = false, message = "这部片只剩这一版了，要删就连影片一起删" });

            var path = row["filePath"]?.ToString() ?? "";
            var wasDefault = row["isDefault"] is true;
            var filePath = path;

            NonQuery(conn, "DELETE FROM video_files WHERE id = @id", P("@id", fileId));
            // 删掉的是默认版本时把影片口径交给原版行（原版一定存在，除非这部片本来就是靠这版当默认）
            if (wasDefault)
            {
                var next = Scalar(conn, @"
                    SELECT id FROM video_files WHERE video_id = @v
                    ORDER BY (type_id = ''), code LIMIT 1", P("@v", videoId))?.ToString();
                if (next is not null)
                    NonQuery(conn, "UPDATE video_files SET is_default = 1 WHERE id = @id", P("@id", next));
            }
            // 指向这一版的点赞记录跟着抹掉：留着会变成"赞过一个已经不存在的版本"
            NonQuery(conn, "DELETE FROM video_likes WHERE file_id = @id", P("@id", fileId));

            var fileDeleted = false;
            if (deleteFile && filePath.Length > 0 && System.IO.File.Exists(filePath))
            {
                try
                {
                    System.IO.File.Delete(filePath);
                    fileDeleted = true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "删除版本文件失败: {Path}", filePath);
                }
            }

            _logger.LogInformation("已删除版本 {Code}（{Path}）", row["code"], filePath);
            return Ok(new
            {
                success = true,
                message = fileDeleted ? "版本与文件已删除" : "版本已删除，文件保留在盘上",
                data = new { fileDeleted }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Delete version failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>
    /// 标记这一版的字幕 / 广告水印。两维各自独立，传哪一维改哪一维；改回 unknown 就是清掉结论。
    /// </summary>
    [HttpPut("{fileId}/source")]
    public IActionResult UpdateSource(string fileId, [FromBody] UpdateSourceRequest req)
    {
        try
        {
            if (req.Subtitle is not null && !Utils.SourceStates.IsSubtitle(req.Subtitle))
                return Ok(new { success = false, message = $"subtitle 取值不对：{req.Subtitle}" });
            if (req.Watermark is not null && !Utils.SourceStates.IsWatermark(req.Watermark))
                return Ok(new { success = false, message = $"watermark 取值不对：{req.Watermark}" });
            if (req.Subtitle is null && req.Watermark is null)
                return Ok(new { success = false, message = "没有要改的维度" });

            using var conn = _db.GetConnection();
            conn.Open();

            NonQuery(conn, @"
                UPDATE video_files
                SET subtitle_state = COALESCE(@subtitle, subtitle_state),
                    watermark_state = COALESCE(@watermark, watermark_state)
                WHERE id = @id",
                P("@subtitle", (object?)req.Subtitle ?? DBNull.Value),
                P("@watermark", (object?)req.Watermark ?? DBNull.Value),
                P("@id", fileId));
            var row = VideoFiles.Find(conn, fileId);
            if (row is null) return NotFound(new { success = false, message = "版本不存在" });

            return Ok(new
            {
                success = true,
                message = "片源标记已更新",
                data = new { subtitleState = row["subtitleState"], watermarkState = row["watermarkState"] }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UpdateSource failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>重扫这一版的分辨率：只读容器头，字幕与水印两维不参与</summary>
    [HttpPost("{fileId}/scan")]
    public IActionResult ScanOne(string fileId)
    {
        try
        {
            using var conn = _db.GetConnection();
            conn.Open();

            var ins = _scanner.ScanOne(conn, fileId);
            if (!ins.Ok) return Ok(new { success = false, message = ins.Error ?? "扫描失败" });

            return Ok(new
            {
                success = true,
                message = $"{ins.Width}×{ins.Height}",
                data = new { resW = ins.Width, resH = ins.Height, codec = ins.Codec }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ScanOne failed for {FileId}", fileId);
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>
    /// 重算这一版的文件大小；路径没了就按行级番号的 {code}.mp4 在"视频"目录里找回。
    /// 顺带把这部片的封面找回来（封面在影片层，一部一张，按影片番号 {code}.jpg 配）。
    /// 换文件了就把分辨率与扫描时间清空、两维回到未标——那是上一份文件的结论，不该跟着新文件走。
    /// </summary>
    [HttpPost("{fileId}/reset-file-size")]
    public IActionResult ResetFileSize(string fileId)
    {
        try
        {
            using var conn = _db.GetConnection();
            conn.Open();

            var row = VideoFiles.Find(conn, fileId);
            if (row is null) return NotFound(new { success = false, message = "版本不存在" });
            var videoId = row["videoId"]?.ToString() ?? "";
            var code = row["code"]?.ToString() ?? "";
            var currentPath = row["filePath"]?.ToString() ?? "";
            var messages = new List<string>();

            var newPath = currentPath;
            long newSize = 0;

            if (!string.IsNullOrEmpty(currentPath) && System.IO.File.Exists(currentPath))
            {
                newSize = new FileInfo(currentPath).Length;
                messages.Add($"文件大小: {FormatSize(newSize)}");
            }
            else if (!string.IsNullOrEmpty(code))
            {
                foreach (var dir in DirsByCategory(conn, "视频"))
                {
                    if (!Directory.Exists(dir)) continue;
                    var candidate = Path.Combine(dir, $"{code}.mp4");
                    if (!System.IO.File.Exists(candidate)) continue;
                    newPath = candidate;
                    newSize = new FileInfo(candidate).Length;
                    messages.Add($"在目录 [{dir}] 中找到匹配文件");
                    break;
                }
                if (newPath == currentPath)
                    messages.Add(DirsByCategory(conn, "视频").Count > 0 ? "在所有配置的视频目录中未找到匹配文件" : "未配置视频目录");
            }
            else
            {
                messages.Add("这一版没有文件名标识，无法搜索");
            }

            // 封面在影片层：只有它空着时才按影片番号去找一张回来
            var coverPath = Scalar(conn, "SELECT cover_path FROM videos WHERE id = @id", P("@id", videoId))?.ToString() ?? "";
            if (string.IsNullOrEmpty(coverPath))
            {
                var movieCode = Scalar(conn, "SELECT code FROM videos WHERE id = @id", P("@id", videoId))?.ToString() ?? "";
                if (!string.IsNullOrEmpty(movieCode))
                {
                    foreach (var dir in DirsByCategory(conn, "封面"))
                    {
                        if (!Directory.Exists(dir)) continue;
                        var candidate = Path.Combine(dir, $"{movieCode}.jpg");
                        if (!System.IO.File.Exists(candidate)) continue;
                        NonQuery(conn, "UPDATE videos SET cover_path = @c WHERE id = @id",
                            P("@c", candidate), P("@id", videoId));
                        messages.Add("封面已找回");
                        break;
                    }
                }
            }

            // 重置就是"回到没量过、没标过的状态"：不论大小有没有变都要写。
            // （这里原先挂在了 sizeChanged || pathChanged 里，于是文件没换的情况下点它什么也没发生——
            //  而"文件没变、只想把两维结论清掉重来"正是这颗按钮最常见的用法。）
            NonQuery(conn, @"
                UPDATE video_files SET file_path = @p, file_size = @s, res_w = NULL, res_h = NULL,
                                       scan_time = NULL, subtitle_state = 'unknown', watermark_state = 'unknown'
                WHERE id = @id",
                P("@p", newPath), P("@s", newSize), P("@id", fileId));

            // 大小确实变了才挪影片的入库时间（与 v10 一致：它决定"最新入库"的排序，
            // 补扫出来的真实大小该让这部片回到前面；没变就不该把一部老片顶到列表最前）
            var wasDefault = row["isDefault"] is true;
            if (wasDefault && newSize != Convert.ToInt64(row["fileSize"] ?? 0L))
            {
                NonQuery(conn, "UPDATE videos SET ctime = @t WHERE id = @id",
                    P("@t", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")), P("@id", videoId));
                messages.Add("入库时间已更新");
            }

            return Ok(new
            {
                success = true,
                data = VideoFiles.Find(conn, fileId),
                message = string.Join("；", messages)
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ResetFileSize failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>删掉这一版的物理文件，库里把路径与大小清空（版本行留着，条目还在）</summary>
    [HttpDelete("{fileId}/file")]
    public IActionResult DeleteFile(string fileId)
    {
        try
        {
            using var conn = _db.GetConnection();
            conn.Open();
            var row = VideoFiles.Find(conn, fileId);
            if (row is null) return NotFound(new { success = false, message = "版本不存在" });

            var path = row["filePath"]?.ToString() ?? "";
            string message;
            if (!string.IsNullOrEmpty(path) && path.StartsWith("/"))
            {
                if (System.IO.File.Exists(path))
                {
                    try
                    {
                        System.IO.File.Delete(path);
                        message = "文件已删除";
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "删除文件失败: {Path}", path);
                        return Ok(new { success = false, message = "文件删除失败（可能被其他程序占用）" });
                    }
                }
                else message = "文件不存在，无需删除";
            }
            else message = "无有效文件路径";

            NonQuery(conn, "UPDATE video_files SET file_path = '', file_size = 0 WHERE id = @id", P("@id", fileId));
            return Ok(new { success = true, message, data = VideoFiles.Find(conn, fileId) });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DeleteFile failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>
    /// 上传落盘后写回这一版的路径与大小。封面仍写在影片层（一部一张）。
    /// </summary>
    [HttpPut("{fileId}/file-info")]
    public IActionResult UpdateFileInfo(string fileId, [FromBody] UpdateFileInfoRequest req)
    {
        try
        {
            using var conn = _db.GetConnection();
            conn.Open();
            if (VideoFiles.Find(conn, fileId) is null)
                return NotFound(new { success = false, message = "版本不存在" });

            long? size = null;
            if (!string.IsNullOrEmpty(req.FilePath) && System.IO.File.Exists(req.FilePath))
                size = new FileInfo(req.FilePath).Length;

            NonQuery(conn, @"
                UPDATE video_files
                SET file_path = @fp, file_size = COALESCE(@fs, file_size)
                WHERE id = @id",
                P("@fp", req.FilePath ?? ""), P("@fs", (object?)size ?? DBNull.Value), P("@id", fileId));

            if (!string.IsNullOrEmpty(req.CoverPath))
            {
                var videoId = Scalar(conn, "SELECT video_id FROM video_files WHERE id = @id", P("@id", fileId))?.ToString();
                if (videoId is not null)
                    NonQuery(conn, "UPDATE videos SET cover_path = @c WHERE id = @id",
                        P("@c", req.CoverPath), P("@id", videoId));
            }

            return Ok(new { success = true, data = VideoFiles.Find(conn, fileId), message = "文件信息已更新" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UpdateFileInfo failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>
    /// 把这一版的盘上文件名对齐到它的行级番号。
    ///
    /// **默认预检**（dryRun=true）：只回"会改成什么名"，一个字节都不动盘。
    /// 这是刻意的设计——改了库里的文件名标识之后，盘上那个名字不会跟着变，
    /// 要变必须在这儿显式 dryRun=false 走一次，避免"填错一格就把 20 GB 的文件改名"。
    /// 库里的 file_path 只在改名成功后才更新（见 VideoFiles.AlignFileName）。
    /// </summary>
    [HttpPost("{fileId}/align-name")]
    public IActionResult AlignName(string fileId, [FromQuery] bool dryRun = true)
    {
        try
        {
            using var conn = _db.GetConnection();
            conn.Open();
            var before = VideoFiles.Find(conn, fileId);
            if (before is null) return NotFound(new { success = false, message = "版本不存在" });

            var (ok, newPath, error) = VideoFiles.AlignFileName(conn, fileId, dryRun);
            if (error is not null)
                return Ok(new { success = false, dryRun, message = error, data = before });

            if (!ok)
                return Ok(new { success = true, dryRun, changed = false, message = "文件名已经与标识一致，不用动", data = before });

            var to = Path.GetFileNameWithoutExtension(newPath);
            return Ok(new
            {
                success = true,
                dryRun,
                changed = true,
                message = dryRun
                    ? $"预检：会把「{System.IO.Path.GetFileName(before["filePath"]?.ToString() ?? "")}」改成「{to}{Path.GetExtension(newPath)}」"
                    : $"已改成「{to}{Path.GetExtension(newPath)}」",
                data = VideoFiles.Find(conn, fileId),
                oldFile = before["filePath"],
                newFile = newPath
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AlignName failed");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>
    /// 版本类型词表（设置里那个分区）+ 每类下的版本行数，供详情页下拉与新增版本对话框用。
    /// </summary>
    [HttpGet("types")]
    public IActionResult Types()
    {
        using var conn = _db.GetConnection();
        conn.Open();
        return Ok(new { success = true, data = VideoFiles.Types(conn) });
    }

    // ---------------------------------------------------------------- 小工具

    private List<string> DirsByCategory(SqliteConnection conn, string category)
    {
        var dirs = new List<string>();
        using var cmd = new SqliteCommand(
            "SELECT path FROM scan_directories WHERE category = @cat ORDER BY path ASC", conn);
        cmd.Parameters.Add(new SqliteParameter("@cat", category));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var path = reader.GetString(0);
            if (!string.IsNullOrWhiteSpace(path)) dirs.Add(path);
        }
        return dirs;
    }

    private static string FormatSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        int order = 0; double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1) { order++; size /= 1024; }
        return Math.Round(size, 2) + " " + sizes[order];
    }

    private static SqliteParameter P(string name, object value) => new(name, value);

    private static object? Scalar(SqliteConnection conn, string sql, params SqliteParameter[] ps)
    {
        using var cmd = new SqliteCommand(sql, conn);
        foreach (var p in ps) cmd.Parameters.Add(p);
        return cmd.ExecuteScalar();
    }

    private static int NonQuery(SqliteConnection conn, string sql, params SqliteParameter[] ps)
    {
        using var cmd = new SqliteCommand(sql, conn);
        foreach (var p in ps) cmd.Parameters.Add(p);
        return cmd.ExecuteNonQuery();
    }

    private static Dictionary<string, object?>? ReadOne(SqliteConnection conn, string sql, string id)
    {
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.Add(new SqliteParameter("@id", id));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        var d = new Dictionary<string, object?>();
        for (var i = 0; i < reader.FieldCount; i++) d[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        return d;
    }

    public sealed class AddVersionRequest
    {
        [JsonPropertyName("videoId")] public string? VideoId { get; set; }
        [JsonPropertyName("typeId")] public string? TypeId { get; set; }
        [JsonPropertyName("label")] public string? Label { get; set; }
        /// <summary>文件名标识；留空按"影片番号 + 类型后缀"生成</summary>
        [JsonPropertyName("code")] public string? Code { get; set; }
        [JsonPropertyName("filePath")] public string? FilePath { get; set; }
    }

    public sealed class UpdateVersionRequest
    {
        [JsonPropertyName("typeId")] public string? TypeId { get; set; }
        [JsonPropertyName("label")] public string? Label { get; set; }
        [JsonPropertyName("code")] public string? Code { get; set; }
        [JsonPropertyName("filePath")] public string? FilePath { get; set; }
    }

    public sealed class UpdateSourceRequest
    {
        /// <summary>字幕情况；null 表示这一维不动</summary>
        [JsonPropertyName("subtitle")] public string? Subtitle { get; set; }
        /// <summary>广告水印；null 表示这一维不动</summary>
        [JsonPropertyName("watermark")] public string? Watermark { get; set; }
    }

    public sealed class UpdateFileInfoRequest
    {
        [JsonPropertyName("filePath")] public string? FilePath { get; set; }
        [JsonPropertyName("coverPath")] public string? CoverPath { get; set; }
    }
}

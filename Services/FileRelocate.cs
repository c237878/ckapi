using ckapi.Utils;
using Microsoft.Data.Sqlite;

namespace ckapi.Services;

/// <summary>
/// 把选中的版本文件搬到另一个目录去（配合"拷到新设备上重编码"这条流程）。
///
/// 为什么做后台任务而不是在请求里跑完：一次可能挑几十部、每部几个 GB，跨卷复制是十分钟到小时级的活，
/// 举着 HTTP 连接跑必然超时，中途离开页面还得自己中止 —— 与扫描同一套理由。
///
/// 为什么一次只搬一个（不并发）：这是**不可逆的批量文件动作**， predictable 比快重要。
/// 真要压时间瓶颈在带宽，并发也不会更快，只会让"失败的是哪一个"更难说清。
///
/// 三条硬规矩：
///   · **绝不覆盖**：目标位置已经有同名文件就跳过并说明，不 asking。同名冲突跨片很常见
///     （不同片商同号、或者那版本来就重名），覆盖等于无声地毁掉一个文件。
///   · **搬成什么样写库才跟着改成什么样**：单文件"复制成功 + 目标存在 + 大小对得上"之后才 UPDATE，
///     顺序反过来会出现"库里指着新位置、盘上新位置什么都没有"。
///   · **预检和真跑共用同一份判定**（<see cref="Plan"/>），不然预检给看的清单和实际做的不是一回事。
///
/// 模式两种：move 是搬走（本地那一版就指向新位置，目标盘拔掉会变死链，由每日盘上核对报出来）；
/// copy 是只拷一份过去、库里仍然指着原文件（想先验货再决定时用这个）。
/// </summary>
public sealed class FileRelocateJob
{
    private readonly SQLiteHelper _db;
    private readonly ILogger<FileRelocateJob> _logger;

    private int _running;
    private CancellationTokenSource _cts = new();
    private volatile int _done;
    private volatile int _total;
    // volatile 不能修饰 long（CLR 不保证 64 位原子），进度里的字节数走 Interlocked
    private long _bytes;
    private volatile int _failed;
    private volatile bool _cancelled;
    private volatile string _current = "";
    private volatile string? _note;
    private DateTime? _startedAt;

    /// <summary>上一轮的结论（跑完才写，正在跑的那一轮看的是上面的计数）</summary>
    public sealed record RunResult(string FinishedAt, string Mode, string Dest, int Total, int Done,
        int Failed, long Bytes, bool Cancelled, string? Note);

    private volatile RunResult? _lastRun;
    public RunResult? LastRun => _lastRun;

    private readonly record struct Item(string FileId, string Path, long Size, string Dest);
    private List<Item> _queue = new();
    private string _dest = "";
    private bool _copy;

    public FileRelocateJob(SQLiteHelper db, ILogger<FileRelocateJob> _logger)
    {
        this._db = db;
        this._logger = _logger;
    }

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>
    /// 一份"要动哪些、动不动得了"的计划。dryRun 与 Start 都走这儿。
    /// 返回的 problems 是**整体层面**的毛病（目录不存在、一个都没选），
    /// 单个文件的毛病记在每一行的 reason 里，界面上要能分开显示。
    /// </summary>
    public static (List<Row> Rows, List<string> Problems) Plan(SQLiteHelper db, IReadOnlyList<string> fileIds,
        string? rawDest, bool copy)
    {
        var problems = new List<string>();
        var ids = (fileIds ?? Array.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
        if (ids.Count == 0) problems.Add("没有选中任何文件");

        var dest = (rawDest ?? "").Trim();
        if (dest.Length == 0) problems.Add("要先给一个目标目录");
        else if (!Path.IsPathRooted(dest)) problems.Add("目标必须是绝对路径");
        else if (!Directory.Exists(dest)) problems.Add($"目标目录不存在或不是目录：{dest}");

        var rows = new List<Row>();
        if (problems.Count > 0) return (rows, problems);

        var seenTargets = new HashSet<string>(StringComparer.Ordinal);
        using var conn = db.GetConnection();
        conn.Open();
        foreach (var id in ids)
        {
            using var cmd = new SqliteCommand(
                @"SELECT IFNULL(f.file_path, ''), IFNULL(f.file_size, 0), f.code, v.code
                  FROM video_files f JOIN videos v ON v.id = f.video_id WHERE f.id = @id", conn);
            cmd.Parameters.AddWithValue("@id", id);
            var src = "";
            long size = 0;
            string fileCode = "", videoCode = "";
            using (var r = cmd.ExecuteReader())
            {
                if (!r.Read())
                {
                    rows.Add(new Row(id, "", "", "", 0, false, "这一版不存在"));
                    continue;
                }
                src = r.GetString(0);
                size = r.GetInt64(1);
                fileCode = r.IsDBNull(2) ? "" : r.GetString(2);
                videoCode = r.IsDBNull(3) ? "" : r.GetString(3);
            }

            var target = "";
            bool ok = true;
            string reason = "";

            if (string.IsNullOrEmpty(src) || size <= 0)
            {
                ok = false; reason = "这一版还没有文件";
            }
            else if (!File.Exists(src))
            {
                ok = false; reason = "文件不在盘上（卷没挂或被搬走了）";
            }
            else
            {
                target = Path.Combine(dest, Path.GetFileName(src));
                var srcDir = Path.GetDirectoryName(src) ?? "";
                if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(src), StringComparison.Ordinal))
                {
                    ok = false; reason = "目标就是它现在待的地方，不用动";
                }
                else if (SafePath.IsInside(dest, srcDir))
                {
                    // 搬进自己所在目录的子目录，看起来无害，实际会让人分不清哪份是原件
                    ok = false; reason = "目标目录在源文件所在目录里面，换个地方";
                }
                else if (File.Exists(target))
                {
                    ok = false; reason = $"目标位置已有同名文件，不覆盖：{Path.GetFileName(target)}";
                }
                else if (!seenTargets.Add(target))
                {
                    ok = false; reason = "这批里有两个文件同名，搬过去会撞车，分开搬或先改名";
                }
            }

            rows.Add(new Row(id, videoCode, fileCode, src, size, ok, reason, target));
        }

        return (rows, problems);
    }

    /// <summary>一行计划。Ok=false 的行在执行时会被跳过而不是让整个批次停下来。</summary>
    public sealed record Row(string FileId, string VideoCode, string FileCode, string Path, long Size,
        bool Ok, string Reason, string Target = "");

    public (bool Started, string Message) Start(SQLiteHelper db, IReadOnlyList<string> fileIds,
        string? rawDest, bool copy)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return (false, "已经有一批在搬了，先等它收尾或手动停止");

        var (rows, problems) = Plan(db, fileIds, rawDest, copy);
        var go = rows.Where(r => r.Ok).ToList();
        if (problems.Count > 0 || go.Count == 0)
        {
            Interlocked.Exchange(ref _running, 0);
            return (false, problems.Count > 0 ? string.Join("；", problems) : "选中的都不能动（每一行都写了原因）");
        }

        _dest = rawDest!.Trim();
        _copy = copy;
        _queue = go.Select(r => new Item(r.FileId, r.Path, r.Size, Path.Combine(_dest, Path.GetFileName(r.Path)))).ToList();
        _total = _queue.Count;
        _done = 0;
        _failed = 0;
        Interlocked.Exchange(ref _bytes, 0);
        _cancelled = false;
        _note = null;
        _current = "";
        _startedAt = DateTime.UtcNow;

        var cts = new CancellationTokenSource();
        _cts = cts;
        _logger.LogInformation("开始搬移 {N} 个文件到 {Dest}（{Mode}）", _total, _dest, copy ? "复制" : "移动");
        _ = Task.Run(() => RunAsync(cts));
        return (true, $"开始{(_copy ? "复制" : "移动")} {_total} 个文件（另有 {rows.Count - go.Count} 个不动，原因在清单里）");
    }

    public void Stop()
    {
        if (!IsRunning) return;
        _cancelled = true;
        _note = "已请求停止：手上那个文件弄完就停";
        _cts.Cancel();
    }

    public object Status()
    {
        var done = _done;
        var elapsed = _startedAt is null ? 0 : (int)(DateTime.UtcNow - _startedAt.Value).TotalSeconds;
        var remaining = Math.Max(0, _total - done);
        var eta = done >= 2 && IsRunning ? (int)Math.Round(remaining * (elapsed / (double)done)) : 0;
        return new
        {
            running = IsRunning,
            what = _copy ? "复制文件" : "移动文件",
            dest = _dest,
            mode = _copy ? "copy" : "move",
            processed = done,
            total = _total,
            failed = _failed,
            bytes = Interlocked.Read(ref _bytes),
            remaining,
            percent = _total == 0 ? 0 : (int)Math.Round(done * 100.0 / _total),
            current = _current,
            elapsed,
            etaSeconds = eta,
            note = _note,
            lastRun = _lastRun
        };
    }

    private async Task RunAsync(CancellationTokenSource cts)
    {
        var token = cts.Token;
        var movedBytes = 0L;
        var failed = 0;
        var done = 0;
        string? firstError = null;

        try
        {
            foreach (var item in _queue)
            {
                if (token.IsCancellationRequested) { _cancelled = true; break; }
                _current = Path.GetFileName(item.Path);

                var (ok, reason, actual) = One(item);
                done++;
                _done = done;
                if (ok)
                {
                    movedBytes += actual;
                    Interlocked.Exchange(ref _bytes, movedBytes);
                }
                else
                {
                    failed++;
                    _failed = failed;
                    firstError ??= reason;
                    _logger.LogWarning("搬移失败：{Path} → {Dest}：{Reason}", item.Path, item.Dest, reason);
                }

                // 让 UI 上的"当前这个"能刷出来，也顺带把线程还给调度器
                await Task.Delay(10, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _note = "异常中断：" + ex.Message;
            _logger.LogError(ex, "搬移任务异常中断");
        }
        finally
        {
            _current = "";
            var note = _note ?? (_cancelled ? "被手动停止" : null)
                        ?? (failed > 0 ? $"{failed} 个没搬成，第一个原因：{firstError}" : null);
            _note = note;
            _lastRun = new RunResult(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), _copy ? "copy" : "move",
                _dest, _total, done, failed, movedBytes, _cancelled, note);
            Interlocked.Exchange(ref _running, 0);
            _logger.LogInformation("搬移结束：{Done}/{Total}，搬动 {GB:F1} GB，失败 {Failed} 个 —— {Note}",
                done, _total, movedBytes / 1e9, failed, note ?? "一切顺利");
        }
    }

    /// <summary>
    /// 搬一个文件并写库。返回"成没成、为什么、真正挪动了多少字节"。
    /// 跨卷的 File.Move 由运行时自己完成"复制 + 删源"，源文件只在复制成功后才被删，
    /// 所以我们只在调用返回后校验目标存在且大小对得上，再改库。
    /// </summary>
    private (bool Ok, string Reason, long Bytes) One(Item item)
    {
        try
        {
            var srcInfo = new FileInfo(item.Path);
            if (!srcInfo.Exists) return (false, "源文件不见了", 0);

            if (_copy)
            {
                File.Copy(item.Path, item.Dest, overwrite: false);
            }
            else
            {
                File.Move(item.Path, item.Dest, overwrite: false);
            }

            var dstInfo = new FileInfo(item.Dest);
            if (!dstInfo.Exists) return (false, "目标上看不到文件", 0);
            if (dstInfo.Length != item.Size)
            {
                // 大小对不上就别改库：移动的场合源已被删，改口等于把死链写进库里，
                // 宁可由界面报"没搬成、去看目标位置"，让人来决定怎么处理
                _logger.LogWarning("目标大小与库里不符：{Dst} 实际 {A} ≠ 记录 {B}", item.Dest, dstInfo.Length, item.Size);
                return (false, $"搬过去了但大小对不上（记录 {item.Size} vs 实际 {dstInfo.Length}），库里没改，先人工看一眼", 0);
            }

            if (!_copy)
            {
                using var conn = _db.GetConnection();
                conn.Open();
                // 只改路径与体积；行级番号、编码、时长、指纹都不跟着变（内容没动）
                using var upd = new SqliteCommand("UPDATE video_files SET file_path = @p, file_size = @s WHERE id = @id", conn);
                upd.Parameters.AddWithValue("@p", item.Dest);
                upd.Parameters.AddWithValue("@s", dstInfo.Length);
                upd.Parameters.AddWithValue("@id", item.FileId);
                if (upd.ExecuteNonQuery() == 0) return (false, "文件已到位，但库里那一行没找到（可能刚被删掉）", 0);
            }

            return (true, "", dstInfo.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, ex.Message, 0);
        }
    }
}

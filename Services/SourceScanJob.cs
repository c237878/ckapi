using ckapi.Utils;

namespace ckapi.Services;

/// <summary>
/// 全站片源扫描的后台任务（只量分辨率）。
///
/// 为什么不放在请求里跑：三千多个文件都在 SMB 共享上，一个来回 0.1~0.5 秒，
/// 全跑一遍是十分钟级的活。放请求里就得前端一直举着连接，离开页面还得自己中止。
///
/// 并发只用在探测那一步（读文件与数据库无关），写库回到单线程一整批一个事务——
/// SQLite 连接不是线程安全的，边并发读边写会把连接状态搞乱。
///
/// 进程内单例、同一时刻只跑一个。重启会丢进度，但已写进去的分辨率都在，
/// 重跑默认只挑没量过的（接口留了 force=true 全库重来，界面上不摆按钮）。
/// </summary>
public sealed class SourceScanJob
{
    /// <summary>同时读几个文件。共享盘的元数据读取是网络往返，6 路已经能把等待盖住，再多只是给人添堵</summary>
    private const int Parallelism = 6;

    /// <summary>一批探完一次性写库：批太小事务开销占比高，太大进度条看着一跳一跳</summary>
    private const int BatchSize = 24;

    private readonly SourceScanner _scanner;
    private readonly SQLiteHelper _db;
    private readonly ILogger<SourceScanJob> _logger;

    // 每次 Start 换一个新的：CancellationTokenSource 一旦 Cancel 就不可复用
    private CancellationTokenSource _cts = new();

    private int _running;
    private volatile int _processed;
    private volatile int _total;
    private volatile int _ok;
    private volatile int _missed;
    private volatile bool _force;
    private DateTime? _startedAt;
    private volatile string? _note;

    public SourceScanJob(SourceScanner scanner, SQLiteHelper db, ILogger<SourceScanJob> logger)
    {
        _scanner = scanner;
        _db = db;
        _logger = logger;
    }

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    public (bool Started, string Message) Start(bool force)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return (false, "扫描任务已经在跑了，先停下它");

        _processed = 0;
        _total = 0;
        _ok = 0;
        _missed = 0;
        _force = force;
        _note = null;
        _startedAt = DateTime.UtcNow;

        var cts = new CancellationTokenSource();
        _cts = cts;

        _ = Task.Run(() => RunAsync(force, cts));
        return (true, force ? "开始重扫全部影片" : "开始扫描未量过的影片");
    }

    public void Stop()
    {
        if (!IsRunning) return;
        _note = "已请求停止";
        _cts.Cancel();
    }

    public object Status()
    {
        var processed = _processed;
        var total = _total;
        var elapsed = _startedAt is null ? 0 : (int)(DateTime.UtcNow - _startedAt.Value).TotalSeconds;
        var remaining = Math.Max(0, total - processed);
        // 平均速度够 3 个以上才给 ETA，否则开局那一下会乱跳
        var eta = processed >= 3 && IsRunning ? (int)Math.Round(remaining * (elapsed / (double)processed)) : 0;

        return new
        {
            running = IsRunning,
            what = "分辨率",
            whatKey = "source",
            force = _force,
            processed,
            total,
            ok = _ok,
            missed = _missed,
            remaining,
            percent = total == 0 ? 0 : (int)Math.Round(processed * 100.0 / total),
            elapsed,
            etaSeconds = eta,
            note = _note
        };
    }

    private async Task RunAsync(bool force, CancellationTokenSource cts)
    {
        var sem = new SemaphoreSlim(Parallelism);
        try
        {
            using var conn = _db.GetConnection();
            conn.Open();

            var rows = _scanner.Pending(conn, force);
            _total = rows.Count;
            if (rows.Count == 0)
            {
                _note = "没有要扫的影片（都量过了；要全库重来用接口 ?force=true）";
                return;
            }

            for (var start = 0; start < rows.Count; start += BatchSize)
            {
                if (cts.IsCancellationRequested) break;

                var batch = rows.GetRange(start, Math.Min(BatchSize, rows.Count - start));
                var found = new SourceScanner.Inspection[batch.Count];

                try
                {
                    await Task.WhenAll(batch.Select((row, i) => Task.Run(async () =>
                    {
                        await sem.WaitAsync(cts.Token);
                        try
                        {
                            // 单个文件读不出不抛异常，Inspect 把原因收在 Error 里
                            found[i] = _scanner.Inspect(row);
                        }
                        finally
                        {
                            sem.Release();
                        }
                    }, cts.Token)));
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                using (var tx = conn.BeginTransaction())
                {
                    for (var i = 0; i < batch.Count; i++)
                    {
                        var ins = found[i];
                        _processed++;

                        if (ins is not { Ok: true })
                        {
                            _missed++;
                            continue;
                        }

                        _scanner.Write(conn, tx, batch[i], ins);
                        _ok++;
                    }
                    tx.Commit();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "片源扫描任务异常中断");
            _note = "任务异常中断，详见后端日志";
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
            sem.Dispose();
            _logger.LogInformation(
                "片源扫描结束（{Mode}）：查 {Processed} 个，量到 {Ok} 个，读不出 {Missed} 个",
                _force ? "重扫全部" : "只扫未量", _processed, _ok, _missed);
        }
    }
}

using ckapi.Utils;

namespace ckapi.Services;

/// <summary>
/// 全站头像抓取的后台任务。
///
/// 为什么不是"前端循环调批量接口"：全库一千多人是小时级的活，
/// 放在请求里跑就要前端一直举着连接、离开页面还得自己中止，进度也只能每批更新一次。
/// 挪到服务端之后：进度是逐个演员累加的，随时可停，关掉浏览器也不影响它继续跑。
///
/// 进程内单例、同一时刻只跑一个任务（这活儿是往别人的站点上问话，并行没有意义）。
/// 重启后端会丢掉进度状态，但已抓到的文件与表里的行都在，重跑会自动跳过有照片的人。
/// </summary>
public sealed class AvatarJob
{
    private readonly AvatarFetcher _fetcher;
    private readonly SQLiteHelper _db;
    private readonly ILogger<AvatarJob> _logger;
    // 每次 Start 都要换一个新的：CancellationTokenSource 一旦 Cancel 就不可复用，
    // 复用会让下一次启动在第一圈就退出（"停止过一次，之后再也启动不起来"）
    private CancellationTokenSource _cts = new();

    private int _running;
    private volatile int _processed;
    private volatile int _total;
    private volatile int _fetched;
    private volatile int _skipped;
    private DateTime? _startedAt;
    private volatile string? _note;

    public AvatarJob(AvatarFetcher fetcher, SQLiteHelper db, ILogger<AvatarJob> logger)
    {
        _fetcher = fetcher;
        _db = db;
        _logger = logger;
    }

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    public (bool Started, string Message) Start()
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return (false, "抓取任务已经在跑了");

        _processed = 0;
        _total = 0;
        _fetched = 0;
        _skipped = 0;
        _note = null;
        _startedAt = DateTime.UtcNow;

        var cts = new CancellationTokenSource();
        _cts = cts;

        _ = Task.Run(() => RunAsync(cts));
        return (true, "已开始抓取全站头像");
    }

    public void Stop()
    {
        if (!IsRunning) return;
        _note = "已请求停止";
        _cts.Cancel();   // 停的是本次运行的那个源，下一次 Start 会用新的
    }

    public object Status()
    {
        var processed = _processed;
        var total = _total;
        var elapsed = _startedAt is null ? 0 : (int)(DateTime.UtcNow - _startedAt.Value).TotalSeconds;
        var remaining = Math.Max(0, total - processed);
        // 预计剩余：用已经跑出来的平均速度估，样本太少（<3）就不给数，免得开局乱跳
        var eta = processed >= 3 && IsRunning ? (int)Math.Round(remaining * (elapsed / (double)processed)) : 0;

        return new
        {
            running = IsRunning,
            processed,
            total,
            fetched = _fetched,
            skipped = _skipped,
            remaining,
            percent = total == 0 ? 0 : (int)Math.Round(processed * 100.0 / total),
            elapsed,
            etaSeconds = eta,
            note = _note
        };
    }

    private async Task RunAsync(CancellationTokenSource cts)
    {
        var consecutive = 0;
        try
        {
            using var conn = _db.GetConnection();
            conn.Open();

            var posterDir = ImageIndex.PosterDir(conn);
            if (string.IsNullOrEmpty(posterDir))
            {
                _note = "未配置艳图目录（系统设置 → 艳图目录）";
                return;
            }

            var risk = _fetcher.DuplicateRiskIds(conn);
            var candidates = _fetcher.Candidates(conn, risk);
            _total = candidates.Count;

            foreach (var id in candidates)
            {
                if (cts.IsCancellationRequested) break;

                (bool Ok, string Message) res;
                try
                {
                    res = await _fetcher.FetchOneAsync(conn, id, posterDir, risk, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    res = (false, ex.Message);
                }

                _processed++;
                if (res.Ok)
                {
                    _fetched++;
                    consecutive = 0;
                }
                else if (res.Message.StartsWith("站点") || res.Message.StartsWith("头像下载"))
                {
                    // 连续失败说明被限流或断网，再问下去只是给人添堵
                    if (++consecutive >= 3)
                    {
                        _note = "站点连续无响应，本轮提前结束";
                        break;
                    }
                }
                else
                {
                    _skipped++;
                    consecutive = 0;
                }

                // 站间节流：这是别人的服务器，一次问一千多人不能没有间隔
                try
                {
                    await Task.Delay(300, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "头像批量抓取任务异常中断");
            _note = "任务异常中断，详见后端日志";
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
            _logger.LogInformation("头像抓取任务结束：查 {Processed} 位，抓到 {Fetched} 张", _processed, _fetched);
        }
    }
}

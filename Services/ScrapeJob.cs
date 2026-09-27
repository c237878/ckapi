using ckapi.Utils;

namespace ckapi.Services;

/// <summary>
/// 全站演员资料抓取的后台任务。
///
/// 为什么不是"前端循环调批量接口"：全库一千多人是小时级的活，
/// 放在请求里跑就要前端一直举着连接、离开页面还得自己中止，进度也只能每批更新一次。
/// 挪到服务端之后：进度是逐个演员累加的，随时可停，关掉浏览器也不影响它继续跑。
///
/// 进程内单例、同一时刻只跑一个任务（这活儿是往别人的站点上问话，并行没有意义）。
/// 源（av-wiki / 老师图鉴）由启动参数选，一次任务只问一个源，
/// 因为限速、每日配额与熔断都是按源各算各的。
/// 重启后端会丢掉进度状态，但已抓到的文件与已填的字段都在，重跑会自动跳过填好的的人。
/// </summary>
public sealed class ScrapeJob
{
    private readonly Dictionary<string, IActorSource> _sources;
    private readonly SQLiteHelper _db;
    private readonly ILogger<ScrapeJob> _logger;
    // 限速、每日配额、连续失败熔断全走抓取通道那张表：
    // 之前这里是写死的 Task.Delay(300)，正是那种"把自己抓封"的写法
    private readonly ScrapeChannelService _channels;
    // 每次 Start 都要换一个新的：CancellationTokenSource 一旦 Cancel 就不可复用，
    // 复用会让下一次启动在第一圈就退出（"停止过一次，之后再也启动不起来"）
    private CancellationTokenSource _cts = new();

    private int _running;
    private volatile IActorSource? _src;
    private volatile int _processed;
    private volatile int _total;
    private volatile int _hit;
    private volatile string? _what;
    private volatile string? _whatKey;
    private DateTime? _startedAt;
    private volatile string? _note;

    public ScrapeJob(
        IEnumerable<IActorSource> sources, SQLiteHelper db, ILogger<ScrapeJob> logger, ScrapeChannelService channels)
    {
        _sources = sources.ToDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);
        _db = db;
        _logger = logger;
        _channels = channels;
    }

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    public (bool Started, string Message) Start(Want want, string? srcKey)
    {
        var src = Resolve(srcKey);
        if (src is null)
            return (false, $"没有这个抓取来源：{(string.IsNullOrEmpty(srcKey) ? "未指定" : srcKey)}");

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return (false, $"抓取任务已经在跑了（{_what}），先停下它");

        _src = src;
        _processed = 0;
        _total = 0;
        _hit = 0;
        _what = $"{src.Label} {Label(want)}";
        _whatKey = Key(want);
        _note = null;
        _startedAt = DateTime.UtcNow;

        var cts = new CancellationTokenSource();
        _cts = cts;

        _ = Task.Run(() => RunAsync(src, want, cts));
        return (true, $"开始从{src.Label}抓取{Label(want)}");
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
        var src = _src;

        return new
        {
            running = IsRunning,
            what = _what,
            whatKey = _whatKey,
            srcKey = src?.Key ?? "",
            srcLabel = src?.Label ?? "",
            processed,
            total,
            hit = _hit,
            remaining,
            percent = total == 0 ? 0 : (int)Math.Round(processed * 100.0 / total),
            elapsed,
            etaSeconds = eta,
            note = _note
        };
    }

    /// <summary>界面拿 whatKey 判断"在跑的是哪种"，别去比对中文标签</summary>
    private static string Key(Want want) => want switch
    {
        Want.Avatar => "avatar",
        Want.Profile => "profile",
        _ => "all"
    };

    private static string Label(Want want) => want switch
    {
        Want.Avatar => "头像",
        Want.Profile => "资料",
        _ => "头像与资料"
    };

    /// <summary>没指定来源时仍走 av-wiki：老书签和老脚本不能因为加了第二个源就改行为</summary>
    private IActorSource? Resolve(string? srcKey) =>
        string.IsNullOrWhiteSpace(srcKey)
            ? _sources.Values.FirstOrDefault(s => s.Key == "avwiki")
            : _sources.GetValueOrDefault(srcKey);

    /// <summary>站点侧失败才算熔断依据；"查无此档"是对话正常结束，不该把通道打死</summary>
    private static bool IsSiteFault(string message) =>
        message.StartsWith("站点") || message.StartsWith("头像下载");

    private async Task RunAsync(IActorSource src, Want want, CancellationTokenSource cts)
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

            var channel = _channels.FirstFor("actor", src.Host);
            if (channel is null)
            {
                _note = $"没有 {src.Label} 这条抓取通道（设置 → 抓取通道）";
                return;
            }

            var risk = ActorGate.DuplicateRiskIds(conn);
            var candidates = await src.CandidatesAsync(conn, risk, want, cts.Token);
            _total = candidates.Count;

            foreach (var id in candidates)
            {
                if (cts.IsCancellationRequested) break;

                // 每条之前重读一次通道状态：Report 会改表，冷却是中途也可能触发的
                var fresh = _channels.Get(channel.Id) ?? channel;
                var gate = _channels.Check(fresh);
                if (!gate.Allowed)
                {
                    _note = $"{gate.Why}，本轮到此为止";
                    break;
                }
                await _channels.WaitTurnAsync(fresh, cts.Token);

                (bool Ok, string Message) res;
                try
                {
                    res = await src.ScrapeAsync(conn, id, posterDir, risk, want, cts.Token);
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
                var fault = !res.Ok && IsSiteFault(res.Message);
                _channels.Report(fresh.Id, !fault, fault ? res.Message : null);

                if (res.Ok)
                {
                    _hit++;
                    consecutive = 0;
                }
                else if (fault)
                {
                    // 连续失败说明被限流或断网，再问下去只是给人添堵。
                    // 熔断的"停多久"由通道的 fail_limit / cooldown_minutes 决定，界面上能调
                    consecutive++;
                    var limited = _channels.Get(fresh.Id) ?? fresh;
                    if (!string.IsNullOrEmpty(limited.BlockedUntil) &&
                        DateTime.TryParse(limited.BlockedUntil, out var until) && until > DateTime.Now)
                    {
                        _note = $"站点连续无响应，进入冷却（{until:HH:mm} 前不再问）";
                        break;
                    }
                }
                else
                {
                    consecutive = 0;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "演员资料批量抓取任务异常中断");
            _note = "任务异常中断，详见后端日志";
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
            _logger.LogInformation("抓取任务结束（{What}）：查 {Processed} 位，有收获 {Hit} 位",
                _what, _processed, _hit);
        }
    }
}

using System.Collections.Concurrent;
using ckapi.Utils;
using Microsoft.Data.Sqlite;

namespace ckapi.Services;

/// <summary>
/// 后台自己跑的那轮"体检"：一次盘上核对 + 一次增量扫描。
///
/// **1. 增量扫描**（`SourceScanJob.Start(force:false)`）——新录入的片子的分辨率和指纹不用人去点
/// 「扫描片源」。候选只有"还没量过的"那些行，所以跑完之后每轮几乎什么都不做，
/// 成本随新增量走，不随库的总量走。
///
/// **2. 盘上核对**——把库里登记的文件路径去重后逐个 stat，只看"在不在"，不读内容。
/// 这一条是给"库里说文件在、盘上其实没有了"准备的：增量扫描永远跳不过已经量过的行，
/// 所以文件后来被搬走／删掉，光靠扫描发现不了，非得整体核对一次才有数。
/// 核对**只报证据不改库**：挂载卷掉线时全部文件都会显示"不在"，这时候清记录
/// 就是把一天的故障变成永久的数据损坏。
///
/// 开关与频率存在 system_settings（设置→运行状态那块界面直接改），**每个看门周期重读一次**，
/// 所以改完最多等一个周期就生效，不用重启进程；appsettings 里那几个键只剩"没在界面上设过"时的默认值。
/// 上一轮的开始时间也落库：频率一旦能设到 7 天、30 天，"重启就算没跑过"会从补一次变成白扫一遍。
///
/// 为什么是"看门 + 到点才跑"而不是 cron：进程经常连着跑几周，唤醒时刻得跟着进程走；
/// 跟 BackupService 同一个套路——每小时醒来看该不该跑，该跑就跑，进程重启后最多一小时补上。
/// </summary>
public class ScanScheduleService : BackgroundService
{
    /// <summary>默认启动后等多久再动手：等库初始化跑完、等开机第一波请求过去，别跟启动抢同一块挂载卷</summary>
    private const int DefaultStartupDelayMinutes = 3;

    /// <summary>默认看门间隔：一天 24 次足够，且错过一次（比如手动扫描正占着）下一轮就补</summary>
    private const int DefaultIntervalMinutes = 60;

    /// <summary>核对的并发。只做一次元数据往返，比读容器头便宜，但也不该把卷砸满</summary>
    private const int SweepParallelism = 8;

    /// <summary>面板上最多列几条"不在"的样本；真要清账的人自然会去磁盘上看</summary>
    private const int SampleLimit = 12;

    /// <summary>缺文件占比超过这个数就当"整卷没挂"，不给逐条清单——那种时候清单是误导</summary>
    private const double SuspiciousRatio = 0.3;

    /// <summary>一轮核对最多花多少分钟。卷冷的时候 stat 一次能要几十秒，
    /// 宁报"没看完"也不能让后台任务没有终点；给 20 分钟是全库三千多个路径的正常量的好几倍</summary>
    private const int SweepBudgetMinutes = 20;

    private static readonly TimeSpan SweepBudget = TimeSpan.FromMinutes(SweepBudgetMinutes);

    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

    /// <summary>开关。关掉只停后台这一条通路，界面上的「扫描片源」按钮照旧能点</summary>
    public const string SettingEnabled = "scan_auto_enabled";

    /// <summary>频率：隔多少天跑一轮，1 就是"每天"</summary>
    public const string SettingIntervalDays = "scan_interval_days";

    /// <summary>上一轮的开始时间。落库而不是记在内存里，重启才不会当天再来一遍</summary>
    public const string SettingLastRun = "scan_last_run";

    /// <summary>频率的可设范围。少于 1 天没意义；超过 30 天那本账就只是心理安慰</summary>
    public const int MinIntervalDays = 1;
    public const int MaxIntervalDays = 30;

    /// <summary>样本：哪一个版本行、属于哪一部片、路径长什么样</summary>
    public sealed record MissingFile(string FileId, string VideoId, string Code, string Path);

    /// <summary>
    /// 一轮核对的结论。跑完才写，中途不更新。
    /// Checked 是**去重后的路径数**（多个版本行指向同一个文件只数一次），
    /// Complete=false 表示时间用完还没看完，那次的数字只是前一段的数。
    /// </summary>
    public sealed record SweepResult(
        string RanAt, int Checked, int Missing, bool Complete, bool Suspicious,
        IReadOnlyList<MissingFile> Samples);

    /// <summary>
    /// 从 system_settings 拼出来的节奏。读的时候就把脏值收拾干净：
    /// 这张表是通用键值表，手改一个 "yes" 进去不该让后台任务从此不跑、或者每小时跑一次。
    /// </summary>
    public sealed record Policy(bool Enabled, int IntervalDays, DateTime? LastRun)
    {
        /// <summary>下一轮到点的时间。从没跑过时为 null（界面该说"没跑过"，而不是报公元 1 年）</summary>
        public DateTime? DueAt => LastRun is null ? null : LastRun.Value + TimeSpan.FromDays(IntervalDays);

        public bool IsDue(DateTime now) => Enabled && (DueAt is null || now >= DueAt.Value);
    }

    private readonly SourceScanJob _job;
    private readonly SQLiteHelper _db;
    private readonly IConfiguration _config;
    private readonly ILogger<ScanScheduleService> _logger;

    private readonly TimeSpan _startupDelay;
    private readonly TimeSpan _interval;

    private volatile string? _note;
    private volatile SweepResult? _lastSweep;

    /// <summary>一轮体检的互斥闸：看门和面板上的「立刻跑一次」可能同时来</summary>
    private int _busy;

    public ScanScheduleService(
        SourceScanJob job, SQLiteHelper db, IConfiguration config, ILogger<ScanScheduleService> logger)
    {
        _job = job;
        _db = db;
        _config = config;
        _logger = logger;
        // 这两个管的是"看门多久醒一次"，不是"要不要跑"——后者在界面上设。
        // 默认值按"连着跑几周的实机"定，验证用的临时实例不该等三分钟才知道任务跑没跑
        _startupDelay = TimeSpan.FromMinutes(Math.Max(0,
            config.GetValue("Media:ScanStartupDelayMinutes", DefaultStartupDelayMinutes)));
        _interval = TimeSpan.FromMinutes(Math.Max(1,
            config.GetValue("Media:ScanIntervalMinutes", DefaultIntervalMinutes)));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(_startupDelay, stoppingToken);
            await TickAsync(stoppingToken);

            using var timer = new PeriodicTimer(_interval);
            // 一轮跑几个小时也没关系：期间漏掉的 tick 不会被补发，下一轮醒来照样只看"到点没"
            while (await timer.WaitForNextTickAsync(stoppingToken)) await TickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // 正常停机
        }
    }

    /// <summary>
    /// 每个看门周期的一次查看：开关开着吗、到点没。都不满足就直接回去——
    /// 所以"关掉"和"设成 7 天"都只要等下一个周期，不用重启进程。
    /// </summary>
    private async Task TickAsync(CancellationToken stoppingToken)
    {
        var p = ReadPolicy();
        if (!p.Enabled)
        {
            _note = "自动体检是关着的；想跑就点下面的「立刻跑一次」，或者把开关打开";
            return;
        }
        if (!p.IsDue(DateTime.Now))
        {
            _note = null;
            return;
        }
        await RunAsync(p.IntervalDays, stoppingToken);
    }

    /// <summary>
    /// 跑一轮体检（带闸）。间隔天数只用来写日志，判定到点的是 <see cref="TickAsync"/>。
    /// 闸要一直握到扫描跑完：核对只有几十秒，扫描才是长的那一段，
    /// 提前放开的话「立刻跑一次」连点两下就会同时起两轮核对。
    /// </summary>
    private async Task RunAsync(int intervalDays, CancellationToken stoppingToken)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            _note = "上一轮还没收尾，这一轮跳过";
            return;
        }
        try
        {
            await BodyAsync(intervalDays, stoppingToken);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>一轮体检的正身。调用方必须已经握着 _busy 那把闸。</summary>
    private async Task BodyAsync(int intervalDays, CancellationToken stoppingToken)
    {
        try
        {
            // 先问一句配置里的目录在不在：卷掉了时全库都会报"不在"，那不是文件不见了，是路断了。
            // 一条 SQL + 几次 Directory.Exists，比全库 stat 便宜得多
            var absent = AbsentScanRoots();
            if (absent.Count > 0)
            {
                // 刻意不记"这轮跑过了"：卷没挂时白跑一轮，还不许下一轮重来，那就只能等人
                _note = "配置的片源目录不在，这一轮跳过：" + string.Join('、', absent);
                _logger.LogWarning("自动体检跳过：{Roots}", string.Join('、', absent));
                return;
            }

            // 先落"这轮开始了"再动手：核对最坏要 20 分钟，崩在半路上也不该下个周期又起一轮
            MarkRunStarted();
            SweepOnce();

            var (started, message) = _job.Start(false, "定时");
            if (started)
            {
                _logger.LogInformation("自动体检（每 {Days} 天）：核对完了，扫描已启动（{Message}）", intervalDays, message);
                while (_job.IsRunning) await Task.Delay(2000, stoppingToken);
            }
            else
                // 手动扫描正占着：核对结果已经在了，扫描下一轮再补
                _note = "扫描让位于手动任务：" + message;
        }
        catch (OperationCanceledException)
        {
            // 停机时扫描还在跑是常态，不算故障
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "自动体检异常");
            _note = "这一轮跑挂了：" + ex.Message;
        }
    }

    /// <summary>
    /// 面板上的「立刻跑一次」：不看开关也不等到点，但仍受同一把闸管。
    /// 闸在这里**同步**抢，不能等后台线程起来才抢——否则两次快速点击都以为自己拿到了。
    /// 活丢到后台线程去做：一轮最坏二十几分钟，不能把这次 HTTP 请求举着。
    /// </summary>
    public (bool Started, string Message) RunNow()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return (false, "已经有一轮在跑了，等它收尾");
        var days = ReadPolicy().IntervalDays;
        _ = Task.Run(async () =>
        {
            try { await BodyAsync(days, CancellationToken.None); }
            finally { Interlocked.Exchange(ref _busy, 0); }
        });
        return (true, "开始盘上核对与增量扫描");
    }

    /// <summary>
    /// 节奏读的是通用键值表，所以这里负责把值收拾成能用的东西：
    /// 缺键按默认（开着、每 1 天），越界的频率夹回范围内，认不出的时间当"没跑过"。
    /// </summary>
    public Policy ReadPolicy()
    {
        var raw = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var conn = _db.GetConnection())
        {
            conn.Open();
            using var cmd = new SqliteCommand(
                "SELECT name, content FROM system_settings WHERE name IN (@a, @b, @c)", conn);
            cmd.Parameters.AddWithValue("@a", SettingEnabled);
            cmd.Parameters.AddWithValue("@b", SettingIntervalDays);
            cmd.Parameters.AddWithValue("@c", SettingLastRun);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) raw[reader.GetString(0)] = reader.IsDBNull(1) ? "" : reader.GetString(1);
        }

        // "没记过"不等于"关掉了"：这套任务上线时就是默认开的，缺键不能当成用户做过关闭的决定
        var enabled = raw.TryGetValue(SettingEnabled, out var e)
            ? e.Trim() is "1" or "true"
            : _config.GetValue<bool?>("Media:AutoScan") ?? true;

        var days = raw.TryGetValue(SettingIntervalDays, out var d) && int.TryParse(d, out var parsedDays)
            ? Math.Clamp(parsedDays, MinIntervalDays, MaxIntervalDays)
            : 1;

        DateTime? last = raw.TryGetValue(SettingLastRun, out var l)
                         && DateTime.TryParse(l, out var parsedAt)
            ? parsedAt
            : null;

        return new Policy(enabled, days, last);
    }

    /// <summary>盘上核对：把登记过路径的版本行按路径去重，逐个 stat。不改库，只出结论。</summary>
    private void SweepOnce()
    {
        var rows = LoadPaths();
        // 同一个路径可能被好几个版本行指着（v14 的疑似重复就是这种情况），
        // 按路径去重才是"盘上有几个文件不在"，否则一边缺、几条就跟着报几条
        var distinct = rows.GroupBy(r => r.Path, StringComparer.Ordinal)
                          .Select(g => g.First())
                          .ToList();

        var seen = 0;
        var gone = 0;
        var missing = new ConcurrentBag<MissingFile>();
        var complete = true;

        // 到点就停，把"看了多少个"如实报出去，而不是让这一轮挂在第一个慢文件上不走
        using var budget = new CancellationTokenSource(SweepBudget);
        try
        {
            Parallel.ForEach(distinct,
                new ParallelOptions { MaxDegreeOfParallelism = SweepParallelism, CancellationToken = budget.Token },
                row =>
                {
                    Interlocked.Increment(ref seen);
                    if (File.Exists(row.Path)) return;
                    if (Interlocked.Increment(ref gone) <= SampleLimit) missing.Add(row);
                });
        }
        catch (OperationCanceledException)
        {
            complete = false;
        }
        catch (Exception ex)
        {
            // 单个路径不会抛（File.Exists 已经把 IO 错误吞成 false），抛了就是线程池层面的事，
            // 记下来但别把已经数出来的结果丢了
            _logger.LogError(ex, "盘上核对中断");
            complete = false;
        }

        var suspicious = seen > 0 && gone / (double)seen > SuspiciousRatio;
        _lastSweep = new SweepResult(
            DateTime.Now.ToString(TimeFormat), seen, gone, complete, suspicious,
            missing.OrderBy(x => x.Code, StringComparer.Ordinal)
                   .ThenBy(x => x.Path, StringComparer.Ordinal)
                   .Take(SampleLimit)
                   .ToList());
        _note = complete ? null
            : $"核对没跑完（{SweepBudgetMinutes} 分钟到点），只看了 {seen} / {distinct.Count} 个";
        _logger.LogInformation(
            "盘上核对：看了 {Seen} / {Total} 个文件，{Missing} 个不在盘上{Tail}",
            seen, distinct.Count, gone,
            suspicious ? "（占比过高，怀疑是挂载卷掉了，不作为清单使用）"
                       : complete ? "" : "（这一轮没看完）");
    }

    /// <summary>配置里登记的片源/封面/字幕目录，哪些现在根本不存在</summary>
    private List<string> AbsentScanRoots()
    {
        var absent = new List<string>();
        using var conn = _db.GetConnection();
        conn.Open();
        using var cmd = new SqliteCommand(
            "SELECT category, path FROM scan_directories WHERE IFNULL(path, '') <> ''", conn);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var path = reader.GetString(1);
            if (!Directory.Exists(path)) absent.Add($"{reader.GetString(0)}：{path}");
        }
        // 同一个路径登记过两次（真实库里就有"视频"两条同一路径）不该在提示里念两遍
        return absent.Distinct().ToList();
    }

    /// <summary>
    /// 所有登记了路径的版本行。这里刻意**不看 file_size**：
    /// "有路径但没量到大小"（自检里的 size_missing）恰恰是"文件从来没找到过"的那种，
    /// 只看 size&gt;0 的就把这一类漏在核对外面了。file_path 为空是"这一版还没上传"，不核对。
    /// </summary>
    private List<MissingFile> LoadPaths()
    {
        const string sql = @"
            SELECT f.id, f.video_id, IFNULL(v.code, ''), f.file_path
            FROM video_files f
            LEFT JOIN videos v ON v.id = f.video_id
            WHERE IFNULL(f.file_path, '') <> ''
            ORDER BY f.id";

        var rows = new List<MissingFile>();
        using var conn = _db.GetConnection();
        conn.Open();
        using var cmd = new SqliteCommand(sql, conn);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new MissingFile(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }
        return rows;
    }

    /// <summary>记一笔"这一轮是什么时候开始的"</summary>
    private void MarkRunStarted()
    {
        using var conn = _db.GetConnection();
        SettingStore.Upsert(conn, SettingLastRun, DateTime.Now.ToString(TimeFormat));
    }

    /// <summary>
    /// 给运行状态面板看的概况：开关、频率、上次与下次，加上最近一轮核对的结果。
    /// 扫描本身的结果在 <see cref="SourceScanJob.LastRun"/> 里。
    /// </summary>
    public object Status()
    {
        var p = ReadPolicy();
        var next = p.DueAt;
        return new
        {
            enabled = p.Enabled,
            intervalDays = p.IntervalDays,
            lastRun = p.LastRun?.ToString(TimeFormat) ?? "",
            nextRunAt = next is null ? "" : next.Value.ToString(TimeFormat),
            due = p.IsDue(DateTime.Now),
            // "在跑"要看两处：这一轮的闸，以及扫描任务本身（核对完了扫描还在跑是常态）
            running = Volatile.Read(ref _busy) == 1 || _job.IsRunning,
            watchdogMinutes = (int)_interval.TotalMinutes,
            minIntervalDays = MinIntervalDays,
            maxIntervalDays = MaxIntervalDays,
            sweep = _lastSweep,
            note = _note
        };
    }
}

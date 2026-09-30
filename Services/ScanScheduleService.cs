using System.Collections.Concurrent;
using ckapi.Utils;
using Microsoft.Data.Sqlite;

namespace ckapi.Services;

/// <summary>
/// 每天自己跑一遍的两件事：
///
/// **1. 增量扫描**（`SourceScanJob.Start(force:false)`）——新录入的片子的分辨率和指纹不用再
/// 人去点「扫描片源」。候选只有"还没量过的"那些行，所以扫完之后每天这一趟几乎什么都不做，
/// 成本随新增量走，不随库的总量走。
///
/// **2. 盘上核对**——把库里登记的文件路径逐个 stat 一遍，只看"在不在"，不读内容。
/// 这一条是给"库里说文件在、盘上其实没有了"准备的：增量扫描永远跳不过已经量过的行，
/// 所以文件后来被搬走／删掉，光靠扫描是发现不了的，非得整体核对一次才有数。
/// 核对**只报证据不改库**：挂载卷掉线时全部文件都会显示"不在"，这时候清掉 file_size
/// 就是把一天的故障变成永久的数据损坏。
///
/// 为什么是"每天一次 + 每小时看门"而不是 cron：进程经常连着跑几周，唤醒时刻得跟着进程走；
/// 跟 BackupService 同一个套路——每小时醒来看今天做过没有，没做就做，进程重启后最多一小时补上。
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

    private readonly TimeSpan _startupDelay;
    private readonly TimeSpan _interval;

    /// <summary>样本：哪一个版本行、属于哪一部片、路径长什么样</summary>
    public sealed record MissingFile(string FileId, string VideoId, string Code, string Path);

    /// <summary>
    /// 一轮核对的结论。跑完才写，中途不更新。
    /// Checked 是**去重后的路径数**（多个版本行指向同一个文件只数一次），
    /// Complete=false 表示时间用完还没看完，那天的数字只是前一段的数。
    /// </summary>
    public sealed record SweepResult(
        string RanAt, int Checked, int Missing, bool Complete, bool Suspicious,
        IReadOnlyList<MissingFile> Samples);

    private readonly SourceScanJob _job;
    private readonly SQLiteHelper _db;
    private readonly IConfiguration _config;
    private readonly ILogger<ScanScheduleService> _logger;

    /// <summary>今天（本地日期）有没有已经为定时跑过一次扫描／核对。只做进程内记忆：
    /// 重启后当天会重来一遍，而这两件事重来都不花钱</summary>
    private string? _scannedOn;
    private string? _sweptOn;

    private volatile string? _note;
    private volatile SweepResult? _lastSweep;

    public ScanScheduleService(
        SourceScanJob job, SQLiteHelper db, IConfiguration config, ILogger<ScanScheduleService> logger)
    {
        _job = job;
        _db = db;
        _config = config;
        _logger = logger;
        // 两个节拍都留了配置口子：默认值是按"连着跑几周的实机"定的，
        // 而验证用的临时实例不该等三分钟才知道定时任务跑没跑
        _startupDelay = TimeSpan.FromMinutes(Math.Max(0,
            config.GetValue("Media:ScanStartupDelayMinutes", DefaultStartupDelayMinutes)));
        _interval = TimeSpan.FromMinutes(Math.Max(1,
            config.GetValue("Media:ScanIntervalMinutes", DefaultIntervalMinutes)));
    }

    /// <summary>关掉自动扫描：appsettings 里写 `"Media": { "AutoScan": false }`。
    /// 只关这一条通路，界面上的「扫描片源」按钮不受影响</summary>
    public bool Enabled => _config.GetValue<bool?>("Media:AutoScan") ?? true;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled)
        {
            _note = "自动扫描已关闭（Media:AutoScan=false），只能手动点「扫描片源」";
            _logger.LogInformation("片源自动扫描已关闭（Media:AutoScan=false）");
            return;
        }

        try
        {
            await Task.Delay(_startupDelay, stoppingToken);
            Tick();

            using var timer = new PeriodicTimer(_interval);
            while (await timer.WaitForNextTickAsync(stoppingToken)) Tick();
        }
        catch (OperationCanceledException)
        {
            // 正常停机
        }
    }

    /// <summary>
    /// 每小时的一次查看：今天扫过没有、核对过没有，各补一次。
    /// 顺序是"先核对再扫描"——核对只要几十秒到几分钟，而它给出的"卷在不在"是扫描值不值得跑的前置条件。
    /// </summary>
    private void Tick()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        try
        {
            if (_sweptOn != today)
            {
                var swept = SweepOnce(today);
                if (!swept) return;   // 卷没挂上：这时候扫描也只会白读，下一小时再试
            }

            if (_scannedOn != today)
            {
                var (started, message) = _job.Start(false, "定时");
                if (started)
                {
                    _scannedOn = today;
                    _logger.LogInformation("定时扫描已启动：{Message}", message);
                }
                else
                {
                    // 手动扫描正占着，或者上一轮还没收尾：不算今天做过，下一小时再来
                    _note = "定时扫描让位于手动任务：" + message;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "定时扫描任务异常");
            _note = "定时任务这一轮跑挂了：" + ex.Message;
        }
    }

    /// <summary>
    /// 盘上核对：把登记过路径的版本行按路径去重，逐个 stat。
    /// 返回 false 表示"配置的目录整个不在"，这时候的结果没有意义，别当成"文件都被删了"。
    /// </summary>
    private bool SweepOnce(string today)
    {
        // 先问一句配置里的目录在不在：卷掉了时全库都会报"不在"，那不是文件不见了，是路断了。
        // 这一句比全库 stat 便宜得多（一条 SQL + 几次 Directory.Exists）
        var absentRoots = AbsentScanRoots();
        if (absentRoots.Count > 0)
        {
            _note = "配置的片源目录不在，跳过核对：" + string.Join('、', absentRoots);
            _logger.LogWarning("盘上核对跳过：{Roots}", string.Join('、', absentRoots));
            return false;
        }

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

        // 一天一次的活动不能没有终点：卷冷的时候一次 stat 能要几十秒，
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

        // 没看完也照样算今天看过：看不完的原因通常是卷慢，下一小时再来看只会更慢
        _sweptOn = today;
        var suspicious = seen > 0 && gone / (double)seen > SuspiciousRatio;
        _lastSweep = new SweepResult(
            Now(), seen, gone, complete, suspicious,
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
        return true;
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

    /// <summary>给运行状态面板看的概况（扫描本身的结果在 SourceScanJob.Status 里）</summary>
    public object Status() => new
    {
        enabled = Enabled,
        startupDelayMinutes = (int)_startupDelay.TotalMinutes,
        intervalMinutes = (int)_interval.TotalMinutes,
        today = DateTime.Now.ToString("yyyy-MM-dd"),
        scannedOn = _scannedOn,
        sweptOn = _sweptOn,
        sweep = _lastSweep,
        note = _note
    };

    private static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
}

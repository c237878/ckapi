using System.Globalization;
using System.Text.RegularExpressions;

namespace ckapi.Services;

/// <summary>
/// 每日常规备份 + 轮转。
///
/// 为什么不能只在启动时备一份：这个进程经常连着跑几周不重启，"今天的快照"就永远只有
/// 重启那天那一份，出事时能回滚的点少得可怜。这里每小时醒一次看今天的快照落盘没有，
/// 没落就补一份——顺带把"半夜崩了被 launchd 拉起""长期不重启"两种情况都覆盖了。
///
/// 备份目录是跨项目共用的（旁边还放着别的项目的 log_*.db），所以轮转**只认自己那一个
/// 严格格式的文件名**：`ckplayer_yyyy-MM-dd.db`。凡是名字里还带标签的一律不碰——
/// 真实目录里这样的名字不少（`_pre-migration`、`_pre-merge`、`_pre-filepath-cleanup`），
/// 那些都是"某个不可逆动作之前的唯一凭据"，宁可能占点盘也不能自动删；
/// 别的项目的文件（log_*.db、manual_*.db）前缀就不对，同样碰不到。
/// </summary>
public class BackupService : BackgroundService
{
    /// <summary>检查间隔：一天 24 次足够，且进程重启后最多一小时就补上</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>每日快照保留天数</summary>
    public const int KeepDailyDays = 14;

    /// <summary>超出 14 天后，周日那份额外保留到多少天（等于每月留一份的能力）</summary>
    public const int KeepWeeklyDays = 60;

    private readonly ILogger<BackupService> _logger;
    private readonly Utils.SQLiteHelper _db;

    public BackupService(ILogger<BackupService> logger, Utils.SQLiteHelper db)
    {
        _logger = logger;
        _db = db;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 启动后先跑一次：这次运行如果正好是当天第一份，就不用等到整点
        RunOnce();

        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken)) RunOnce();
        }
        catch (OperationCanceledException)
        {
            // 正常停机
        }
    }

    /// <summary>今天的快照不存在就补一份，然后按保留策略清理。返回本次是否新建了备份。</summary>
    public bool RunOnce()
    {
        var created = _db.BackupDatabase("每日常规");
        var removed = Rotate(_db.GetDbPath(), _db.GetBackupPath(), DateTime.Now, _logger);
        // 删除动作必须留痕，哪怕这次没新建快照：出了问题要能回答"是谁在什么时候删了哪几份"
        if (created || removed > 0)
            _logger.LogInformation("备份例行检查：新建 {Created}，清理旧快照 {Removed} 份", created ? "是" : "否", removed);
        return created;
    }

    /// <summary>
    /// 轮转：14 天内的每日快照全留；更早的只留每周日那一份，留到 60 天；再老删掉。
    /// 抽成静态方法是为了能在副本目录上单独验证——生产目录里混着别的项目的文件，
    /// 我不想拿真目录测删除逻辑。
    /// </summary>
    public static int Rotate(string dbPath, string? backupDir, DateTime now, ILogger? logger = null)
    {
        if (string.IsNullOrEmpty(backupDir) || !Directory.Exists(backupDir)) return 0;

        var pattern = new Regex(
            @"^" + Regex.Escape(Path.GetFileNameWithoutExtension(dbPath)) + @"_(\d{4}-\d{2}-\d{2})\.db$",
            RegexOptions.IgnoreCase);

        var dated = new List<(DateTime Day, string Path)>();
        foreach (var file in Directory.GetFiles(backupDir, "*.db"))
        {
            var m = pattern.Match(Path.GetFileName(file));
            if (!m.Success) continue;                       // 不是本程序的每日快照：绝不碰
            if (!DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) continue;
            dated.Add((day, file));
        }

        var removed = 0;
        foreach (var (day, file) in dated.OrderByDescending(x => x.Day))
        {
            var age = (now.Date - day.Date).Days;
            var keep = age <= KeepDailyDays
                || (age <= KeepWeeklyDays && day.DayOfWeek == DayOfWeek.Sunday);
            if (keep) continue;

            try
            {
                File.Delete(file);
                removed++;
            }
            catch (Exception ex)
            {
                // 删不掉不影响正确性，只是多占点盘
                logger?.LogWarning(ex, "清理旧快照失败: {File}", file);
            }
        }

        return removed;
    }

    /// <summary>快照条目。用 record 而不是匿名类型：健康检查也要读这份清单。</summary>
    public record SnapshotItem(string Name, long Bytes, string Mtime, string Kind);

    /// <summary>备份概况的精简版，给健康检查用（它只关心"最近一份是什么时候"）。</summary>
    public record BackupSummary(bool Configured, SnapshotItem? Latest, int Count);

    public static BackupSummary Summary(string dbPath, string? backupDir)
    {
        var items = Items(dbPath, backupDir);
        var configured = !string.IsNullOrEmpty(backupDir) && Directory.Exists(backupDir);
        return new BackupSummary(configured, items.FirstOrDefault(), items.Count);
    }

    /// <summary>给「运行状态」面板看的备份清单。</summary>
    public static object Status(string dbPath, string? backupDir)
    {
        var top = Items(dbPath, backupDir);
        return new
        {
            backupDir = backupDir ?? "",
            configured = !string.IsNullOrEmpty(backupDir) && Directory.Exists(backupDir),
            latest = top.FirstOrDefault(),
            count = top.Count,
            keepDailyDays = KeepDailyDays,
            keepWeeklyDays = KeepWeeklyDays,
            items = top
        };
    }

    /// <summary>
    /// 列出自己的快照，按日期从新到旧，只取界面用得上的前 14 份。
    /// 排序用文件名而不是 mtime：名字里就是 ISO 日期，而拷贝或恢复过的文件 mtime 会变、日期不会。
    /// </summary>
    private static List<SnapshotItem> Items(string dbPath, string? backupDir)
    {
        var prefix = Path.GetFileNameWithoutExtension(dbPath);
        var items = new List<SnapshotItem>();
        if (string.IsNullOrEmpty(backupDir) || !Directory.Exists(backupDir)) return items;

        // 两种都算自己的：常规每日快照，以及带标签的那份（迁移前 / 合并前，界面分开标）
        var pattern = new Regex(
            @"^" + Regex.Escape(prefix) + @"_(?<date>\d{4}-\d{2}-\d{2})(?<tag>_[A-Za-z-]+)?\.db$");

        foreach (var file in Directory.GetFiles(backupDir, prefix + "*.db"))
        {
            var name = Path.GetFileName(file);
            var m = pattern.Match(name);
            if (!m.Success) continue;
            var fi = new FileInfo(file);
            items.Add(new SnapshotItem(
                name,
                fi.Length,
                fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                m.Groups["tag"].Success ? m.Groups["tag"].Value.TrimStart('_') : "daily"));
        }

        return items.OrderByDescending(x => x.Name, StringComparer.Ordinal).Take(14).ToList();
    }

}

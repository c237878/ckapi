using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace ckapi.Services;

/// <summary>
/// 快照：每日常规备份 + 轮转，加上界面上那四个动作（马上存一份、目录搬到别处、恢复到某一份、删一份）。
///
/// 为什么不能只在启动时备一份：这个进程经常连着跑几周不重启，"今天的快照"就永远只有
/// 重启那天那一份，出事时能回滚的点少得可怜。这里每小时醒一次看今天的快照落盘没有，
/// 没落就补一份——顺带把"半夜崩了被 launchd 拉起""长期不重启"两种情况都覆盖了。
///
/// 备份目录是跨项目共用的（旁边还放着别的项目的 log_*.db），所以**凡是碰文件的动作都只认
/// 自己那一个严格格式的文件名**：`ckplayer_yyyy-MM-dd[_标签].db`。轮转更保守，只删没标签的常规快照；
/// 带标签的（`_pre-migration`、`_pre-merge`、`_manual-2245`、`_pre-restore-2147`）都是
/// "某个不可逆动作之前的唯一凭据"，宁可能占点盘也不自动删——要删人在界面上点。
///
/// 恢复走的是 SQLite 的在线备份接口，把快照灌回**当前打开的那份库**，而不是换文件：
/// 换文件得先停服务，还得保证把 -wal / -shm 一起清掉，否则旧 WAL 会往恢复进来的库上回放，
/// 出来的东西既不等于快照也不等于现在。灌回去没这两个坑，它本身还是一个事务，中途失败就是原样。
/// 代价是恢复完的结构可能比代码旧，所以调用方紧接着要重跑一次初始化把迁移补齐（实测 11→14 是顺的）。
/// </summary>
public class BackupService : BackgroundService
{
    /// <summary>检查间隔：一天 24 次足够，且进程重启后最多一小时就补上</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>每日快照保留天数</summary>
    public const int KeepDailyDays = 14;

    /// <summary>超出 14 天后，周日那份额外保留到多少天（等于每月留一份的能力）</summary>
    public const int KeepWeeklyDays = 60;

    /// <summary>快照目录（界面上设的这一份）。没设过就用 appsettings 里的 ConnectionStrings:BackupPath</summary>
    public const string SettingDir = "backup_dir";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

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
    /// 带标签的一律不碰。抽成静态方法是为了能在副本目录上单独验证——
    /// 生产目录里混着别的项目的文件，我不想拿真目录测删除逻辑。
    /// </summary>
    public static int Rotate(string dbPath, string? backupDir, DateTime now, ILogger? logger = null)
    {
        if (string.IsNullOrEmpty(backupDir) || !Directory.Exists(backupDir)) return 0;

        var dated = new List<(DateTime Day, string Path)>();
        foreach (var file in Directory.GetFiles(backupDir, "*.db"))
        {
            var name = Path.GetFileName(file);
            var parsed = ParseName(dbPath, name);
            if (parsed is null) continue;                     // 不是本程序的快照：绝不碰
            if (HasTag(dbPath, name)) continue;               // 带标签的是不可逆动作前的凭证：也不碰
            dated.Add((parsed.Value.Day, file));
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

    // ---------------- 界面上的四个动作 ----------------

    /// <summary>
    /// 马上存一份，文件名带时刻（`..._manual-2245.db`）。
    /// 同一分钟内再点不会新建第二份——这是个天然节流，也省得人去记"我刚才是不是点过了"。
    /// </summary>
    public (bool Created, string Message, string? File) BackupNow()
    {
        var tag = $"manual-{DateTime.Now:HHmm}";
        var created = _db.BackupDatabase("手动备份", tag: tag);
        var name = _db.BuildBackupFileName(tag) + Path.GetExtension(_db.GetDbPath());
        return created
            ? (true, $"已存一份快照：{name}", name)
            : (false, $"这一分钟已经存过了：{name}", name);
    }

    /// <summary>
    /// 换快照目录：只改"以后往哪儿写"，不动已有文件。要把旧目录里的快照一起搬走用 <see cref="TransferTo"/>。
    /// 传空 = 清掉界面值、回到 appsettings 那份。
    /// </summary>
    public (bool Ok, string Message) SetDir(string? path)
    {
        var target = NormalizeDir(path);
        if (target is null && !string.IsNullOrWhiteSpace(path))
            return (false, "路径要写全（以 / 开头），而且不能是一个文件");

        if (target is not null)
        {
            try { Directory.CreateDirectory(target); }
            catch (Exception ex) { return (false, "目录建不出来：" + ex.Message); }
            if (!IsWritable(target)) return (false, "这个目录写不了（盘满、只读，或者还没挂上）");
        }

        using (var conn = _db.GetConnection())
        {
            if (target is null) Utils.SettingStore.Remove(conn, SettingDir);
            else Utils.SettingStore.Upsert(conn, SettingDir, target);
        }
        _db.SetBackupPathOverride(target);

        var message = target is null
            ? $"已回到配置文件里那份：{_db.GetDefaultBackupPath() ?? "(配置里没写)"}"
            : $"快照目录已设为 {target}";
        _logger.LogInformation("快照目录变更：{Message}", message);
        return (true, message);
    }

    /// <summary>
    /// 把已有的快照从当前目录搬到（或复制到）新目录。**只搬自己命名的那些文件**，
    /// 别的项目的一律留在原地。先复制、校验字节数一致、整批成功后才删源；
    /// 任何一条对不上就停下来报错，不搞"搬了一半"。
    /// </summary>
    public (bool Ok, string Message, int Moved, int Skipped, long Bytes) TransferTo(string? path, bool removeSource)
    {
        var from = _db.GetBackupPath();
        var to = NormalizeDir(path);
        if (to is null) return (false, "目标目录要写全（以 / 开头）", 0, 0, 0);
        if (string.IsNullOrEmpty(from)) return (false, "现在还没有快照目录，没得搬", 0, 0, 0);
        if (string.Equals(from, to, StringComparison.Ordinal))
            return (false, "目标就是当前目录，不用搬", 0, 0, 0);
        if (!Directory.Exists(from)) return (false, $"当前目录不在：{from}", 0, 0, 0);

        try { Directory.CreateDirectory(to); }
        catch (Exception ex) { return (false, "目标目录建不出来：" + ex.Message, 0, 0, 0); }
        if (!IsWritable(to)) return (false, "目标目录写不了", 0, 0, 0);

        var mine = Items(_db.GetDbPath(), from, int.MaxValue);
        if (mine.Count == 0)
            return (true, $"{from} 里没有本程序的快照，只把目录指过去了", 0, 0, 0);

        var moved = 0;
        var skipped = 0;
        var bytes = 0L;

        // 这一趟只做"复制 + 校验"，删除留到整批都成功之后：中途报错时源文件还是一个不少
        var toDelete = new List<string>();
        foreach (var item in mine)
        {
            var src = Path.Combine(from, item.Name);
            var dst = Path.Combine(to, item.Name);
            if (File.Exists(dst))
            {
                var dstLen = new FileInfo(dst).Length;
                if (dstLen == item.Bytes) { skipped++; continue; }        // 已经一模一样地在那儿了
                return (false, $"目标里有同名但字节数不同的文件：{item.Name}（{dstLen} vs {item.Bytes}）",
                    moved, skipped, bytes);
            }
            try
            {
                File.Copy(src, dst);
                if (new FileInfo(dst).Length != item.Bytes)
                    return (false, $"复制完字节数对不上：{item.Name}", moved, skipped, bytes);
                bytes += item.Bytes;
                if (removeSource) toDelete.Add(src);
                moved++;
            }
            catch (Exception ex)
            {
                return (false, $"搬 {item.Name} 失败：" + ex.Message, moved, skipped, bytes);
            }
        }

        if (removeSource)
        {
            foreach (var src in toDelete)
            {
                try { File.Delete(src); }
                catch (Exception ex) { _logger.LogWarning(ex, "旧快照删不掉（副本已经在目标目录）: {File}", src); }
            }
        }

        // 目录指针最后才改：前面任何一步没成，服务还在往旧目录写，不会出现"新目录少文件"的中间态
        var set = SetDir(to);
        if (!set.Ok) return (false, set.Message, moved, skipped, bytes);

        _logger.LogInformation(
            "快照{Mode}：{Moved} 份（{Bytes} 字节）从 {From} 到 {To}，目标里已存在的 {Skipped} 份没动",
            removeSource ? "搬" : "复制", moved, bytes, from, to, skipped);
        return (true,
            $"{(removeSource ? "搬" : "复制")}了 {moved} 份（{bytes / 1048576.0:F1} MB）"
            + (skipped > 0 ? $"，目标里已存在的 {skipped} 份没动" : "") + $"，目录现在指向 {to}",
            moved, skipped, bytes);
    }

    /// <summary>删掉某一份快照。只认自己的命名，界面上已经二次确认过才走到这儿。</summary>
    public (bool Ok, string Message) DeleteOne(string fileName)
    {
        var found = ResolveSnapshot(fileName);
        if (found is null) return (false, "这不是本程序的快照文件名，或者文件不在");
        try
        {
            File.Delete(found);
            _logger.LogWarning("删掉快照：{File}", found);
            return (true, $"已删掉 {Path.GetFileName(found)}");
        }
        catch (Exception ex)
        {
            return (false, "删不掉：" + ex.Message);
        }
    }

    /// <summary>
    /// 恢复到指定快照：先给当前库存一份 pre-restore 凭证，再把快照灌回当前库。
    /// 这一步会**丢掉那份快照之后写进去的所有东西**（点赞、进度、新录的片都在内），
    /// 所以界面要二次确认，这里也顺手把前后各表的数量回给界面，让人看得见换成了什么。
    /// </summary>
    public (bool Ok, string Message, RestoreInfo? Info) RestoreFrom(string fileName)
    {
        var snapshot = ResolveSnapshot(fileName);
        if (snapshot is null) return (false, "这不是本程序的快照文件名，或者文件不在", null);

        // 快照自己先过一遍完整性检查：那种拷贝途中断掉的半截文件，灌回去就是把库搞坏
        Dictionary<string, int> fromCounts;
        try
        {
            using var check = new SqliteConnection($"Data Source={snapshot};Mode=ReadOnly");
            check.Open();
            var integrity = Scalar(check, "PRAGMA integrity_check")?.ToString();
            if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
                return (false, $"这份快照自己就不干净（integrity_check 回的是「{integrity}」），别拿它恢复", null);

            fromCounts = Counts(check);
            if (!fromCounts.ContainsKey("videos"))
                return (false, "这份快照里没有 videos 表，不是本程序的库", null);
        }
        catch (Exception ex)
        {
            return (false, "读不开这份快照：" + ex.Message, null);
        }

        var before = CountsOfLive();

        // 留凭证：这一份是"恢复之前的现在"，恢复错了还回得去
        var rescueTag = $"pre-restore-{DateTime.Now:HHmm}";
        if (!_db.BackupDatabase("恢复前", force: true, tag: rescueTag))
            return (false, "没能给当前库留下恢复前的凭证，中止——不想让你在没有退路的情况下回滚", null);
        var rescueFile = _db.BuildBackupFileName(rescueTag) + Path.GetExtension(_db.GetDbPath());

        try
        {
            using var src = new SqliteConnection($"Data Source={snapshot};Mode=ReadOnly");
            src.Open();
            using var dst = _db.GetConnection();
            src.BackupDatabase(dst);
            // 灌完立刻把 WAL 收干净：留在 -wal 里的是"恢复前"那些页的旧帧
            Scalar(dst, "PRAGMA wal_checkpoint(TRUNCATE)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "恢复快照失败：{File}", snapshot);
            return (false, $"灌回当前库时失败（数据没动，恢复前的凭证在 {rescueFile}）：" + ex.Message, null);
        }

        _logger.LogWarning(
            "已按 {File} 恢复：videos {BV}→{AV}、video_likes {BL}→{AL}；恢复前的库存在 {Rescue}",
            Path.GetFileName(snapshot), before.Videos, fromCounts.GetValueOrDefault("videos"),
            before.Likes, fromCounts.GetValueOrDefault("video_likes"), rescueFile);

        // 这里报的是"快照里原本有什么"，不是恢复完的最终样子：老快照要补结构（比如 v11 建版本行），
        // 补完行数还会变，所以最终数字由调用方在 Initialize 之后另读一次
        var info = new RestoreInfo(
            Path.GetFileName(snapshot), rescueFile,
            before.Videos, before.Files, before.Likes,
            fromCounts.GetValueOrDefault("videos"), fromCounts.GetValueOrDefault("video_likes"),
            // 老快照压根没有这两张表，界面上要能说清"不是删了，是那时候还没有"
            fromCounts.ContainsKey("video_files"), fromCounts.ContainsKey("file_state"));
        return (true, $"已按 {Path.GetFileName(snapshot)} 恢复", info);
    }

    /// <summary>
    /// 恢复这一步换掉了什么。*Before 是点恢复那一刻的当前库，*InSnapshot 是那份快照自己的样子。
    /// </summary>
    public record RestoreInfo(
        string Snapshot, string PreRestoreFile,
        int VideosBefore, int FilesBefore, int LikesBefore,
        int VideosInSnapshot, int LikesInSnapshot,
        bool SnapshotHasFiles, bool SnapshotHasStates);

    /// <summary>当前库的三张关键表行数。控制器在补完结构后再读一次，界面才能报"最后是几个"。</summary>
    public record LiveCounts(int Videos, int Files, int Likes);

    public LiveCounts Now()
    {
        using var conn = _db.GetConnection();
        var c = Counts(conn);
        return new LiveCounts(
            c.GetValueOrDefault("videos"), c.GetValueOrDefault("video_files"), c.GetValueOrDefault("video_likes"));
    }

    private LiveCounts CountsOfLive() => Now();

    // ---------------- 共用的文件名与目录活儿 ----------------

    /// <summary>
    /// 认文件名并给出绝对路径，三道关：名字格式对、路径跑不出快照目录、文件真在。
    /// 界面上能挑的文件就是这里认下的文件；反过来说，任何拼出来的路径过不了这三关都当没看见。
    /// </summary>
    private string? ResolveSnapshot(string fileName)
    {
        var dir = _db.GetBackupPath();
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;

        var name = Path.GetFileName(fileName ?? "");        // 顺手把任何目录部分削掉
        if (ParseName(_db.GetDbPath(), name) is null) return null;

        var root = Path.GetFullPath(dir) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(dir, name));
        if (!full.StartsWith(root, StringComparison.Ordinal)) return null;
        return File.Exists(full) ? full : null;
    }

    /// <summary>是不是带标签的快照（这类不自动删，只由人在界面上删）</summary>
    private static bool HasTag(string dbPath, string fileName) => Match(fileName, dbPath)?.tag.Length > 1;

    /// <summary>
    /// 解析快照文件名：&lt;库名&gt;_yyyy-MM-dd[_标签].db，不是本程序的快照返回 null。
    /// 标签允许数字，因为界面上存的是带时刻的 manual-2245 / pre-restore-2147。
    /// </summary>
    private static (DateTime Day, string Tag)? ParseName(string dbPath, string fileName)
    {
        var m = Match(fileName, dbPath);
        if (m is null) return null;
        if (!DateTime.TryParseExact(m.Value.date, "yyyy-MM-dd", Inv, DateTimeStyles.None, out var day)) return null;
        return (day, m.Value.tag.TrimStart('_'));
    }

    private static (string date, string tag)? Match(string fileName, string dbPath)
    {
        var pattern = "^" + Regex.Escape(Path.GetFileNameWithoutExtension(dbPath))
            + @"_(?<date>\d{4}-\d{2}-\d{2})(?<tag>_[A-Za-z0-9-]+)?\.db$";
        var m = Regex.Match(fileName, pattern, RegexOptions.IgnoreCase);
        return m.Success ? (m.Groups["date"].Value, m.Groups["tag"].Value) : null;
    }

    /// <summary>目录路径收拾：空 → null（"用配置默认"）；相对路径或指向文件 → null</summary>
    private static string? NormalizeDir(string? path)
    {
        var p = (path ?? "").Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(p)) return null;
        if (!Path.IsPathRooted(p) || System.IO.File.Exists(p)) return null;
        return p;
    }

    /// <summary>能不能写：建一个临时文件再删。只判断目录，不碰里面的内容。</summary>
    private static bool IsWritable(string dir)
    {
        var probe = Path.Combine(dir, $".wprobe-{Environment.ProcessId}-{Guid.NewGuid():N}");
        try
        {
            System.IO.File.WriteAllText(probe, "1");
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { if (System.IO.File.Exists(probe)) System.IO.File.Delete(probe); } catch { /* 探活留下的垃圾删不掉就算了 */ }
        }
    }

    /// <summary>
    /// 几张关键表的行数。老快照里没有 video_files / file_state 这些新表，
    /// 所以逐张单独查、查不到就不算它——不能因为快照旧就在恢复那一步报错。
    /// </summary>
    private static Dictionary<string, int> Counts(SqliteConnection conn)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var table in new[] { "videos", "video_files", "video_likes", "file_state", "actors" })
        {
            try
            {
                var name = Scalar(conn, "SELECT name FROM sqlite_master WHERE type='table' AND name=@t",
                    new SqliteParameter("@t", table))?.ToString();
                if (string.IsNullOrEmpty(name)) continue;
                result[table] = Convert.ToInt32(Scalar(conn, $"SELECT COUNT(*) FROM [{table}]") ?? 0);
            }
            catch
            {
                // 单张表查不动就当它不存在，恢复流程不该因此卡住
            }
        }
        return result;
    }

    private static object? Scalar(SqliteConnection conn, string sql, params SqliteParameter[] ps)
    {
        using var cmd = new SqliteCommand(sql, conn);
        if (ps.Length > 0) cmd.Parameters.AddRange(ps);
        return cmd.ExecuteScalar();
    }

    /// <summary>快照条目。用 record 而不是匿名类型：Status 要排序取前 14 条，匿名类型跨不了那道。</summary>
    public record SnapshotItem(string Name, long Bytes, string Mtime, string Kind);

    /// <summary>备份概况的精简版，给健康检查用（它只关心"最近一份是什么时候"）。</summary>
    public record BackupSummary(bool Configured, SnapshotItem? Latest, int Count);

    public static BackupSummary Summary(string dbPath, string? backupDir)
    {
        var items = Items(dbPath, backupDir, 14);
        var configured = !string.IsNullOrEmpty(backupDir) && Directory.Exists(backupDir);
        return new BackupSummary(configured, items.FirstOrDefault(), items.Count);
    }

    /// <summary>
    /// 给「运行状态」面板看的备份清单。目录、条数、总字节都从"当前生效的那份"读，
    /// 因为面板上还要改它——静态方法拿不到界面上刚设的那个目录。
    /// </summary>
    public object Status()
    {
        var dir = _db.GetBackupPath();
        var shown = Items(_db.GetDbPath(), dir, 14);
        var all = Items(_db.GetDbPath(), dir, int.MaxValue);
        bool fromSettings;
        using (var conn = _db.GetConnection())
            fromSettings = !string.IsNullOrEmpty(Utils.SettingStore.Read(conn, SettingDir));

        return new
        {
            backupDir = dir ?? "",
            configured = !string.IsNullOrEmpty(dir) && Directory.Exists(dir),
            // 界面要说清现在生效的是界面值还是配置文件里那份，并给一个"退回默认"的口子
            fromSettings,
            defaultDir = _db.GetDefaultBackupPath() ?? "",
            latest = shown.FirstOrDefault(),
            count = all.Count,
            totalBytes = all.Sum(x => x.Bytes),
            keepDailyDays = KeepDailyDays,
            keepWeeklyDays = KeepWeeklyDays,
            items = shown
        };
    }

    /// <summary>
    /// 列出自己的快照，按名字从新到旧。界面取最近 14 条，统计条数与总体积时传 int.MaxValue。
    /// 排序用文件名而不是 mtime：名字里就是 ISO 日期和时刻，而拷贝或恢复过的文件 mtime 会变、名字不会。
    /// </summary>
    private static List<SnapshotItem> Items(string dbPath, string? backupDir, int limit)
    {
        var items = new List<SnapshotItem>();
        if (string.IsNullOrEmpty(backupDir) || !Directory.Exists(backupDir)) return items;

        var prefix = Path.GetFileNameWithoutExtension(dbPath);
        foreach (var file in Directory.GetFiles(backupDir, prefix + "*.db"))
        {
            var name = Path.GetFileName(file);
            var parsed = ParseName(dbPath, name);
            if (parsed is null) continue;                     // 别的项目的文件（log_*.db）前缀就不对
            var fi = new FileInfo(file);
            items.Add(new SnapshotItem(
                name,
                fi.Length,
                fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                string.IsNullOrEmpty(parsed.Value.Tag) ? "daily" : parsed.Value.Tag));
        }

        return items.OrderByDescending(x => x.Name, StringComparer.Ordinal).Take(limit).ToList();
    }
}

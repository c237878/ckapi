using Microsoft.Data.Sqlite;

namespace ckapi.Services;

/// <summary>
/// 数据服务接口
/// </summary>
public interface IDataService
{
    /// <summary>
    /// 初始化数据库和表结构
    /// </summary>
    void Initialize();
}

/// <summary>
/// 数据服务实现 - 负责项目启动时的数据库初始化
///
/// 结构说明：
///   1. CreateBaseTables 是"当前完整结构"的唯一权威声明，全新库只执行它。
///   2. Migrations 是历史库的追赶路径，按 PRAGMA user_version 逐级向前。
///   3. 两者必须保持等价：新库跑完 == 老库迁移完。改结构时同时改两处。
/// </summary>
public class DataService : IDataService
{
    /// <summary>Migrations 数组的最高版本号；新增迁移步骤时 +1。</summary>
    private const int TargetVersion = 8;

    /// <summary>
    /// 历史库追赶路径。键为"应用此步骤后达到的版本"，只执行 user_version 之下的步骤。
    /// 在构造函数里赋值：v2 步骤要用实例日志器，而字段初始化器不能引用实例成员。
    ///
    /// 注意：v2 删掉的列绝不能在这里再 AddColumnIfMissing —— 从旧备份恢复出的 version=0 库
    /// 会把整个列表重放一遍，那样刚删的列又会长回来。
    /// </summary>
    private readonly (int Version, string Name, Action<SqliteConnection> Up)[] Migrations;

    private static readonly (int Version, string Name, Action<SqliteConnection> Up)[] AdditiveMigrations =
    {
        (1, "补齐历史库缺失列（comics.status / scan_directories.category / video_likes.target_type 等）", c =>
        {
            // media_attr_flags 在 v6 被拆掉，但这里必须留着：从 version=0 的旧备份重放时，
            // v6 的值映射要读它，少了这一行老库就没有这一列可搬。
            AddColumnIfMissing(c, "videos", "media_attr_flags", "INTEGER DEFAULT 0");
            AddColumnIfMissing(c, "videos", "sort_order", "INTEGER DEFAULT 0");
            AddColumnIfMissing(c, "videos", "code", "TEXT");
            AddColumnIfMissing(c, "videos", "country", "TEXT DEFAULT ''");
            AddColumnIfMissing(c, "videos", "seriesid", "TEXT");
            AddColumnIfMissing(c, "actors", "alias", "TEXT");
            AddColumnIfMissing(c, "actors", "country", "TEXT");
            AddColumnIfMissing(c, "comics", "status", "INTEGER DEFAULT 0");
            AddColumnIfMissing(c, "scan_directories", "category", "TEXT DEFAULT ''");
            AddColumnIfMissing(c, "video_likes", "target_type", "TEXT NOT NULL DEFAULT 'video'");
            // 原本这里还有 comic_chapters.utime 与 scan_directories.auto_create_series 两行，
            // v2 已把这两列删掉；若保留，从旧备份恢复出的 version=0 库重放时会把它们加回来。
        }),
    };

    /// <summary>
    /// 删除经全仓取证确认无任何引用的列与表。
    ///
    /// 每一项都满足：数据侧 100% 为空或行数为零，代码侧无 INSERT/UPDATE/按名读取，
    /// 且不在任何索引里（在索引里的列 SQLite 会直接拒绝 DROP，无法静默通过）。
    ///
    ///   videos.added_at                       被 ctime 取代（见提交 d9c85ce），4774/4774 为空
    ///   videos.utime                          无任何写入路径，4774/4774 为空
    ///   actors.avatar_path                    1612/1612 为空；演员照片实际走 posterDir 文件墙，与此列无关
    ///   comic_chapters.utime                  只写不读，且不在 INSERT 列表里（17/27 为 NULL）
    ///   scan_directories.recursive            a18fc7c 删除扫描器后无人执行，4/4 为 0
    ///   scan_directories.auto_create_series   同上
    ///   video_types (表)                      0 行，两个仓库零引用
    ///   scan_tasks (表)                       死扫描器的 6 行日志，零引用；删前转存到备份目录
    ///   system_settings 的 daily_recommend_%  缓存改为内存后遗留的孤儿键（f6b17ce）
    ///
    /// 刻意保留：friend_links.logo（数据全空但读写与 UI 都活着）、
    /// video_likes.target_type（区分影片/漫画点赞，且在索引里）、videos.sort_order（系列内排序，在索引里）、
    /// video_series.ctime/utime（NOT NULL 且 PUT /series 会把客户端回传值直接写库）。
    /// </summary>
    private void DropDeadFields(SqliteConnection conn)
    {
        DumpTableBeforeDrop(conn, "scan_tasks");

        var dropColumns = new (string Table, string Column)[]
        {
            ("videos", "added_at"),
            ("videos", "utime"),
            ("actors", "avatar_path"),
            ("comic_chapters", "utime"),
            ("scan_directories", "recursive"),
            ("scan_directories", "auto_create_series"),
        };

        foreach (var (table, column) in dropColumns)
        {
            if (!ColumnExists(conn, table, column)) continue;
            NonQuery(conn, $"ALTER TABLE [{table}] DROP COLUMN [{column}]");
            _logger.LogInformation("已删除列 [{Table}].[{Column}]", table, column);
        }

        foreach (var table in new[] { "video_types", "scan_tasks" })
        {
            if (!TableExists(conn, table)) continue;
            NonQuery(conn, $"DROP TABLE [{table}]");
            _logger.LogInformation("已删除遗留表 [{Table}]", table);
        }

        var removed = NonQuery(conn, "DELETE FROM system_settings WHERE name LIKE 'daily_recommend_%'");
        if (removed > 0)
            _logger.LogInformation("已清理 {Count} 个 daily_recommend_* 孤儿设置行", removed);
    }

    /// <summary>
    /// actors.alias（空格分隔）→ actor_aliases 一行一个。
    ///
    /// 拆分规则：按空白切、去空、去重、丢掉与本人姓名相同的项、单项最长 60 字。
    /// 实测 1342 条别名只用空格分隔（无 、，/ 等），所以按空白切不会误拆中文名；
    /// 唯一有歧义的是含拉丁字母的 30 条（如「志保 Shiho」是一个别名还是两个），
    /// 这类整条记进日志当复核清单，人工在界面上修——写启发式规则不如人眼一遍。
    /// </summary>
    private void NormalizeActorAliases(SqliteConnection conn)
    {
        if (!ColumnExists(conn, "actors", "alias"))
        {
            _logger.LogInformation("actors.alias 已不存在，跳过别名规范化");
            return;
        }

        var rows = new List<(string Id, string Name, string Raw)>();
        using (var read = new SqliteCommand(
                   "SELECT id, name, alias FROM actors WHERE alias IS NOT NULL AND TRIM(alias) <> ''", conn))
        using (var reader = read.ExecuteReader())
        {
            while (reader.Read())
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        var inserted = 0;
        var review = new List<string>();

        using (var tx = conn.BeginTransaction())
        {
            using (var del = new SqliteCommand("DELETE FROM actor_aliases", conn, tx))
                del.ExecuteNonQuery();

            using var ins = new SqliteCommand(
                "INSERT OR IGNORE INTO actor_aliases (actor_id, alias) VALUES (@id, @alias)", conn, tx);

            foreach (var (id, name, raw) in rows)
            {
                // 含拉丁字母的那批拆出来可能有歧义（"志保 Shiho"是一个别名还是两个），记进日志复核
                if (raw.Any(char.IsAsciiLetter))
                    review.Add($"{name} | {raw}");

                foreach (var alias in Utils.Aliases.Normalize(new[] { raw }, name))
                {
                    ins.Parameters.Clear();
                    ins.Parameters.Add(new SqliteParameter("@id", id));
                    ins.Parameters.Add(new SqliteParameter("@alias", alias));
                    inserted += ins.ExecuteNonQuery();
                }
            }

            tx.Commit();
        }

        NonQuery(conn, "ALTER TABLE actors DROP COLUMN alias");

        _logger.LogInformation(
            "别名规范化完成：{Actors} 位演员 → {Rows} 行 actor_aliases，已删除 actors.alias 列",
            rows.Count, inserted);

        if (review.Count > 0)
            _logger.LogInformation(
                "以下 {Count} 位演员的别名含拉丁字母，按空格拆分可能有歧义，请在界面上复核：\n{List}",
                review.Count, string.Join("\n", review));
    }

    /// <summary>
    /// 把塞在 actors.bio 里的外链拆到 actor_links。
    ///
    /// 实测 382 位演员的 bio 含 URL，抽出 528 条；其中 379 条抽掉 URL 后什么都不剩
    /// （它们被当成了链接容器，不是简介），只有 3 条还有正文，那些正文原样留在 bio 里。
    /// 幂等：先清表再抽，重放不会产生重复行。
    /// </summary>
    private void ExtractActorLinks(SqliteConnection conn)
    {
        var rows = new List<(string Id, string Bio)>();
        using (var read = new SqliteCommand(
                   "SELECT id, bio FROM actors WHERE bio IS NOT NULL AND bio LIKE '%http%'", conn))
        using (var reader = read.ExecuteReader())
        {
            while (reader.Read())
                rows.Add((reader.GetString(0), reader.IsDBNull(1) ? "" : reader.GetString(1)));
        }

        var linkRows = 0;
        var clearedBio = 0;
        var keptBio = 0;

        using (var tx = conn.BeginTransaction())
        {
            using (var del = new SqliteCommand("DELETE FROM actor_links", conn, tx))
                del.ExecuteNonQuery();

            using var ins = new SqliteCommand(
                "INSERT INTO actor_links (id, actor_id, kind, url) VALUES (@id, @actorId, @kind, @url)", conn, tx);
            using var upd = new SqliteCommand("UPDATE actors SET bio = @bio WHERE id = @id", conn, tx);

            foreach (var (id, bio) in rows)
            {
                var (found, rest) = Utils.Links.ExtractFromBio(bio);
                if (found.Count == 0) continue;

                foreach (var link in found)
                {
                    ins.Parameters.Clear();
                    ins.Parameters.Add(new SqliteParameter("@id", Guid.NewGuid().ToString("N").ToUpper()));
                    ins.Parameters.Add(new SqliteParameter("@actorId", id));
                    ins.Parameters.Add(new SqliteParameter("@kind", link.Kind));
                    ins.Parameters.Add(new SqliteParameter("@url", link.Url));
                    linkRows += ins.ExecuteNonQuery();
                }

                upd.Parameters.Clear();
                upd.Parameters.Add(new SqliteParameter("@bio", string.IsNullOrEmpty(rest) ? DBNull.Value : (object)rest));
                upd.Parameters.Add(new SqliteParameter("@id", id));
                upd.ExecuteNonQuery();
                if (string.IsNullOrEmpty(rest)) clearedBio++; else keptBio++;
            }

            tx.Commit();
        }

        _logger.LogInformation(
            "外链拆分完成：{Actors} 位演员 → {Rows} 行 actor_links（{Cleared} 条简介清空、{Kept} 条保留正文）",
            rows.Count, linkRows, clearedBio, keptBio);
    }

    /// <summary>
    /// actors 加 birthdate 列。
    ///
    /// 新列必须走迁移：CreateBaseTables 里的 CREATE TABLE IF NOT EXISTS 对已存在的表不做任何事，
    /// 历史库不会凭空长出这一列。
    /// 存 ISO 文本（1987-05-16），只到月的存 1987-05 —— 补成 01 号是造假数据；
    /// 没查到的就留 NULL，不用空串占位。
    /// </summary>
    private void AddActorBirthdate(SqliteConnection conn)
    {
        if (ColumnExists(conn, "actors", "birthdate"))
        {
            _logger.LogInformation("actors.birthdate 已存在，跳过");
            return;
        }

        NonQuery(conn, "ALTER TABLE actors ADD COLUMN birthdate TEXT");
        _logger.LogInformation("已为 actors 添加 birthdate 列");
    }

    /// <summary>
    /// 把复合的 videos.media_attr_flags 拆成两个独立维度，并给分辨率留出位置。
    ///
    /// 旧口径是一根一维的梯子：劣质 &lt; 无字幕 &lt; 完美。前两格说的其实是互不相干的两件事
    /// （画面干不干净 / 字幕在不在），挤在一格里既写不清也筛不准。拆成：
    ///
    ///   subtitle_state：unknown 未标 · none 发行版本就没字幕 · missing 这片有字幕但这份没带上 · has 有字幕
    ///   watermark_state：unknown 未标 · none 没有台标与广告 · light 角落台标 · heavy 满屏广告
    ///
    /// 只搬"确定说得通"的值：2 无字幕 → missing（旧的长文案就是"缺少字幕文件"），3 完美 → has。
    /// 1 劣质在新口径里没有对应维度（它说的是清晰度，而清晰度这次由扫描客观量出来），
    /// 所以两个状态都留 unknown，不去猜它当年指的是广告还是糊。
    ///
    /// 这里曾经还多建了一列 watched 来存"非 0 就是看过"的暗号，v7 又删掉了：
    /// 扫描只写分辨率之后，两个状态有结论只可能是人工标的，直接推导即可，不必存两份会分叉的事实。
    /// </summary>
    private void SplitSourceAttributes(SqliteConnection conn)
    {
        AddColumnIfMissing(conn, "videos", "subtitle_state", "TEXT NOT NULL DEFAULT 'unknown'");
        AddColumnIfMissing(conn, "videos", "watermark_state", "TEXT NOT NULL DEFAULT 'unknown'");
        AddColumnIfMissing(conn, "videos", "watched", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(conn, "videos", "res_w", "INTEGER");
        AddColumnIfMissing(conn, "videos", "res_h", "INTEGER");
        AddColumnIfMissing(conn, "videos", "scan_time", "TEXT");

        if (!ColumnExists(conn, "videos", "media_attr_flags"))
        {
            _logger.LogInformation("videos.media_attr_flags 已不存在，跳过值映射");
            return;
        }

        var byFlag = new Dictionary<int, int>();
        using (var read = new SqliteCommand(
                   "SELECT IFNULL(media_attr_flags, 0) AS flag, COUNT(*) AS n FROM videos GROUP BY flag", conn))
        using (var reader = read.ExecuteReader())
        {
            while (reader.Read()) byFlag[reader.GetInt32(0)] = reader.GetInt32(1);
        }

        NonQuery(conn, "UPDATE videos SET watched = 1 WHERE IFNULL(media_attr_flags, 0) <> 0");
        NonQuery(conn, "UPDATE videos SET subtitle_state = 'missing' WHERE media_attr_flags = 2");
        NonQuery(conn, "UPDATE videos SET subtitle_state = 'has' WHERE media_attr_flags = 3");
        NonQuery(conn, "ALTER TABLE videos DROP COLUMN media_attr_flags");

        int Of(int flag) => byFlag.TryGetValue(flag, out var n) ? n : 0;
        _logger.LogInformation(
            "片源标记拆分完成：无字幕 {Missing} 条 → subtitle_state=missing，完美 {Has} 条 → has，" +
            "劣质 {Poor} 条不映射（清晰度改由扫描量），未标记 {Blank} 条两维均为 unknown；media_attr_flags 列已删除",
            Of(2), Of(3), Of(1), Of(0));
    }

    /// <summary>
    /// 删掉 v6 顺手加的 videos.watched。
    ///
    /// 加它是因为旧代码拿"media_attr_flags 非 0"当"看过"用，拆成两维后怕推不回来：
    /// 那时扫描还会顺带把字幕填成 has，一扫完全站就都成"有结论"了，今日推荐没有片可推。
    /// 现在扫描只写分辨率，两个状态只要有一个不是 unknown 就必定是人工标的，
    /// 直接推导即可（见 Utils.SourceStates.Unrated），存一份副本只会和真值分叉。
    ///
    /// 代价说清楚：v6 里那 300 条"劣质"两维都是 unknown，从此按"没看过"算，
    /// 会重新进今日推荐——按新口径它们确实还没给过结论，等再看到时顺手按两维标一次。
    /// </summary>
    private void DropWatchedFlag(SqliteConnection conn)
    {
        if (!ColumnExists(conn, "videos", "watched"))
        {
            _logger.LogInformation("videos.watched 已不存在，跳过");
            return;
        }

        var rated = Convert.ToInt32(Scalar(conn, "SELECT COUNT(*) FROM videos WHERE watched = 1") ?? 0);
        NonQuery(conn, "ALTER TABLE videos DROP COLUMN watched");
        _logger.LogInformation(
            "已删除 videos.watched 列（{Rated} 条曾标过看过）：改由两维是否均未标记推导，" +
            "其中两维都是 unknown 的那些会重新按没看过参与推荐", rated);
    }

    /// <summary>
    /// videos 加 original_name（日文原名）与 release_date（发行日期）。
    ///
    /// 现有的 name 其实是**中文译名**——它一直是显示标题，不改名（牵动一片代码，收益只是命名好看），
    /// 语义在界面与注释里写清楚。原名与发行日期由人填，将来接抓取通道时同一个入口写回。
    ///
    /// release_date 沿用 actors.birthdate 那套口径：只收 `YYYY` / `YYYY-MM` / `YYYY-MM-DD`，
    /// 站点只给到月份就存 `2024-03`，**不补成 01 号**（补出来的是假数据），其余一律 NULL。
    /// 校验在控制器里（NormalizeReleaseDate）。
    ///
    /// 这一版只加两列：片商 / 标签 / 关联分组 / 影片外链四组表是纯新增，
    /// 任何库启动都会走 CreateBaseTables 建出来，没有数据要搬，所以不进迁移。
    /// </summary>
    private void AddVideoTitles(SqliteConnection conn)
    {
        AddColumnIfMissing(conn, "videos", "original_name", "TEXT");
        AddColumnIfMissing(conn, "videos", "release_date", "TEXT");
        _logger.LogInformation("已为 videos 添加 original_name / release_date 列");
    }

    private readonly ILogger<DataService> _logger;
    private readonly Utils.SQLiteHelper _db;

    public DataService(ILogger<DataService> logger, Utils.SQLiteHelper db)
    {
        _logger = logger;
        _db = db;

        Migrations = AdditiveMigrations
            .Append((2, "移除零引用的列与遗留表（见 DropDeadFields 注释）", DropDeadFields))
            .Append((3, "演员别名规范化为 actor_aliases（见 NormalizeActorAliases 注释）", NormalizeActorAliases))
            .Append((4, "演员外链从 bio 里拆到 actor_links（见 ExtractActorLinks 注释）", ExtractActorLinks))
            .Append((5, "演员加出生日期列 birthdate（见 AddActorBirthdate 注释）", AddActorBirthdate))
            .Append((6, "片源复合标记拆成字幕 + 广告水印两维，加分辨率列（见 SplitSourceAttributes 注释）", SplitSourceAttributes))
            .Append((7, "删掉 videos.watched，看过与否改由两维是否均未标记推导（见 DropWatchedFlag 注释）", DropWatchedFlag))
            .Append((8, "影片加日文原名与发行日期列（见 AddVideoTitles 注释）", AddVideoTitles))
            .ToArray();
    }

    /// <summary>
    /// 初始化数据库和表结构
    /// </summary>
    public void Initialize()
    {
        _logger.LogInformation("开始初始化数据库...");

        try
        {
            using var conn = _db.GetConnection();
            conn.Open();

            var isFresh = !TableExists(conn, "videos");

            // 结构迁移前必须留一份即时快照，且不能被"当天已有常规快照"顶掉：
            // 实测过一次 07:42 的常规快照让 22:32 的迁移跳过了备份，纯属侥幸。
            var pendingMigration = !isFresh && GetVersion(conn) < TargetVersion;
            _db.BackupDatabase(pendingMigration ? "结构迁移前" : "启动",
                force: pendingMigration,
                tag: pendingMigration ? "pre-migration" : null);

            CreateBaseTables(conn);

            if (isFresh)
            {
                SetVersion(conn, TargetVersion);
                _logger.LogInformation("全新数据库，直接标记为版本 {Version}", TargetVersion);
            }
            else
            {
                RunMigrations(conn);
            }

            SeedTaxonomy(conn);

            CreateIndexes(conn);
            Analyze(conn);

            _logger.LogInformation(
                "数据库初始化完成，schema 版本 {Version}，路径 {DbPath}",
                GetVersion(conn), _db.GetDbPath());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "数据库初始化失败");
            throw;
        }
    }

    // ---------------------------------------------------------------- 迁移执行

    private void RunMigrations(SqliteConnection conn)
    {
        var current = GetVersion(conn);
        if (current >= TargetVersion) return;

        foreach (var (version, name, up) in Migrations)
        {
            if (version <= current) continue;

            _logger.LogInformation("应用迁移 {From} -> {To}: {Name}", current, version, name);
            try
            {
                up(conn);
                SetVersion(conn, version);
                current = version;
            }
            catch (Exception ex)
            {
                // 不吞掉：结构没对齐就继续跑，后面每个查询都会以难懂的方式失败
                _logger.LogError(ex, "迁移 {Version}（{Name}）失败，中止初始化", version, name);
                throw;
            }
        }
    }

    private static int GetVersion(SqliteConnection conn)
        => Convert.ToInt32(Scalar(conn, "PRAGMA user_version"));

    private static void SetVersion(SqliteConnection conn, int version)
        => NonQuery(conn, $"PRAGMA user_version = {version}");

    private static void Analyze(SqliteConnection conn) => NonQuery(conn, "ANALYZE");

    // ---------------------------------------------------------------- 基线结构

    private static void CreateBaseTables(SqliteConnection conn)
    {
        NonQuery(conn, @"
            CREATE TABLE IF NOT EXISTS videos (
                id              TEXT    PRIMARY KEY,
                name            TEXT    NOT NULL,
                category        TEXT    NOT NULL,
                file_path       TEXT,
                file_size       INTEGER,
                cover_path      TEXT,
                code            TEXT,
                country         TEXT DEFAULT '',
                seriesid        TEXT,
                ctime           TEXT,
                sort_order      INTEGER DEFAULT 0,
                subtitle_state  TEXT    NOT NULL DEFAULT 'unknown',
                watermark_state TEXT    NOT NULL DEFAULT 'unknown',
                res_w           INTEGER,
                res_h           INTEGER,
                scan_time       TEXT,
                original_name   TEXT,
                release_date    TEXT
            )");

        NonQuery(conn, @"
            CREATE TABLE IF NOT EXISTS actors (
                id         TEXT    PRIMARY KEY,
                name       TEXT    UNIQUE NOT NULL,
                bio        TEXT,
                ctime      TEXT,
                country    TEXT,
                birthdate  TEXT
            )");

        // 曾用名一条一行。原先全塞在 actors.alias 里用空格分隔，
        // 既没法精确检索一个曾用名，也没法区分"志保 Shiho"是一个别名还是两个
        NonQuery(conn, @"
            CREATE TABLE IF NOT EXISTS actor_aliases (
                actor_id TEXT NOT NULL,
                alias    TEXT NOT NULL,
                PRIMARY KEY (actor_id, alias)
            )");

        // 外链一条一行：kind 是白名单枚举（见 Utils/Links），url 只允许 http/https。
        // 以前这些地址混在 bio 里，既点不了也让简介显得很乱
        NonQuery(conn, @"
            CREATE TABLE IF NOT EXISTS actor_links (
                id       TEXT    PRIMARY KEY,
                actor_id TEXT    NOT NULL,
                kind     TEXT    NOT NULL,
                url      TEXT    NOT NULL
            )");

        // 演员图片的元数据：文件名/尺寸/主图标记，一行一张。
        // 建这张表就是为了不再"每次打开详情页扫一遍目录"——扫描由界面上的同步动作触发（见 ActorController.SyncImages）。
        // 不加版本号迁移：纯新增表没有数据要搬，全新库与历史库都会走 CreateBaseTables 这条同样的路。
        NonQuery(conn, @"
            CREATE TABLE IF NOT EXISTS actor_images (
                actor_id   TEXT    NOT NULL,
                file_name  TEXT    NOT NULL,
                is_primary INTEGER NOT NULL DEFAULT 0,
                width      INTEGER,
                height     INTEGER,
                size       INTEGER NOT NULL DEFAULT 0,
                mtime      TEXT,
                ctime      TEXT,
                PRIMARY KEY (actor_id, file_name)
            )");

        // 艳图池（<艳图目录>/default/）的图片清单，与 actor_images 同一套同步口径，
        // 只是这一池没有归属列，所以文件名本身就是主键。
        NonQuery(conn, @"
            CREATE TABLE IF NOT EXISTS highlight_images (
                file_name TEXT    PRIMARY KEY,
                width     INTEGER,
                height    INTEGER,
                size      INTEGER NOT NULL DEFAULT 0,
                mtime     TEXT,
                ctime     TEXT
            )");

        NonQuery(conn, @"
            CREATE TABLE IF NOT EXISTS video_actors (
                video_id TEXT,
                actor_id TEXT,
                PRIMARY KEY (video_id, actor_id),
                FOREIGN KEY (video_id) REFERENCES videos(id),
                FOREIGN KEY (actor_id) REFERENCES actors(id)
            )");

        // ---------------------------------------------------------------- 片商

        // 片商是实体不是字符串：「マドンナ / Madonna / 麦当娜」是同一家，存进 videos 的一列文本
        // 就会变成用字符串当外键，按片商浏览得靠模糊匹配。别名走 studio_aliases，与演员曾用名同一套做法。
        NonQuery(conn, @"
            CREATE TABLE IF NOT EXISTS studios (
                id      TEXT    NOT NULL PRIMARY KEY,
                name    TEXT    NOT NULL UNIQUE,
                country TEXT,
                link    TEXT,
                ctime   TEXT,
                utime   TEXT
            )");

        NonQuery(conn, @"
            CREATE TABLE IF NOT EXISTS studio_aliases (
                studio_id TEXT NOT NULL,
                alias     TEXT NOT NULL,
                PRIMARY KEY (studio_id, alias)
            )");

        // 多对多：一部片常常是"制作商 A + 发行商 B"两家（TMDB 也是 M:N）。
        // role 留空表示不区分，只有 maker / label 两种取值有意义。
        NonQuery(conn, @"
            CREATE TABLE IF NOT EXISTS video_studios (
                video_id  TEXT NOT NULL,
                studio_id TEXT NOT NULL,
                role      TEXT NOT NULL DEFAULT '',
                PRIMARY KEY (video_id, studio_id)
            )");

        // ---------------------------------------------------------------- 题材标签

        NonQuery(conn, @"
            CREATE TABLE IF NOT EXISTS tags (
                id    TEXT NOT NULL PRIMARY KEY,
                name  TEXT NOT NULL UNIQUE,
                ctime TEXT
            )");

        // 打标时输入的近义词归一到正名，避免"巨乳/爆乳/大胸"各管一摊
        NonQuery(conn, @"
            CREATE TABLE IF NOT EXISTS tag_aliases (
                tag_id TEXT NOT NULL,
                alias  TEXT NOT NULL,
                PRIMARY KEY (tag_id, alias)
            )");

        // source 记这一刀是谁打的：manual 人工、ai 自动。
        // 分开存是为了能筛出"AI 打的还没复核"的那批，而不是把两种信任度混成一锅。
        NonQuery(conn, @"
            CREATE TABLE IF NOT EXISTS video_tags (
                video_id TEXT NOT NULL,
                tag_id   TEXT NOT NULL,
                source   TEXT NOT NULL DEFAULT 'manual',
                ctime    TEXT,
                PRIMARY KEY (video_id, tag_id)
            )");

        // AI 不许直接往 tags 里造新词：想造就落这条队列，人工批准（转成正式标签）或并入已有。
        // 词表失控是标签功能唯一的死法，所以把口子收在这里而不是靠提示词自觉。
        NonQuery(conn, @"
            CREATE TABLE IF NOT EXISTS tag_suggestions (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                video_id   TEXT NOT NULL,
                tag_id     TEXT,
                name       TEXT NOT NULL,
                note       TEXT,
                status     TEXT NOT NULL DEFAULT 'pending',
                created_at TEXT
            )");

        // ---------------------------------------------------------------- 关联影片（合辑）

        // 一个文件是几部片子剪在一起时，它们天然是一个"组"而不是两两连线：
        // 3 部互联要 3 条边、5 部要 10 条，而且语义上就是同一部合辑。
        // position 给"上/中/下"的顺序；一部片可以同时属于多个组。
        // 与 videos.seriesid 不冲突：系列是官方续作线，组是他自己剪的合辑。
        NonQuery(conn, @"
            CREATE TABLE IF NOT EXISTS video_groups (
                id    TEXT NOT NULL PRIMARY KEY,
                name  TEXT NOT NULL,
                ctime TEXT
            )");

        NonQuery(conn, @"
            CREATE TABLE IF NOT EXISTS video_group_items (
                group_id TEXT NOT NULL,
                video_id TEXT NOT NULL,
                position INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (group_id, video_id)
            )");

        // 影片的外部档案地址（av-wiki / javcup / FANZA 的番号页）。
        // 与 actor_links 同构、共用 Utils/Links 的校验与归类；它同时是将来抓取的首选定位键：
        // 自己的数据已经指向档案时，抓取不用再搜一遍、也不会认错片（演员侧的经验）。
        NonQuery(conn, @"
            CREATE TABLE IF NOT EXISTS video_links (
                id       TEXT    NOT NULL PRIMARY KEY,
                video_id TEXT    NOT NULL,
                kind     TEXT    NOT NULL,
                url      TEXT    NOT NULL
            )");

        NonQuery(conn, @"
            CREATE TABLE IF NOT EXISTS video_series (
                id      TEXT NOT NULL PRIMARY KEY,
                name    TEXT NOT NULL,
                alias   TEXT,
                link    TEXT,
                country TEXT,
                ctime   TEXT NOT NULL,
                utime   TEXT NOT NULL
            )");

        NonQuery(conn, @"
            CREATE TABLE IF NOT EXISTS video_likes (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                video_id    TEXT    NOT NULL,
                liked_at    TEXT    NOT NULL,
                target_type TEXT    NOT NULL DEFAULT 'video'
            )");

        NonQuery(conn, @"
            CREATE TABLE IF NOT EXISTS system_settings (
                id      TEXT NOT NULL PRIMARY KEY,
                name    TEXT NOT NULL,
                content TEXT,
                ctime   TEXT NOT NULL,
                utime   TEXT NOT NULL
            )");

        NonQuery(conn, @"
            CREATE TABLE IF NOT EXISTS friend_links (
                id          TEXT NOT NULL PRIMARY KEY,
                name        TEXT NOT NULL,
                link        TEXT NOT NULL,
                logo        TEXT,
                description TEXT,
                sortorder   INTEGER DEFAULT 0,
                ctime       TEXT NOT NULL,
                utime       TEXT NOT NULL
            )");

        NonQuery(conn, @"
            CREATE TABLE IF NOT EXISTS scan_directories (
                id                TEXT    NOT NULL PRIMARY KEY,
                path              TEXT    NOT NULL,
                ctime             TEXT    NOT NULL,
                utime             TEXT    NOT NULL,
                category          TEXT DEFAULT ''
            )");

        NonQuery(conn, @"
            CREATE TABLE IF NOT EXISTS comics (
                id          TEXT    NOT NULL PRIMARY KEY,
                name        TEXT    NOT NULL,
                author      TEXT    DEFAULT '',
                description TEXT    DEFAULT '',
                url         TEXT    DEFAULT '',
                cover_path  TEXT    DEFAULT '',
                directory   TEXT    DEFAULT '',
                ctime       TEXT    NOT NULL,
                utime       TEXT    NOT NULL,
                status      INTEGER DEFAULT 0
            )");

        NonQuery(conn, @"
            CREATE TABLE IF NOT EXISTS comic_chapters (
                id          TEXT    NOT NULL PRIMARY KEY,
                comic_id    TEXT    NOT NULL,
                title       TEXT    NOT NULL,
                directory   TEXT    DEFAULT '',
                sort_order  INTEGER DEFAULT 0,
                image_count INTEGER DEFAULT 0,
                ctime       TEXT    NOT NULL
            )");
    }

    /// <summary>
    /// 索引。建库前全库除主键自索引外没有任何索引，列表查询每次都是全表扫 + 相关子查询。
    /// </summary>
    private void CreateIndexes(SqliteConnection conn)
    {
        var indexes = new (string Name, string Sql)[]
        {
            // 列表页默认按 ctime 倒序分页；按分类筛选时走 (category, ctime) 复合索引
            ("idx_videos_ctime", "CREATE INDEX IF NOT EXISTS idx_videos_ctime ON videos(ctime DESC)"),
            ("idx_videos_category_ctime", "CREATE INDEX IF NOT EXISTS idx_videos_category_ctime ON videos(category, ctime DESC)"),
            // code 用于字幕/流媒体寻址与封面文件名匹配
            ("idx_videos_code", "CREATE INDEX IF NOT EXISTS idx_videos_code ON videos(code)"),
            ("idx_videos_country", "CREATE INDEX IF NOT EXISTS idx_videos_country ON videos(country)"),
            // 系列详情页：WHERE seriesid = ? ORDER BY sort_order
            ("idx_videos_series_sort", "CREATE INDEX IF NOT EXISTS idx_videos_series_sort ON videos(seriesid, sort_order)"),
            // video_actors 主键是 (video_id, actor_id)，反查"某演员的影片"需要 actor 侧索引
            ("idx_video_actors_actor", "CREATE INDEX IF NOT EXISTS idx_video_actors_actor ON video_actors(actor_id)"),
            // 卡片上的 like_count 子查询与点赞列表
            ("idx_video_likes_video", "CREATE INDEX IF NOT EXISTS idx_video_likes_video ON video_likes(video_id, target_type)"),
            ("idx_video_likes_time", "CREATE INDEX IF NOT EXISTS idx_video_likes_time ON video_likes(liked_at)"),
            ("idx_comics_ctime", "CREATE INDEX IF NOT EXISTS idx_comics_ctime ON comics(ctime DESC)"),
            ("idx_comic_chapters_comic", "CREATE INDEX IF NOT EXISTS idx_comic_chapters_comic ON comic_chapters(comic_id, sort_order)"),
            ("idx_series_name", "CREATE INDEX IF NOT EXISTS idx_series_name ON video_series(name)"),
            // 按曾用名精确/前缀检索；按演员取自己的别名走主键 (actor_id, alias) 前缀
            ("idx_actor_aliases_alias", "CREATE INDEX IF NOT EXISTS idx_actor_aliases_alias ON actor_aliases(alias)"),
            // 详情页取外链、按 kind 排查
            ("idx_actor_links_actor", "CREATE INDEX IF NOT EXISTS idx_actor_links_actor ON actor_links(actor_id, kind)"),
            // 相册一次取某人全部图片并按主图打头；列表页的头像也走这条
            ("idx_actor_images_actor", "CREATE INDEX IF NOT EXISTS idx_actor_images_actor ON actor_images(actor_id, is_primary DESC, file_name)"),
            // 每人最多一张主图：部分唯一索引，NULL/0 行不受约束
            ("idx_actor_images_primary", "CREATE UNIQUE INDEX IF NOT EXISTS idx_actor_images_primary ON actor_images(actor_id) WHERE is_primary = 1"),
            // 片商：按别名找正主、按片商反查它名下有哪些片
            ("idx_studio_aliases_alias", "CREATE INDEX IF NOT EXISTS idx_studio_aliases_alias ON studio_aliases(alias)"),
            ("idx_video_studios_studio", "CREATE INDEX IF NOT EXISTS idx_video_studios_studio ON video_studios(studio_id)"),
            // 标签：按标签筛片是主路径（WHERE tag_id = ? 再取影片），别名同理
            ("idx_tag_aliases_alias", "CREATE INDEX IF NOT EXISTS idx_tag_aliases_alias ON tag_aliases(alias)"),
            ("idx_video_tags_tag", "CREATE INDEX IF NOT EXISTS idx_video_tags_tag ON video_tags(tag_id)"),
            // 同一部片同一个候选词只留一条待审；批准/驳回后不再占唯一性，可以再次提出
            ("idx_tag_sugg_pending", "CREATE UNIQUE INDEX IF NOT EXISTS idx_tag_sugg_pending ON tag_suggestions(video_id, name) WHERE status = 'pending'"),
            // 详情页"本片属于哪个合辑"：从影片反查所在组，主键 (group_id, video_id) 帮不上这个方向
            ("idx_video_group_items_video", "CREATE INDEX IF NOT EXISTS idx_video_group_items_video ON video_group_items(video_id)"),
            ("idx_video_links_video", "CREATE INDEX IF NOT EXISTS idx_video_links_video ON video_links(video_id, kind)"),
        };

        foreach (var (name, sql) in indexes)
        {
            try
            {
                NonQuery(conn, sql);
                _logger.LogInformation("索引 [{Name}] 就绪", name);
            }
            catch (Exception ex)
            {
                // 索引缺失只是慢，不该让应用起不来
                _logger.LogWarning(ex, "创建索引 [{Name}] 失败", name);
            }
        }
    }

    // ---------------------------------------------------------------- 工具

    private static void AddColumnIfMissing(SqliteConnection conn, string table, string column, string declaration)
    {
        var exists = false;
        using (var check = new SqliteCommand($"PRAGMA table_info({table})", conn))
        using (var reader = check.ExecuteReader())
        {
            while (reader.Read())
            {
                if (string.Equals(reader["name"].ToString(), column, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }

        if (exists) return;
        NonQuery(conn, $"ALTER TABLE [{table}] ADD COLUMN [{column}] {declaration}");
    }

    private static bool TableExists(SqliteConnection conn, string table)
        => Scalar(conn, "SELECT name FROM sqlite_master WHERE type='table' AND name=@t", P("@t", table)) != null;

    private static object? Scalar(SqliteConnection conn, string sql, params SqliteParameter[] parameters)
    {
        using var cmd = new SqliteCommand(sql, conn);
        if (parameters is { Length: > 0 }) cmd.Parameters.AddRange(parameters);
        return cmd.ExecuteScalar();
    }

    private static int NonQuery(SqliteConnection conn, string sql, params SqliteParameter[] parameters)
    {
        using var cmd = new SqliteCommand(sql, conn);
        if (parameters is { Length: > 0 }) cmd.Parameters.AddRange(parameters);
        return cmd.ExecuteNonQuery();
    }

    private static bool ColumnExists(SqliteConnection conn, string table, string column)
    {
        using var check = new SqliteCommand($"PRAGMA table_info({table})", conn);
        using var reader = check.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader["name"].ToString(), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 删表前把它转存成 SQL 到备份目录。数据本身可能不值钱，但迁移不可逆，
    /// 留一份比事后解释"当时觉得没用"便宜得多。
    /// </summary>
    private void DumpTableBeforeDrop(SqliteConnection conn, string table)
    {
        if (!TableExists(conn, table)) return;

        var backupPath = _db.GetBackupPath();
        if (string.IsNullOrEmpty(backupPath) || !Directory.Exists(backupPath)) return;

        var file = Path.Combine(backupPath, $"dropped_{table}_{DateTime.Now:yyyyMMdd-HHmmss}.sql");
        try
        {
            var lines = new List<string>();
            using (var schemaCmd = new SqliteCommand("SELECT sql FROM sqlite_master WHERE type='table' AND name = @t", conn))
            {
                schemaCmd.Parameters.Add(new SqliteParameter("@t", table));
                if (schemaCmd.ExecuteScalar()?.ToString() is { } ddl)
                    lines.Add(ddl + ";");
            }

            using (var dataCmd = new SqliteCommand($"SELECT * FROM [{table}]", conn))
            using (var reader = dataCmd.ExecuteReader())
            {
                var cols = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
                while (reader.Read())
                {
                    var values = Enumerable.Range(0, reader.FieldCount).Select(i =>
                        reader.IsDBNull(i) ? "NULL" : "'" + reader[i].ToString()?.Replace("'", "''") + "'");
                    lines.Add($"INSERT INTO [{table}] ({string.Join(", ", cols)}) VALUES ({string.Join(", ", values)});");
                }
            }

            File.WriteAllLines(file, lines);
            _logger.LogInformation("已把待删除表 [{Table]} 的 {Count} 条语句转存到 {File}", table, lines.Count, file);
        }
        catch (Exception ex)
        {
            // 转存失败不阻断迁移：每日全库快照已经覆盖了这份数据
            _logger.LogWarning(ex, "转存待删除表 [{Table]} 失败，仍继续迁移", table);
        }
    }

    /// <summary>
    /// 播种地区与分类的规范列表（system_settings.countries / .categories）。
    ///
    /// 只在键不存在时写一次：用户后来在设置里清空是有意为之，不该被启动流程复活。
    /// 全新库和既有库都走这里，所以不占迁移版本号——只加数据，不改结构。
    /// </summary>
    private static void SeedTaxonomy(SqliteConnection conn)
    {
        SeedListIfMissing(conn, "countries", @"
            SELECT country FROM videos   WHERE country IS NOT NULL AND country <> ''
            UNION
            SELECT country FROM actors   WHERE country IS NOT NULL AND country <> ''
            UNION
            SELECT country FROM video_series WHERE country IS NOT NULL AND country <> ''
            ORDER BY country");

        // 分类还要并上首页已配置的，否则 homePageCategories 里会有选项不在可选集中
        SeedListIfMissing(conn, "categories", @"
            SELECT category FROM videos WHERE category IS NOT NULL AND category <> ''
            ORDER BY category", "homePageCategories");
    }

    /// <summary>从 selectSql 取值，再并入 extraFromKey 这个设置里已有的值，保序去重。</summary>
    private static void SeedListIfMissing(SqliteConnection conn, string name, string selectSql, string? extraFromKey = null)
    {
        if (Scalar(conn, "SELECT content FROM system_settings WHERE name = @n", P("@n", name)) != null)
            return;

        var values = new List<string>();
        using (var cmd = new SqliteCommand(selectSql, conn))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                if (!reader.IsDBNull(0)) values.Add(reader.GetString(0));
            }
        }

        if (extraFromKey != null)
        {
            var extra = Scalar(conn, "SELECT content FROM system_settings WHERE name = @n", P("@n", extraFromKey)) as string;
            if (!string.IsNullOrWhiteSpace(extra))
                values.AddRange(extra.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        var distinct = values.Distinct().ToList();
        NonQuery(conn,
            "INSERT INTO system_settings (id, name, content, ctime, utime) VALUES (@id, @n, @v, @t, @t)",
            P("@id", Guid.NewGuid().ToString("N").ToUpper()),
            P("@n", name),
            P("@v", string.Join(",", distinct)),
            P("@t", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
    }

    private static SqliteParameter P(string name, object value) => new(name, value);
}

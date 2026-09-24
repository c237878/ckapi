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
    private const int TargetVersion = 4;

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
                id               TEXT    PRIMARY KEY,
                name             TEXT    NOT NULL,
                category         TEXT    NOT NULL,
                file_path        TEXT,
                file_size        INTEGER,
                cover_path       TEXT,
                code             TEXT,
                country          TEXT DEFAULT '',
                seriesid         TEXT,
                ctime            TEXT,
                media_attr_flags INTEGER DEFAULT 0,
                sort_order       INTEGER DEFAULT 0
            )");

        NonQuery(conn, @"
            CREATE TABLE IF NOT EXISTS actors (
                id          TEXT    PRIMARY KEY,
                name        TEXT    UNIQUE NOT NULL,
                bio         TEXT,
                ctime       TEXT,
                country     TEXT
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

        NonQuery(conn, @"
            CREATE TABLE IF NOT EXISTS video_actors (
                video_id TEXT,
                actor_id TEXT,
                PRIMARY KEY (video_id, actor_id),
                FOREIGN KEY (video_id) REFERENCES videos(id),
                FOREIGN KEY (actor_id) REFERENCES actors(id)
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

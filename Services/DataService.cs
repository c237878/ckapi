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
    private const int TargetVersion = 14;

    /// <summary>代码期望的 schema 版本，给「运行状态」面板判断"迁移到底跑完没有"用。</summary>
    public static int SchemaTargetVersion => TargetVersion;

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

    /// <summary>
    /// 片商从多对多收成一部片一个值（v9）。
    ///
    /// 原来照 TMDB 做成 video_studios(video_id, studio_id, role)，预设"制作商 A + 发行商 B"会常见；
    /// 实际填下来 27 部有片商的影片没有一部挂过两家，role 全是空串，
    /// 于是这张表只剩"给一列值假装成集合"的成本：界面要单选、筛选要 EXISTS、删片商要清挂接。
    ///
    /// 回填按 role='maker' 优先取一条（真有多家的老数据时留下制作商，且结果确定、重放不变），
    /// 然后整表删掉。studioid 与 seriesid 同形，读法也照它来。
    /// </summary>
    private void StudioToOneColumn(SqliteConnection conn)
    {
        AddColumnIfMissing(conn, "videos", "studioid", "TEXT");

        if (TableExists(conn, "video_studios"))
        {
            NonQuery(conn, @"
                UPDATE videos SET studioid = (
                    SELECT vs.studio_id FROM video_studios vs
                    WHERE vs.video_id = videos.id
                    ORDER BY CASE WHEN IFNULL(vs.role, '') = 'maker' THEN 0 ELSE 1 END, vs.studio_id
                    LIMIT 1
                )
                WHERE studioid IS NULL
                  AND EXISTS (SELECT 1 FROM video_studios vs WHERE vs.video_id = videos.id)");
            NonQuery(conn, "DROP TABLE video_studios");
            _logger.LogInformation("片商已收成 videos.studioid 单值，video_studios 中间表已删除");
        }
    }

    /// <summary>
    /// 撤掉题材标签与关联合辑两组结构（v10）。
    ///
    /// 标签这套东西的前提是"有人来用这些标签浏览"：词表要人维护、AI 提的候选要人审、
    /// 近义词要人并入，而这几件事每天在做的只有一个人，收益撑不起成本，整组删掉。
    /// 合辑同理——库里一组都没建过。
    ///
    /// 六张表（tags / tag_aliases / video_tags / tag_suggestions / video_groups / video_group_items）
    /// 都是独立表，videos 上没有列要收，所以只 DROP。结构迁移前 Initialize 已经强制留了一份
    /// pre-migration 快照，要回滚用那份。
    /// </summary>
    private void DropTagAndGroups(SqliteConnection conn)
    {
        foreach (var table in new[]
                 {
                     "tag_suggestions", "video_tags", "tag_aliases", "tags",
                     "video_group_items", "video_groups"
                 })
        {
            if (TableExists(conn, table)) NonQuery(conn, $"DROP TABLE {table}");
        }
        _logger.LogInformation("已撤除题材标签与关联合辑的六张表");
    }

    /// <summary>
    /// 文件层从 videos 拆到 video_files，解说片从"另起一部片"改成"这部片的一个版本"（v11）。
    ///
    /// 起因：库里那 9 个解说版（ATID-428C 这一类）当初是按"新增影片"录的，于是同一部片在库里
    /// 有两行、列表里有两张卡，筛选与统计各算一遍。真实关系是"一部片 + 若干份文件"，
    /// 所以每部片建一条原版行（is_default=1），9 条解说行折回父片当版本行。
    ///
    /// 只搬能自动对上位的：解说行按"库内最长前缀"找父片，命中 0 个或并列多个一律中止，不猜。
    /// 中止前 Initialize 已经强制留了 pre-migration 快照，被删的行另外转存成 SQL 单独留一份。
    ///
    /// 全程不改任何一个磁盘文件名，搬的只是库里的指向；封面本来就在影片层，一份不动。
    /// </summary>
    private void SplitVideoFiles(SqliteConnection conn)
    {
        if (!ColumnExists(conn, "videos", "file_path"))
        {
            _logger.LogInformation("videos 已没有文件层列，跳过拆分");
            return;
        }

        if (!TableExists(conn, "video_files") || !TableExists(conn, "version_types"))
        {
            // Initialize 里 CreateBaseTables 先跑，正常到不了这里；真到了说明结构声明被删了
            throw new InvalidOperationException("video_files / version_types 未建表，无法拆分文件层");
        }

        // 1) 挑出"其实是另一部片的版本"的行：判据是他当初把解说频道挂成了片商，
        //    所以凡是片商叫「AV解说 ××」的行都是版本行候选。
        var candidates = new List<(string Id, string Code, string StudioId, string StudioName)>();
        using (var read = new SqliteCommand(@"
            SELECT v.id, IFNULL(v.code, ''), v.studioid, s.name
            FROM videos v JOIN studios s ON s.id = v.studioid
            WHERE s.name LIKE 'AV解说 %' AND IFNULL(v.code, '') <> ''
            ORDER BY v.code", conn))
        using (var reader = read.ExecuteReader())
        {
            while (reader.Read())
                candidates.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }

        var candidateIds = candidates.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var folded = new List<(string Id, string Code, string ParentId, string StudioId, string StudioName, string Suffix)>();
        var unmatched = new List<string>();

        foreach (var c in candidates)
        {
            // 最长前缀命中：父片番号必须是它的真前缀，且越长越像（ATID-428C 的父片是 ATID-428，不是 ATID）。
            // 前缀比较用 substr 相等而不是 LIKE：番号里出现 _ 或 % 时 LIKE 会把它们当通配符误配。
            // 父片自己不能也是解说行，但判据必须写成 "IS NULL OR NOT IN"——库里多数片没填片商，
            // 而 NULL NOT IN (…) 在 SQL 里是 unknown 而非 true，光写 NOT IN 会把没填片商的父片全滤掉
            // （2026-09-29 在库副本上实测：9 条只对上 1 条，迁移被下面的闸门当场中止）。
            // 父片自己不能也是解说行；判据要写成 "IS NULL OR NOT IN"——库里大部分片没填片商，
            // 而 NULL NOT IN (...) 在 SQL 里是 unknown 不是 true，直接 NOT IN 会把没填片商的父片全滤掉
            var parents = new List<(string Id, string Code)>();
            using (var find = new SqliteCommand(@"
                SELECT p.id, p.code FROM videos p
                WHERE p.code IS NOT NULL AND p.code <> '' AND p.code <> @code
                  AND substr(@code, 1, length(p.code)) = p.code
                  AND (p.studioid IS NULL OR p.studioid NOT IN (SELECT id FROM studios WHERE name LIKE 'AV解说 %'))
                ORDER BY LENGTH(p.code) DESC, p.id LIMIT 2", conn))
            {
                find.Parameters.Add(new SqliteParameter("@code", c.Code));
                using var pr = find.ExecuteReader();
                while (pr.Read()) parents.Add((pr.GetString(0), pr.GetString(1)));
            }

            var longest = parents.FirstOrDefault();
            var tied = parents.Count == 2 && parents[0].Code.Length == parents[1].Code.Length;
            if (parents.Count == 0 || tied || longest.Code.Length >= c.Code.Length)
            {
                unmatched.Add(c.Code);
                continue;
            }

            folded.Add((c.Id, c.Code, longest.Id, c.StudioId, c.StudioName, c.Code.Substring(longest.Code.Length)));
        }

        if (unmatched.Count > 0)
        {
            throw new InvalidOperationException(
                $"解说版对不上父片，迁移中止（不猜）：{string.Join("、", unmatched)}。" +
                "先看这几条的番号，或直接在界面上把它们当独立影片留着。");
        }

        // 2) 那几家解说频道从片商搬进版本类型：它们说的是"这一版是谁做的"，不是制片商。
        //    名字取掉「AV解说 」前缀的短名（界面上他一直就叫"湿姐""步非烟"），后缀按实测码尾填。
        var typeByStudio = new Dictionary<string, string>(StringComparer.Ordinal);
        var sort = 0;
        foreach (var group in folded.GroupBy(f => f.StudioId).OrderBy(g => g.First().StudioName, StringComparer.Ordinal))
        {
            var shortName = group.First().StudioName.StartsWith("AV解说 ") ? group.First().StudioName["AV解说 ".Length..].Trim() : group.First().StudioName;
            var suffix = group.Select(g => g.Suffix).OrderByDescending(s => s.Length).First();

            var existing = Scalar(conn, "SELECT id FROM version_types WHERE name = @n", P("@n", shortName))?.ToString();
            var typeId = existing ?? Guid.NewGuid().ToString("N").ToUpper();
            if (existing is null)
            {
                NonQuery(conn, "INSERT INTO version_types (id, name, suffix, sort) VALUES (@id, @n, @s, @sort)",
                    P("@id", typeId), P("@n", shortName), P("@s", suffix), P("@sort", ++sort));
            }
            typeByStudio[group.Key] = typeId;
        }
        _logger.LogInformation("版本类型已建 {Count} 条（由解说频道搬入）：{Names}", typeByStudio.Count,
            string.Join("、", folded.GroupBy(f => f.StudioName).Select(g => $"{g.Key["AV解说 ".Length..]}={g.First().Suffix}")));

        // 3) 解说行折成父片的版本行（is_default=0，文件名标识与那份文件的属性整体搬过去）
        foreach (var f in folded)
        {
            NonQuery(conn, @"
                INSERT INTO video_files (id, video_id, code, type_id, label, file_path, file_size,
                                         res_w, res_h, subtitle_state, watermark_state, scan_time, is_default, ctime)
                SELECT upper(hex(randomblob(16))), @parent, j.code, @type, IFNULL(j.name, ''),
                       IFNULL(j.file_path, ''), IFNULL(j.file_size, 0), j.res_w, j.res_h,
                       IFNULL(j.subtitle_state, 'unknown'), IFNULL(j.watermark_state, 'unknown'), j.scan_time, 0, j.ctime
                FROM videos j WHERE j.id = @id",
                P("@parent", f.ParentId), P("@type", typeByStudio[f.StudioId]), P("@id", f.Id));
        }

        // 4) 每部活下来的片各建一条原版行：番号就是它的文件名标识，is_default=1
        var foldIds = InList(folded.Select(x => x.Id));
        // 那句"已经建过默认行的就别再建"永远要在，但 WHERE 只能出现一次：
        // 原先把 exclude 拼成空串时，SQL 直接变成 `FROM videos v NOT EXISTS (...)` ——
        // 库里一条解说片都没有（早期的备份、按快照恢复回来的旧库都是这种）就报
        // near "EXISTS": syntax error，迁移整条挂在这儿
        var alreadyDefault = "NOT EXISTS (SELECT 1 FROM video_files f WHERE f.video_id = v.id AND f.is_default = 1)";
        var where = folded.Count == 0
            ? $"WHERE {alreadyDefault}"
            : $"WHERE v.id NOT IN ({foldIds}) AND {alreadyDefault}";
        var made = NonQuery(conn, $@"
            INSERT INTO video_files (id, video_id, code, type_id, label, file_path, file_size,
                                     res_w, res_h, subtitle_state, watermark_state, scan_time, is_default, ctime)
            SELECT upper(hex(randomblob(16))), v.id, IFNULL(v.code, ''), '', '',
                   IFNULL(v.file_path, ''), IFNULL(v.file_size, 0), v.res_w, v.res_h,
                   IFNULL(v.subtitle_state, 'unknown'), IFNULL(v.watermark_state, 'unknown'), v.scan_time, 1, v.ctime
            FROM videos v
            {where}");
        _logger.LogInformation("已为 {Made} 部片建出默认版本行", made);

        // 5) 删掉被折走的解说影片行。它挂的演员与系列父片都有（实测差集为 0），点赞 0 条，
        //    所以搬过去不会丢关系；但仍旧先转存一份，回滚时不必整库还原。
        if (folded.Count > 0)
        {
            DumpRowsBeforeDelete(conn, "videos", $"id IN ({foldIds})");
            foreach (var table in new[] { "video_actors", "video_links", "video_likes", "video_files" })
                DumpRowsBeforeDelete(conn, table, $"video_id IN ({foldIds})");

            foreach (var table in new[] { "video_actors", "video_links", "video_likes", "video_files" })
                NonQuery(conn, $"DELETE FROM {table} WHERE video_id IN ({foldIds})");
            NonQuery(conn, $"DELETE FROM videos WHERE id IN ({foldIds})");
        }
        _logger.LogInformation("已把 {Count} 条解说影片行折成版本行并删除原行：{Codes}",
            folded.Count, string.Join("、", folded.Select(f => f.Code)));

        // 6) 点赞记到版本上：旧记录指的是"当时那部片的默认版本"，回填成默认行的 id
        AddColumnIfMissing(conn, "video_likes", "file_id", "TEXT");
        NonQuery(conn, @"
            UPDATE video_likes SET file_id = (
                SELECT f.id FROM video_files f WHERE f.video_id = video_likes.video_id AND f.is_default = 1
            ) WHERE file_id IS NULL AND target_type = 'video'");

        // 7) 解说频道不再是片商：连同它们的别名一起撤掉（此时已没有影片挂着它们）
        var studioIds = folded.Select(f => f.StudioId).Distinct().ToList();
        if (studioIds.Count > 0)
        {
            var ids = InList(studioIds);
            var inUse = Convert.ToInt32(Scalar(conn, $"SELECT COUNT(*) FROM videos WHERE studioid IN ({ids})") ?? 0);
            if (inUse > 0)
            {
                _logger.LogWarning("那几家解说频道还被 {Count} 部片当片商用着，片商与别名先留着", inUse);
            }
            else
            {
                DumpRowsBeforeDelete(conn, "studio_aliases", $"studio_id IN ({ids})");
                DumpRowsBeforeDelete(conn, "studios", $"id IN ({ids})");
                NonQuery(conn, $"DELETE FROM studio_aliases WHERE studio_id IN ({ids})");
                NonQuery(conn, $"DELETE FROM studios WHERE id IN ({ids})");
                _logger.LogInformation("已把 {Count} 家解说频道从片商撤掉（它们现在是版本类型）", studioIds.Count);
            }
        }

        // 8) 「av解说」不再是分类：解说版不是一部片，也就没有分类可言
        var categories = Scalar(conn, "SELECT content FROM system_settings WHERE name = 'categories'")?.ToString() ?? "";
        var kept = categories
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(c => c != "av解说")
            .ToList();
        if (kept.Count != categories.Split(',', StringSplitOptions.RemoveEmptyEntries).Length)
        {
            NonQuery(conn, "UPDATE system_settings SET content = @c, utime = @u WHERE name = 'categories'",
                P("@c", string.Join(",", kept)), P("@u", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
            _logger.LogInformation("已从分类清单撤掉 av解说，剩余 {Count} 项", kept.Count);
        }

        // 9) 影片层那七列就此退休
        foreach (var column in new[]
                 { "file_path", "file_size", "res_w", "res_h", "subtitle_state", "watermark_state", "scan_time" })
        {
            if (ColumnExists(conn, "videos", column)) NonQuery(conn, $"ALTER TABLE videos DROP COLUMN {column}");
        }

        // 10) 收口自检：每部片必须恰好一条默认版本，且同一类型只能有一条——
        // 这两条都是 CreateIndexes 里唯一索引要兜的事，先在这儿查：索引建不上只留一条 warning，
        // 之后每次启动都安静地少一个约束，比迁移当场中止难查得多
        var orphan = Convert.ToInt32(Scalar(conn, @"
            SELECT COUNT(*) FROM videos v
            WHERE (SELECT COUNT(*) FROM video_files f WHERE f.video_id = v.id AND f.is_default = 1) <> 1") ?? 0);
        if (orphan > 0)
            throw new InvalidOperationException($"{orphan} 部片没有恰好一条默认版本行，迁移中止");

        var dupType = Convert.ToInt32(Scalar(conn, @"
            SELECT COUNT(*) FROM (
                SELECT video_id FROM video_files GROUP BY video_id, type_id HAVING COUNT(*) > 1)") ?? 0);
        if (dupType > 0)
        {
            var sample = Scalar(conn, @"
                SELECT p.code || ' / ' || IFNULL(vt.name, '原版') FROM video_files f
                JOIN videos p ON p.id = f.video_id LEFT JOIN version_types vt ON vt.id = f.type_id
                GROUP BY f.video_id, f.type_id HAVING COUNT(*) > 1 LIMIT 3")?.ToString() ?? "";
            throw new InvalidOperationException(
                $"{dupType} 组同一部片挂了同一个版本类型两次（例：{sample}），迁移中止。" +
                "一部片的每个版本类型只能有一条，先在界面上把重复的那条改成别的类型再启动。");
        }

        _logger.LogInformation(
            "文件层拆分完成：{Files} 个版本行 / {Movies} 部片，其中多版本片 {Multi} 部；videos 的七个文件列已删除",
            Scalar(conn, "SELECT COUNT(*) FROM video_files"),
            Scalar(conn, "SELECT COUNT(*) FROM videos"),
            Convert.ToInt32(Scalar(conn, @"
                SELECT COUNT(*) FROM (SELECT video_id FROM video_files GROUP BY video_id HAVING COUNT(*) > 1)") ?? 0));
    }

    /// <summary>
    /// v12：video_likes 加 play_time（REAL，秒，可空）——点赞那一刻的播放位置。
    ///
    /// 为什么要存：一部片里被点赞的那些时间点就是"精彩瞬间"的原始材料，攒着以后能做集锦；
    /// 现在不存，以后想知道"她当时看到哪儿点的赞"就再也问不回来了。
    /// 位置属于**这一版**的时间轴（原版 119 分钟、解说版 21 分钟，同一个数字不是同一个画面），
    /// 所以它和 file_id 是成对读的，单独一个数没有意义。
    ///
    /// 只加列，不回填：老记录当时没这个信息，猜不出来，留 NULL 让界面显示"—"。
    /// </summary>
    private void AddLikePlayTime(SqliteConnection conn)
    {
        AddColumnIfMissing(conn, "video_likes", "play_time", "REAL");
    }

    /// <summary>
    /// v13：建 file_state（服务端播放进度）。
    ///
    /// 原来进度只在浏览器本地，换设备/清缓存就归零；这份状态值得进库的理由还在后面：
    /// 点赞已经记了 play_time（v12），"看到哪儿"和"哪些点被赞过"放在一起才做得出精彩瞬间。
    ///
    /// 不回填：本地 localStorage 里那些值只有那台设备自己看得见，服务器也读不到它们，
    /// 由前端第一次读到本地值时自己传上来（见 VideoDetail 的进度逻辑）。
    /// </summary>
    private void AddFileState(SqliteConnection conn)
    {
        NonQuery(conn, @"
            CREATE TABLE IF NOT EXISTS file_state (
                file_id    TEXT NOT NULL PRIMARY KEY,
                position   REAL NOT NULL,
                updated_at TEXT NOT NULL
            )");
    }

    /// <summary>
    /// v14：video_files 加 fingerprint，用来发现"同一部片重复入库"。
    ///
    /// 只加列不回填：回填要读三千多个文件的头尾，那是几十分钟级的活，不该塞在一次启动迁移里。
    /// 由扫描顺手补（SourceScanner.Pending 把"指纹还空着"也当候选），跑一次普通扫描就齐了。
    /// 索引故意不是唯一的：重复是"给人看的证据"，不是"该被数据库拒绝的错误"。
    /// </summary>
    private void AddFingerprint(SqliteConnection conn)
    {
        AddColumnIfMissing(conn, "video_files", "fingerprint", "TEXT");
    }

    /// <summary>把一串 id 写成 SQL 的 IN 列表。只用于内部生成的 GUID，不接受用户输入。</summary>
    private static string InList(IEnumerable<string> ids)
        => string.Join(",", ids.Select(i => $"'{i.Replace("'", "''")}'"));

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
            .Append((9, "片商收成 videos.studioid 单值，删掉 video_studios（见 StudioToOneColumn 注释）", StudioToOneColumn))
            .Append((10, "撤掉题材标签与关联合辑六张表（见 DropTagAndGroups 注释）", DropTagAndGroups))
            .Append((11, "文件层拆到 video_files，解说片折回父片当版本（见 SplitVideoFiles 注释）", SplitVideoFiles))
            .Append((12, "点赞记录带上点赞那一刻的播放进度 play_time（见 AddLikePlayTime 注释）", AddLikePlayTime))
            .Append((13, "播放进度搬到服务端 file_state 表（见 AddFileState 注释）", AddFileState))
            .Append((14, "版本行加内容指纹 fingerprint（见 AddFingerprint 注释）", AddFingerprint))
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

            // 库级一次性设置：WAL 让"前台在读"和"后台扫描在写"不再互斥
            // （默认 delete 模式下写事务要独占，撞车就直接 SQLITE_BUSY）。
            // journal_mode 会写进库文件，之后每条连接都自动是这个模式；-wal/-shm 落在库旁边，
            // 所以库必须在支持共享内存的本地盘上（这条已经成立：/Volumes/disk1 是本地盘，媒体卷才走 SMB）
            var journalMode = Scalar(conn, "PRAGMA journal_mode=WAL")?.ToString();
            _logger.LogInformation("SQLite 日志模式：{Mode}", string.IsNullOrEmpty(journalMode) ? "未知" : journalMode);

            var isFresh = !TableExists(conn, "videos");

            // 快照目录可以在界面上改（存 system_settings.backup_dir），必须在下面这份"迁移前快照"之前
            // 交给 SQLiteHelper，否则最要紧的那份凭证还会写到旧目录里去
            ApplyBackupDir(conn);

            // 结构迁移前必须留一份即时快照，且不能被"当天已有常规快照"顶掉：
            // 实测过一次 07:42 的常规快照让 22:32 的迁移跳过了备份，纯属侥幸。
            // 名字里带时刻，是因为一天可能迁两次（早上一轮、按快照恢复完又补一轮）——
            // 用 force 覆盖同一天的 pre-migration 等于把第一次那份凭证弄丢
            var pendingMigration = !isFresh && GetVersion(conn) < TargetVersion;
            _db.BackupDatabase(pendingMigration ? "结构迁移前" : "启动",
                force: pendingMigration,
                tag: pendingMigration ? $"pre-migration-{DateTime.Now:HHmm}" : null);

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
            SeedScrapeChannels(conn);

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

    /// <summary>
    /// 把界面上设过的快照目录交给 SQLiteHelper。表还没建、或压根没设过，就什么都不做——
    /// 那时用的是 appsettings 里那份默认值。读失败也只退回默认，不该让库起不来。
    /// </summary>
    private void ApplyBackupDir(SqliteConnection conn)
    {
        try
        {
            if (!TableExists(conn, "system_settings")) return;
            var configured = Scalar(conn, "SELECT content FROM system_settings WHERE name = @n",
                P("@n", BackupService.SettingDir))?.ToString();
            if (!string.IsNullOrWhiteSpace(configured)) _db.SetBackupPathOverride(configured);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取快照目录设置失败，这一轮先用配置文件里那份");
        }
    }

    // ---------------------------------------------------------------- 基线结构

    private static void CreateBaseTables(SqliteConnection conn)
    {
        // 影片层只留"这部片是什么"：片名、番号、分类、封面、归属。
        // 文件层七列（路径/大小/宽高/字幕/水印/扫描时间）v11 起搬到 video_files，这里不再存副本
        NonQuery(conn, @"
            CREATE TABLE IF NOT EXISTS videos (
                id              TEXT    PRIMARY KEY,
                name            TEXT    NOT NULL,
                category        TEXT    NOT NULL,
                cover_path      TEXT,
                code            TEXT,
                country         TEXT DEFAULT '',
                seriesid        TEXT,
                ctime           TEXT,
                sort_order      INTEGER DEFAULT 0,
                original_name   TEXT,
                release_date    TEXT,
                /* 片商是一部片的一个值，与 seriesid 同形（v9 起；原来是 video_studios 多对多） */
                studioid        TEXT
            )");

        // 文件层（v11）：一部片 → 一到多份文件，1:N 严格（这些版本只属于这部片，不会同时对应多部片，2026-09-28 定）。
        // 只属于"这一份文件"的事实全在这里，影片层不再存副本 —— 筛选与统计走 is_default 那一行。
        // 每部片恰有一条 is_default=1 由代码保证（迁移动作与新建影片都会建原版行），
        // 部分唯一索引只能保证"至多一条"，见 CreateIndexes 的 idx_video_files_default。
        NonQuery(conn, @"
            CREATE TABLE IF NOT EXISTS video_files (
                id              TEXT    PRIMARY KEY,
                video_id        TEXT    NOT NULL,
                /* 本行的文件名标识：番号 + 尾巴，原版那一行等于影片番号。
                   它是「文件名该长什么样」的权威值，改名工具按它对齐，不每次从影片番号重算 */
                code            TEXT    NOT NULL DEFAULT '',
                /* 版本类型；'' 固定表示原版那一行。
                   同一部片的同一个类型只能有一条，由 idx_video_files_movie_type 兜 */
                type_id         TEXT    NOT NULL DEFAULT '',
                /* 版本名称，空则界面显示类型名 */
                label           TEXT    NOT NULL DEFAULT '',
                /* 空串 = 版本条目已建、文件还没上传。不用任何前缀暗号表示空 */
                file_path       TEXT    NOT NULL DEFAULT '',
                file_size       INTEGER NOT NULL DEFAULT 0,
                res_w           INTEGER,
                res_h           INTEGER,
                subtitle_state  TEXT    NOT NULL DEFAULT 'unknown',
                watermark_state TEXT    NOT NULL DEFAULT 'unknown',
                scan_time       TEXT,
                /* 内容指纹：头尾各 64KB + 大小的 SHA256，带算法前缀。
                   空 = 还没扫到；由扫描任务顺手补，不在启动迁移里回填 */
                fingerprint     TEXT,
                /* 当前作为影片口径的那一版：列表筛选、统计、卡片字段都走它。
                   用户可以设任意一版为默认，所以它与文件名无关 */
                is_default      INTEGER NOT NULL DEFAULT 0,
                ctime           TEXT
            )");

        // 版本类型词表（设置里维护）。初始内容是从 studios 里搬出来的那几家中文 AV 解说频道：
        // 它们本来就是"这一版是谁做的"，不是制片商，之前挂成片商是临时的将就
        NonQuery(conn, @"
            CREATE TABLE IF NOT EXISTS version_types (
                id     TEXT    PRIMARY KEY,
                name   TEXT    NOT NULL,
                /* 只用于新建版本时建议文件名后缀（C=解说、E=剪辑…）。
                   番号级联改名时不重算它，只换前缀，否则同类型的两版会撞成一个名字 */
                suffix TEXT    NOT NULL DEFAULT '',
                sort   INTEGER NOT NULL DEFAULT 0
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

        // 一部片只有一家片商（实测库里 27 部有片商的没有一部挂两家），所以挂在 videos.studioid 上，
        // 不再另建 video_studios 中间表——多对多表在这里换来的只有"界面得假装单选"。

        // 题材标签与关联合辑在 v10 整体撤除（建表与索引都已移除，迁移注释见 DropTagAndGroups）。

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

        // 抓取通道：一个站点一条，把"去哪查、怎么抽、要多礼貌"三段都做成可配置的数据。
        // 做成可视化配置的原因不是偷懒少写代码，而是通道会换——av-wiki 一被封，
        // 他要能在界面上填一个新源接着跑，而不是等我改一版重新部署。
        //
        // 后六个字段是运行态而不是配置：配额计数、连续失败数、冷却到几点、最后一次为什么被封。
        // 它们和配置同表同行，是为了让列表页一次就能显示"这条现在能不能用、为什么不能用"。
        NonQuery(conn, @"
            CREATE TABLE IF NOT EXISTS scrape_channels (
                id                 TEXT    NOT NULL PRIMARY KEY,
                name               TEXT    NOT NULL,
                entity             TEXT    NOT NULL DEFAULT 'video',
                enabled            INTEGER NOT NULL DEFAULT 1,
                query_source       TEXT    NOT NULL DEFAULT 'code',
                fetch_url          TEXT    NOT NULL DEFAULT '',
                fetch_kind         TEXT    NOT NULL DEFAULT 'json',
                referer            TEXT,
                user_agent         TEXT,
                rules              TEXT    NOT NULL DEFAULT '[]',
                min_interval_ms    INTEGER NOT NULL DEFAULT 1500,
                daily_quota        INTEGER NOT NULL DEFAULT 300,
                fail_limit         INTEGER NOT NULL DEFAULT 3,
                cooldown_minutes   INTEGER NOT NULL DEFAULT 60,
                note               TEXT,
                used_date          TEXT,
                used_today         INTEGER NOT NULL DEFAULT 0,
                consecutive_fails  INTEGER NOT NULL DEFAULT 0,
                blocked_until      TEXT,
                last_error         TEXT,
                last_ok_at         TEXT,
                ctime              TEXT,
                utime              TEXT
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

        // 点赞针对的是"某一部片的某一版"（v11 定）。file_id 指到 video_files 那一行，
        // 界面从卡片上点默认版本时就是那一版；聚合口径见 VideoController 的三个榜：
        // 记录按版本各算一条，不去重（日历显示总和、点赞榜带版本列、最近点赞按卡片标识区分）。
        NonQuery(conn, @"
            CREATE TABLE IF NOT EXISTS video_likes (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                video_id    TEXT    NOT NULL,
                liked_at    TEXT    NOT NULL,
                target_type TEXT    NOT NULL DEFAULT 'video',
                file_id     TEXT,
                play_time   REAL
            )");

        // 「这一版看到哪儿了」放在服务端而不是浏览器 localStorage（v13）：
        // 换设备、清缓存、换浏览器都不再从头看起。独立一张表而不是往 video_files 加列——
        // 它不是文件的属性，而是"某人在这份文件上的状态"，以后要分用户也是这张表加一列 owner。
        NonQuery(conn, @"
            CREATE TABLE IF NOT EXISTS file_state (
                file_id    TEXT NOT NULL PRIMARY KEY,
                position   REAL NOT NULL,
                updated_at TEXT NOT NULL
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
            // 片商：按别名找正主；按片商筛片走 videos.studioid
            ("idx_studio_aliases_alias", "CREATE INDEX IF NOT EXISTS idx_studio_aliases_alias ON studio_aliases(alias)"),
            ("idx_videos_studioid", "CREATE INDEX IF NOT EXISTS idx_videos_studioid ON videos(studioid)"),
            ("idx_video_links_video", "CREATE INDEX IF NOT EXISTS idx_video_links_video ON video_links(video_id, kind)"),
            // 文件层（v11）：卡片查询一律走 "video_id = v.id AND is_default = 1"，
            // 这条部分唯一索引既是那个 JOIN 的访问路径，也保证一部片至多一条默认版本
            ("idx_video_files_default", "CREATE UNIQUE INDEX IF NOT EXISTS idx_video_files_default ON video_files(video_id) WHERE is_default = 1"),
            // 一部片的同一个类型只能有一条（2026-09-29 定）：type_id 是 NOT NULL，
            // 空串固定表示原版，所以这一条同时兜住了"原版每部至多一条"。
            // 比"同类型的后缀不许重复"更直接：后缀只是新建时的建议值，真要加第二版就该换个类型。
            ("idx_video_files_movie_type", "CREATE UNIQUE INDEX IF NOT EXISTS idx_video_files_movie_type ON video_files(video_id, type_id)"),
            // 详情页的版本下拉：按片取全部版本；改名工具与字幕寻址按行级番号找文件
            ("idx_video_files_video", "CREATE INDEX IF NOT EXISTS idx_video_files_video ON video_files(video_id, is_default DESC, code)"),
            ("idx_video_files_code", "CREATE INDEX IF NOT EXISTS idx_video_files_code ON video_files(code)"),
            ("idx_video_files_type", "CREATE INDEX IF NOT EXISTS idx_video_files_type ON video_files(type_id)"),
            ("idx_video_files_fingerprint", "CREATE INDEX IF NOT EXISTS idx_video_files_fingerprint ON video_files(fingerprint)"),
            // 点赞榜/最近点赞按版本取记录；删除版本行时要按 file_id 找点赞
            ("idx_video_likes_file", "CREATE INDEX IF NOT EXISTS idx_video_likes_file ON video_likes(file_id)"),
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
    /// 删行之前把这几行转存成 SQL 到备份目录。
    ///
    /// 与 DumpTableBeforeDrop 同一个动机：迁移不可逆，整库快照虽然兜得住，但"具体哪几行被搬走、
    /// 被删掉"单独留一份，回滚或核对时才不必把整个库捞出来重看。
    /// where 一律由本文件内部拼（id 是自己生成的 GUID），不接受外部输入。
    /// </summary>
    private void DumpRowsBeforeDelete(SqliteConnection conn, string table, string where)
    {
        if (!TableExists(conn, table)) return;

        var backupPath = _db.GetBackupPath();
        if (string.IsNullOrEmpty(backupPath) || !Directory.Exists(backupPath)) return;

        var file = Path.Combine(backupPath, $"deleted_{table}_{DateTime.Now:yyyyMMdd-HHmmss}_{++_dumpSeq}.sql");
        try
        {
            var lines = new List<string>();
            using var cmd = new SqliteCommand($"SELECT * FROM [{table}] WHERE {where}", conn);
            using var reader = cmd.ExecuteReader();
            var cols = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
            while (reader.Read())
            {
                var values = Enumerable.Range(0, reader.FieldCount).Select(i =>
                    reader.IsDBNull(i) ? "NULL" : "'" + reader[i].ToString()?.Replace("'", "''") + "'");
                lines.Add($"INSERT INTO [{table}] ({string.Join(", ", cols)}) VALUES ({string.Join(", ", values)});");
            }

            if (lines.Count == 0) return;
            File.WriteAllLines(file, lines);
            _logger.LogInformation("已把 [{Table}] 待删除的 {Count} 行转存到 {File}", table, lines.Count, file);
        }
        catch (Exception ex)
        {
            // 转存失败不阻断迁移：每日全库快照与 pre-migration 快照已经覆盖了这份数据
            _logger.LogWarning(ex, "转存 [{Table}] 待删除行失败，仍继续迁移", table);
        }
    }

    private int _dumpSeq;

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

    /// <summary>
    /// 播种内置通道：av-wiki 与老师图鉴的女优档案。
    ///
    /// 它们的抽取逻辑都在代码里（"唯一命中才算数"那套判定不适合降级成配置项），
    /// 所以 fetch_kind 记 builtin、rules 留空——但**限速、配额、熔断、开关都从这条行走**，
    /// 于是设置页里能看到它、能关掉它、能调它一天问多少次。
    /// 按域名判重而不是按"表是否为空"：新加一个源时老库要能补上这一条，
    /// 而他改过的参数不能被启动流程复活。
    /// </summary>
    private static void SeedScrapeChannels(SqliteConnection conn)
    {
        SeedChannel(conn, "av-wiki", "av-wiki 女优档案",
            "https://av-wiki.net/wp-json/wp/v2/tags?search={q}", "https://av-wiki.net/",
            1500, 300, 120,
            "内置抽取：唯一命中且名字对得上才写；站方上了 Imunify360 反爬，被拒时会自动进入冷却");

        // 站方 /terms 明写不得批量抓取，所以这条的礼貌参数比 av-wiki 保守一个数量级
        SeedChannel(conn, "laoshi.ink", "老师图鉴 女优档案",
            "https://laoshi.ink/actresses/", "https://laoshi.ink/",
            8000, 40, 240,
            "内置抽取：只补生日、别名（日文名/罗马音）与头像，姓名要唯一命中档案编号才写；" +
            "站方用户协议禁止批量抓取，每日配额故意给得小，站上缺的字段一律写「待补充」已过滤");
    }

    private static void SeedChannel(
        SqliteConnection conn, string host, string name, string url, string referer,
        int intervalMs, int quota, int cooldownMinutes, string note)
    {
        if (Scalar(conn, "SELECT id FROM scrape_channels WHERE fetch_url LIKE @h",
                P("@h", $"%{host}%")) != null) return;

        const string sql = @"
            INSERT INTO scrape_channels
                (id, name, entity, enabled, query_source, fetch_url, fetch_kind, referer, user_agent,
                 rules, min_interval_ms, daily_quota, fail_limit, cooldown_minutes, note, ctime, utime)
            VALUES
                (@id, @name, 'actor', 1, 'name', @url, 'builtin', @referer, NULL, '[]',
                 @interval, @quota, 3, @cooldown, @note, @t, @t)";
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N").ToUpper());
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@url", url);
        cmd.Parameters.AddWithValue("@referer", referer);
        cmd.Parameters.AddWithValue("@interval", intervalMs);
        cmd.Parameters.AddWithValue("@quota", quota);
        cmd.Parameters.AddWithValue("@cooldown", cooldownMinutes);
        cmd.Parameters.AddWithValue("@note", note);
        cmd.Parameters.AddWithValue("@t", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.ExecuteNonQuery();
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

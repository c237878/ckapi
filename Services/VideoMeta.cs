using ckapi.Utils;
using Microsoft.Data.Sqlite;

namespace ckapi.Services;

/// <summary>
/// 影片四个扩展结构位（片商 / 题材标签 / 关联合辑 / 外部档案链接）的读写。
///
/// 详情页要一次带出这四块、编辑要整组替换、片商与标签的管理页又共用同一套名字归一规则，
/// 所以集中在这里，不在各控制器里各写一遍 SQL（卡片 SELECT 列表当年就是栽在七处各写一遍）。
///
/// 三条贯穿所有写入的规矩：
///   1. **外键约束是关掉的**（SQLite 默认），删影片 / 删片商 / 删标签时必须显式清子表，
///      否则会留下指向不存在行的关系，界面上一片空白点不动。
///   2. 名字进词表前先归一（正名 → 别名），归不到才新建。"麦当娜 / Madonna / マドンナ"
///      必须是同一家，否则按片商浏览等于没有。
///   3. 标签只有人工路径能新建；AI 路径写 tag_suggestions（见 TagController）。
///      词表失控是标签功能唯一的死法，所以把口子收在库里而不是靠提示词自觉。
/// </summary>
public static class VideoMeta
{
    public sealed record TagRef(string Id, string Name, string Source);
    public sealed record LinkRef(string Id, string Kind, string Url);
    public sealed record PeerRef(string Id, string Code, string Name, int Position);
    public sealed record GroupRef(string Id, string Name, int Position, List<PeerRef> Peers);

    private static string NewId() => Guid.NewGuid().ToString("N").ToUpper();

    // ---------------------------------------------------------------- 读

    // 片商是一部片的一个值（videos.studioid），读的时候随卡片那一条 SELECT 一起带出来，
    // 不再单独查一次——原来 Studios(videoId) 那个多对多读法已经跟着 v9 一起废掉了。

    public static List<TagRef> Tags(SqliteConnection conn, string videoId)
    {
        var list = new List<TagRef>();
        const string sql = @"
            SELECT t.id, t.name, vt.source
            FROM video_tags vt JOIN tags t ON t.id = vt.tag_id
            WHERE vt.video_id = @id
            ORDER BY t.name";
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", videoId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(new TagRef(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return list;
    }

    public static List<LinkRef> Links(SqliteConnection conn, string videoId)
    {
        var list = new List<LinkRef>();
        const string sql = "SELECT id, kind, url FROM video_links WHERE video_id = @id ORDER BY kind, url";
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", videoId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(new LinkRef(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return list;
    }

    /// <summary>
    /// 这部片所在的合辑，每个组带上组内全部影片（按 position 排）。
    /// 详情页的"接着看下一部"就靠这个列表，所以一次读全，别让前端再逐组请求。
    /// </summary>
    public static List<GroupRef> Groups(SqliteConnection conn, string videoId)
    {
        var mine = new List<(string Id, string Name, int Position)>();
        const string selfSql = @"
            SELECT g.id, g.name, gi.position
            FROM video_group_items gi JOIN video_groups g ON g.id = gi.group_id
            WHERE gi.video_id = @id
            ORDER BY g.ctime, g.name";
        using (var cmd = new SqliteCommand(selfSql, conn))
        {
            cmd.Parameters.AddWithValue("@id", videoId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                mine.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
        }

        var result = new List<GroupRef>(mine.Count);
        const string peerSql = @"
            SELECT v.id, IFNULL(v.code, ''), v.name, gi.position
            FROM video_group_items gi JOIN videos v ON v.id = gi.video_id
            WHERE gi.group_id = @g
            ORDER BY gi.position, v.code";
        foreach (var (id, name, position) in mine)
        {
            var peers = new List<PeerRef>();
            using var cmd = new SqliteCommand(peerSql, conn);
            cmd.Parameters.AddWithValue("@g", id);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                peers.Add(new PeerRef(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3)));
            result.Add(new GroupRef(id, name, position, peers));
        }
        return result;
    }

    // ---------------------------------------------------------------- 词表归一

    /// <summary>
    /// 名字 → 片商 id。先按正名精确匹配，再按别名匹配；都归不到且允许新建时才建。
    /// 大小写与首尾空白不算区别（"prestage" 与 "Prestige" 这种混写在这批数据里真存在）。
    /// </summary>
    public static string? FindStudio(SqliteConnection conn, string name)
    {
        var key = (name ?? "").Trim();
        if (key.Length == 0) return null;

        const string sql = @"
            SELECT id FROM studios WHERE name = @n COLLATE NOCASE
            UNION
            SELECT a.studio_id FROM studio_aliases a JOIN studios s ON s.id = a.studio_id
            WHERE a.alias = @n COLLATE NOCASE
            LIMIT 1";
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@n", key);
        return cmd.ExecuteScalar() as string;
    }

    public static string EnsureStudio(SqliteConnection conn, SqliteTransaction? tx, string name)
    {
        var key = (name ?? "").Trim();
        var found = FindStudio(conn, key);
        if (found is not null) return found;

        var id = NewId();
        const string sql = "INSERT INTO studios (id, name, ctime, utime) VALUES (@id, @n, @t, @t)";
        using var cmd = new SqliteCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@n", key);
        cmd.Parameters.AddWithValue("@t", Now());
        cmd.ExecuteNonQuery();
        return id;
    }

    /// <summary>名字 → 标签 id（正名或别名）。归不到返回 null，由调用方决定是建还是进待审队列</summary>
    public static string? FindTag(SqliteConnection conn, string name)
    {
        var key = (name ?? "").Trim();
        if (key.Length == 0) return null;

        const string sql = @"
            SELECT id FROM tags WHERE name = @n COLLATE NOCASE
            UNION
            SELECT a.tag_id FROM tag_aliases a JOIN tags t ON t.id = a.tag_id
            WHERE a.alias = @n COLLATE NOCASE
            LIMIT 1";
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@n", key);
        return cmd.ExecuteScalar() as string;
    }

    public static string EnsureTag(SqliteConnection conn, SqliteTransaction? tx, string name)
    {
        var key = (name ?? "").Trim();
        var found = FindTag(conn, key);
        if (found is not null) return found;

        var id = NewId();
        using var cmd = new SqliteCommand("INSERT INTO tags (id, name, ctime) VALUES (@id, @n, @t)", conn, tx);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@n", key);
        cmd.Parameters.AddWithValue("@t", Now());
        cmd.ExecuteNonQuery();
        return id;
    }

    // ---------------------------------------------------------------- 写

    /// <summary>
    /// 解析一部片的那一家片商：给 id 就用 id（认不出直接报错，别静默丢掉——界面会显示"挂上了"而实际没挂）；
    /// 只给 name 就先按正名/别名归一，归不到才新建；两个都空表示"这部片没有片商"。
    /// </summary>
    public static (string? Id, bool Created) ResolveStudio(SqliteConnection conn, string? id, string? name)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            using var check = new SqliteCommand("SELECT name FROM studios WHERE id = @id", conn);
            check.Parameters.AddWithValue("@id", id.Trim());
            if (check.ExecuteScalar() is null) throw new MetaException($"片商不存在：{id}");
            return (id.Trim(), false);
        }

        var key = (name ?? "").Trim();
        if (key.Length == 0) return (null, false);

        var found = FindStudio(conn, key);
        return found is not null ? (found, false) : (EnsureStudio(conn, null, key), true);
    }

    /// <summary>写 videos.studioid（null 就是清空）</summary>
    public static void SetStudio(SqliteConnection conn, string videoId, string? studioId)
    {
        using var cmd = new SqliteCommand("UPDATE videos SET studioid = @s WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("@s", (object?)studioId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", videoId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 把这家片商补到同系列**还没填片商**的影片上，返回补了几部。
    /// 一个系列基本就是同一家在做，逐部手填没有意义；已有片商的绝不动——
    /// 系列里混进别家（联名、复刻、换牌）时那几部已有的结论得留着。
    /// </summary>
    public static int FillSeriesStudios(SqliteConnection conn, string videoId, string? studioId)
    {
        if (string.IsNullOrWhiteSpace(studioId)) return 0;

        string? seriesId;
        using (var cmd = new SqliteCommand("SELECT seriesid FROM videos WHERE id = @id", conn))
        {
            cmd.Parameters.AddWithValue("@id", videoId);
            seriesId = cmd.ExecuteScalar() as string;
        }
        if (string.IsNullOrWhiteSpace(seriesId)) return 0;

        using var upd = new SqliteCommand(@"
            UPDATE videos SET studioid = @s
            WHERE seriesid = @ser AND id <> @id AND (studioid IS NULL OR studioid = '')", conn);
        upd.Parameters.AddWithValue("@s", studioId);
        upd.Parameters.AddWithValue("@ser", seriesId);
        upd.Parameters.AddWithValue("@id", videoId);
        return upd.ExecuteNonQuery();
    }

    /// <summary>
    /// 整组替换标签。createMissing=false 时归不到的名字会被跳过并回给调用方
    /// ——AI 路径就是这个形状：能挂的挂上，新词一个都不进词表。
    /// </summary>
    public static (int Attached, List<string> Unknown) SetTags(
        SqliteConnection conn, string videoId, List<string>? names, string source, bool createMissing)
    {
        var ids = new List<string>();
        var unknown = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in names ?? new List<string>())
        {
            var name = (raw ?? "").Trim();
            if (name.Length == 0) continue;

            var id = FindTag(conn, name);
            if (id is null)
            {
                if (!createMissing) { unknown.Add(name); continue; }
                id = EnsureTag(conn, null, name);
            }
            if (seen.Add(id)) ids.Add(id);
        }

        using (var tx = conn.BeginTransaction())
        {
            using (var del = new SqliteCommand("DELETE FROM video_tags WHERE video_id = @id", conn, tx))
            {
                del.Parameters.AddWithValue("@id", videoId);
                del.ExecuteNonQuery();
            }
            using var ins = new SqliteCommand(
                "INSERT INTO video_tags (video_id, tag_id, source, ctime) VALUES (@v, @t, @s, @time)", conn, tx);
            foreach (var id in ids)
            {
                ins.Parameters.Clear();
                ins.Parameters.AddWithValue("@v", videoId);
                ins.Parameters.AddWithValue("@t", id);
                ins.Parameters.AddWithValue("@s", source == "ai" ? "ai" : "manual");
                ins.Parameters.AddWithValue("@time", Now());
                ins.ExecuteNonQuery();
            }
            tx.Commit();
        }
        return (ids.Count, unknown);
    }

    /// <summary>整组替换外部链接，校验与去重共用 Utils/Links（与演员外链同一套规则）</summary>
    public static int SetLinks(SqliteConnection conn, string videoId, List<ActorLink>? links)
    {
        var clean = Utils.Links.Normalize(links);

        using (var tx = conn.BeginTransaction())
        {
            using (var del = new SqliteCommand("DELETE FROM video_links WHERE video_id = @id", conn, tx))
            {
                del.Parameters.AddWithValue("@id", videoId);
                del.ExecuteNonQuery();
            }
            using var ins = new SqliteCommand(
                "INSERT INTO video_links (id, video_id, kind, url) VALUES (@id, @v, @k, @u)", conn, tx);
            foreach (var link in clean)
            {
                ins.Parameters.Clear();
                ins.Parameters.AddWithValue("@id", NewId());
                ins.Parameters.AddWithValue("@v", videoId);
                ins.Parameters.AddWithValue("@k", link.Kind);
                ins.Parameters.AddWithValue("@u", link.Url);
                ins.ExecuteNonQuery();
            }
            tx.Commit();
        }
        return clean.Count;
    }

    // ---------------------------------------------------------------- 合辑

    public static string EnsureGroup(SqliteConnection conn, string? id, string name)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            using var check = new SqliteCommand("SELECT name FROM video_groups WHERE id = @id", conn);
            check.Parameters.AddWithValue("@id", id);
            if (check.ExecuteScalar() is null) throw new MetaException($"合辑不存在：{id}");
            return id;
        }

        var key = (name ?? "").Trim();
        if (key.Length == 0) throw new MetaException("新建合辑要给名字");

        var newId = NewId();
        using var cmd = new SqliteCommand("INSERT INTO video_groups (id, name, ctime) VALUES (@id, @n, @t)", conn);
        cmd.Parameters.AddWithValue("@id", newId);
        cmd.Parameters.AddWithValue("@n", key);
        cmd.Parameters.AddWithValue("@t", Now());
        cmd.ExecuteNonQuery();
        return newId;
    }

    /// <summary>把影片放进合辑的末尾（position 取组内最大值 +10，留出手工插空的余量）</summary>
    public static void AddToGroup(SqliteConnection conn, string groupId, string videoId)
    {
        const string sql = @"
            INSERT OR IGNORE INTO video_group_items (group_id, video_id, position)
            SELECT @g, @v, IFNULL(MAX(position), 0) + 10 FROM video_group_items WHERE group_id = @g";
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@g", groupId);
        cmd.Parameters.AddWithValue("@v", videoId);
        cmd.ExecuteNonQuery();
    }

    public static void RemoveFromGroup(SqliteConnection conn, string groupId, string videoId)
    {
        using var cmd = new SqliteCommand(
            "DELETE FROM video_group_items WHERE group_id = @g AND video_id = @v", conn);
        cmd.Parameters.AddWithValue("@g", groupId);
        cmd.Parameters.AddWithValue("@v", videoId);
        cmd.ExecuteNonQuery();
    }

    // ---------------------------------------------------------------- 删除时的子表清理

    /// <summary>
    /// 删一部影片要顺手清掉的东西。外键没开，漏一个就留下指向不存在行的关系。
    /// 点赞与演员/片商/标签关系都按 video_id 存，一起清。
    /// </summary>
    public static void PurgeVideo(SqliteConnection conn, string videoId, SqliteTransaction? tx = null)
    {
        foreach (var sql in new[]
                 {
                     "DELETE FROM video_tags WHERE video_id = @id",
                     "DELETE FROM video_links WHERE video_id = @id",
                     "DELETE FROM video_group_items WHERE video_id = @id",
                     "DELETE FROM tag_suggestions WHERE video_id = @id",
                 })
        {
            using var cmd = new SqliteCommand(sql, conn, tx);
            cmd.Parameters.AddWithValue("@id", videoId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 删片商：先把引用它的影片摘干净（studioid 是指向 studios 的外键式引用，外键没开，
    /// 漏了就会在详情页显示成一家不存在的片商），别名表跟着 studio_id 一起删。
    /// </summary>
    public static void PurgeStudio(SqliteConnection conn, string studioId, SqliteTransaction? tx = null)
    {
        using (var cmd = new SqliteCommand("UPDATE videos SET studioid = NULL WHERE studioid = @id", conn, tx))
        {
            cmd.Parameters.AddWithValue("@id", studioId);
            cmd.ExecuteNonQuery();
        }
        using var del = new SqliteCommand("DELETE FROM studio_aliases WHERE studio_id = @id", conn, tx);
        del.Parameters.AddWithValue("@id", studioId);
        del.ExecuteNonQuery();
    }

    /// <summary>删标签：清挂接、清别名、清还没处理的候选词</summary>
    public static void PurgeTag(SqliteConnection conn, string tagId, SqliteTransaction? tx = null)
    {
        foreach (var sql in new[]
                 {
                     "DELETE FROM video_tags WHERE tag_id = @id",
                     "DELETE FROM tag_aliases WHERE tag_id = @id",
                     "DELETE FROM tag_suggestions WHERE tag_id = @id",
                 })
        {
            using var cmd = new SqliteCommand(sql, conn, tx);
            cmd.Parameters.AddWithValue("@id", tagId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 把 from 标签合并进 to：挂接改挂（同一部片两个都有的，INSERT OR IGNORE 之后删掉多余的，
    /// 原行仍会被清掉所以不会留空档）、别名搬过去、from 自己的候选词转成指向正名。
    /// 与演员合并同一套做法。
    /// </summary>
    public static int MergeTag(SqliteConnection conn, string fromId, string toId)
    {
        using var tx = conn.BeginTransaction();
        using (var move = new SqliteCommand(@"
            INSERT OR IGNORE INTO video_tags (video_id, tag_id, source, ctime)
            SELECT vt.video_id, @to, vt.source, vt.ctime FROM video_tags vt WHERE vt.tag_id = @from", conn, tx))
        {
            move.Parameters.AddWithValue("@to", toId);
            move.Parameters.AddWithValue("@from", fromId);
            move.ExecuteNonQuery();
        }
        using (var del = new SqliteCommand("DELETE FROM video_tags WHERE tag_id = @from", conn, tx))
        {
            del.Parameters.AddWithValue("@from", fromId);
            del.ExecuteNonQuery();
        }
        using (var alias = new SqliteCommand(@"
            INSERT OR IGNORE INTO tag_aliases (tag_id, alias)
            SELECT @to, alias FROM tag_aliases WHERE tag_id = @from", conn, tx))
        {
            alias.Parameters.AddWithValue("@to", toId);
            alias.Parameters.AddWithValue("@from", fromId);
            alias.ExecuteNonQuery();
        }
        using (var drop = new SqliteCommand("DELETE FROM tag_aliases WHERE tag_id = @from", conn, tx))
        {
            drop.Parameters.AddWithValue("@from", fromId);
            drop.ExecuteNonQuery();
        }
        using (var sugg = new SqliteCommand("UPDATE tag_suggestions SET tag_id = @to WHERE tag_id = @from", conn, tx))
        {
            sugg.Parameters.AddWithValue("@to", toId);
            sugg.Parameters.AddWithValue("@from", fromId);
            sugg.ExecuteNonQuery();
        }
        using (var rm = new SqliteCommand("DELETE FROM tags WHERE id = @from", conn, tx))
        {
            rm.Parameters.AddWithValue("@from", fromId);
            rm.ExecuteNonQuery();
        }
        tx.Commit();
        return 1;
    }

    // ---------------------------------------------------------------- 小工具

    public static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>参数不合法：控制器统一 catch 成 200 + success=false，不抛 500</summary>
    public sealed class MetaException : Exception
    {
        public MetaException(string message) : base(message) { }
    }

    /// <summary>
    /// 发行日期口径与演员生日一致：只收 YYYY / YYYY-MM / YYYY-MM-DD，
    /// 站点只给到月份就存 2024-03，**不补成 01 号**——补出来的是假数据。
    /// </summary>
    public static string? NormalizeReleaseDate(string? raw)
    {
        var v = (raw ?? "").Trim();
        if (v.Length == 0) return null;
        return System.Text.RegularExpressions.Regex.IsMatch(v, @"^\d{4}(-\d{2}(-\d{2})?)?$") ? v : null;
    }
}

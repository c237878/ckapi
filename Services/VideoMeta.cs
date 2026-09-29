using ckapi.Utils;
using Microsoft.Data.Sqlite;

namespace ckapi.Services;

/// <summary>
/// 影片两个扩展结构位（片商 / 外部档案链接）的读写，外加删影片、删片商时的子表清理。
///
/// 详情页要一次带出这两块、片商管理页与影片挑选器共用同一套名字归一规则，
/// 所以集中在这里，不在各控制器里各写一遍 SQL（卡片 SELECT 列表当年就是栽在七处各写一遍）。
///
/// 两条贯穿所有写入的规矩：
///   1. **外键约束是关掉的**（SQLite 默认），删影片 / 删片商时必须显式清子表，
///      否则会留下指向不存在行的关系，界面上一片空白点不动。
///   2. 名字进词表前先归一（正名 → 别名），归不到才新建。"麦当娜 / Madonna / マドンナ"
///      必须是同一家，否则按片商浏览等于没有。
/// </summary>
public static class VideoMeta
{
    public sealed record LinkRef(string Id, string Kind, string Url);

    private static string NewId() => Guid.NewGuid().ToString("N").ToUpper();

    // ---------------------------------------------------------------- 读

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
    ///
    /// 只补日本 av：片商实体是给日本 av 建的那份词表（抓取归一、别名都按那套走），
    /// 同一个系列里挂着的欧美片、解说/混剪类影片不是同一家出的，顺手补上等于凭空写错。
    /// '日本' / 'av' 是 设置 → 地区与分类 里的规范取值，在那里改名要同步改这里。
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
            WHERE seriesid = @ser AND id <> @id
              AND country = '日本' AND category = 'av'
              AND (studioid IS NULL OR studioid = '')", conn);
        upd.Parameters.AddWithValue("@s", studioId);
        upd.Parameters.AddWithValue("@ser", seriesId);
        upd.Parameters.AddWithValue("@id", videoId);
        return upd.ExecuteNonQuery();
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


    // ---------------------------------------------------------------- 删除时的子表清理

    /// <summary>
    /// 删一部影片要顺手清掉的东西。外键没开，漏一个就留下指向不存在行的关系。
    /// 演员与片商不用在这儿清：前者是 video_actors 由调用方自己删，后者现在是 videos 上的一列，行没了就没了。
    /// </summary>
    public static void PurgeVideo(SqliteConnection conn, string videoId, SqliteTransaction? tx = null)
    {
        using (var cmd = new SqliteCommand("DELETE FROM video_links WHERE video_id = @id", conn, tx))
        {
            cmd.Parameters.AddWithValue("@id", videoId);
            cmd.ExecuteNonQuery();
        }

        // 版本行属于这部片（v11）：片没了，它名下的每一份文件记录一起走，
        // 否则 video_files 里会留下指向不存在影片的孤儿行
        using (var files = new SqliteCommand("DELETE FROM video_files WHERE video_id = @id", conn, tx))
        {
            files.Parameters.AddWithValue("@id", videoId);
            files.ExecuteNonQuery();
        }

        // 点赞记录带上了版本列（file_id），删片时按影片清完就够了，不必再按版本补一刀
        using var likes = new SqliteCommand("DELETE FROM video_likes WHERE video_id = @id", conn, tx);
        likes.Parameters.AddWithValue("@id", videoId);
        likes.ExecuteNonQuery();
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

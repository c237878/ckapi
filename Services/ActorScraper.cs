using ckapi.Utils;
using Microsoft.Data.Sqlite;

namespace ckapi.Services;

/// <summary>一次抓取要做哪几件事。头像与资料共用同一次查档，别问两遍。</summary>
[Flags]
public enum Want
{
    Avatar = 1,
    Profile = 2,
    All = Avatar | Profile
}

/// <summary>
/// 按姓名与曾用名去 av-wiki 查档案，然后把缺的补上：头像、出生日期、简介。
///
/// 判定"确定"的口径在 Utils.AvWiki.Certify —— 认错人比抓不到严重得多。
/// 落库一律遵循"只填空着的地方"：已经写过的简介、手工挑过的主图，都不覆盖。
/// </summary>
public sealed class ActorScraper
{
    private readonly ILogger<ActorScraper> _logger;

    public ActorScraper(ILogger<ActorScraper> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 出现在查重候选里的演员。抓取要跳过他们：同一张脸在两个名字下各存一份、
    /// 简介也是两份，之后合并时还得处理两套磁盘目录，比先合并再抓麻烦得多。
    /// </summary>
    public HashSet<string> DuplicateRiskIds(SqliteConnection conn)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        const string sql = @"
            SELECT t1.actor_id FROM actor_aliases t1
              JOIN actor_aliases t2 ON t1.alias = t2.alias AND t1.actor_id < t2.actor_id
            UNION
            SELECT t2.actor_id FROM actor_aliases t1
              JOIN actor_aliases t2 ON t1.alias = t2.alias AND t1.actor_id < t2.actor_id
            UNION
            SELECT a1.id FROM actors a1 JOIN actor_aliases t ON t.alias = a1.name
            UNION
            SELECT t.actor_id FROM actors a1 JOIN actor_aliases t ON t.alias = a1.name";
        using var cmd = new SqliteCommand(sql, conn);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetString(0));
        return ids;
    }

    /// <summary>
    /// 还缺东西的演员，按影片数多的先来（先补最常用到的那些）。
    /// 掩码决定"缺什么算缺"，所以跑完头像再跑资料时，候选集会自己缩小。
    /// </summary>
    public List<string> Candidates(SqliteConnection conn, HashSet<string> risk, Want want)
    {
        var missing = want switch
        {
            Want.Avatar => "NOT EXISTS (SELECT 1 FROM actor_images i WHERE i.actor_id = a.id)",
            Want.Profile => "(a.birthdate IS NULL OR a.bio IS NULL)",
            _ => "(NOT EXISTS (SELECT 1 FROM actor_images i WHERE i.actor_id = a.id)" +
                 " OR a.birthdate IS NULL OR a.bio IS NULL)"
        };

        var list = new List<string>();
        using var cmd = new SqliteCommand(
            $@"SELECT a.id FROM actors a
                WHERE {missing}
                ORDER BY (SELECT COUNT(*) FROM video_actors va WHERE va.actor_id = a.id) DESC, a.name", conn);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            if (!risk.Contains(id)) list.Add(id);
        }

        return list;
    }

    /// <summary>
    /// 抓一位演员。返回的 Message 既是给用户看的说明，也是"为什么什么都没抓"的记录。
    /// conn 由调用方持有：批量跑的时候复用一条连接，别一位演员开一次。
    /// </summary>
    public async Task<(bool Ok, string Message)> ScrapeAsync(
        SqliteConnection conn, string id, string posterDir, HashSet<string> risk, Want want, CancellationToken ct)
    {
        string? name = null, birthdate = null, bio = null;
        var images = 0;
        var ourNames = new List<string>();
        var slugs = new List<string>();

        using (var q = new SqliteCommand("SELECT name, birthdate, bio FROM actors WHERE id = @id", conn))
        {
            q.Parameters.Add(new SqliteParameter("@id", id));
            using var reader = q.ExecuteReader();
            if (!reader.Read()) return (false, "演员不存在");

            name = reader.GetString(0);
            birthdate = reader.IsDBNull(1) ? null : reader.GetString(1);
            bio = reader.IsDBNull(2) ? null : reader.GetString(2);
        }

        if (string.IsNullOrEmpty(name)) return (false, "演员姓名为空，没法查");
        ourNames.Add(name);

        using (var q = new SqliteCommand("SELECT alias FROM actor_aliases WHERE actor_id = @id", conn))
        {
            q.Parameters.Add(new SqliteParameter("@id", id));
            using var reader = q.ExecuteReader();
            while (reader.Read()) ourNames.Add(reader.GetString(0));
        }

        using (var q = new SqliteCommand(
                   "SELECT url FROM actor_links WHERE actor_id = @id AND url LIKE '%av-wiki.net%'", conn))
        {
            q.Parameters.Add(new SqliteParameter("@id", id));
            using var reader = q.ExecuteReader();
            while (reader.Read())
            {
                var slug = AvWiki.SlugOf(reader.GetString(0));
                if (!string.IsNullOrEmpty(slug)) slugs.Add(slug);
            }
        }

        using (var q = new SqliteCommand("SELECT COUNT(*) FROM actor_images WHERE actor_id = @id", conn))
        {
            q.Parameters.Add(new SqliteParameter("@id", id));
            images = Convert.ToInt32(q.ExecuteScalar());
        }

        var needAvatar = want.HasFlag(Want.Avatar) && images == 0;
        var needProfile = want.HasFlag(Want.Profile) && (birthdate is null || string.IsNullOrWhiteSpace(bio));

        if (risk.Contains(id)) return (false, "该演员还在查重候选里，先合并再抓");
        if (!needAvatar && !needProfile)
        {
            if (want.HasFlag(Want.Avatar) && images > 0) return (false, $"已有 {images} 张照片，不动手");
            return (false, "该填的都填好了");
        }

        // 查询顺序：档案链接（我们自己的数据已经指向它）→ 含假名的曾用名（最接近站点的写法）
        // → 其余曾用名 → 本名。每人最多问 5 次，问不到就算了
        var probes = slugs.Select(s => ("slug", s))
            .Concat(OrderQueries(ourNames).Take(5).Select(q => ("search", q)))
            .ToList();

        foreach (var (kind, value) in probes)
        {
            List<AvWiki.Profile> hits;
            try
            {
                hits = kind == "slug"
                    ? await AvWiki.BySlugAsync(value, ct)
                    : await AvWiki.SearchAsync(value, ct);
            }
            catch (Exception ex) when (IsNetworkFault(ex, ct))
            {
                return (false, $"站点请求失败（{ex.Message}）");
            }

            var hit = AvWiki.Certify(hits, ourNames);
            if (!hit.Ok) continue;

            var profile = hit.Profile!.Value;
            var done = new List<string>();

            if (needAvatar)
            {
                try
                {
                    var dl = await AvWiki.DownloadAsync(profile.Portrait!, ct);
                    if (dl is null) return (false, $"档案「{profile.Name}」的头像下载失败");

                    // 文件名固定用「默认」：ImageIndex 补选主图时第一个就认它，不用额外置位
                    var dir = Path.Combine(posterDir, id);
                    Directory.CreateDirectory(dir);
                    var target = Path.Combine(dir, $"默认{dl.Value.Ext}");
                    var temp = target + ".tmp";
                    await File.WriteAllBytesAsync(temp, dl.Value.Bytes, ct);
                    File.Move(temp, target, overwrite: true);
                    ImageIndex.SyncActor(conn, id, dir);
                    done.Add("头像");
                }
                catch (Exception ex) when (IsNetworkFault(ex, ct))
                {
                    return (false, $"头像下载失败（{ex.Message}）");
                }
            }

            if (needProfile)
            {
                // COALESCE 保证只填空着的地方：人写过的简介不会被机器覆盖
                using var upd = new SqliteCommand(
                    @"UPDATE actors
                         SET birthdate = COALESCE(birthdate, @b),
                             bio       = COALESCE(NULLIF(TRIM(bio), ''), @i)
                       WHERE id = @id", conn);
                upd.Parameters.Add(new SqliteParameter("@b", (object?)profile.Birthdate ?? DBNull.Value));
                upd.Parameters.Add(new SqliteParameter("@i", (object?)profile.Intro ?? DBNull.Value));
                upd.Parameters.Add(new SqliteParameter("@id", id));
                upd.ExecuteNonQuery();

                if (birthdate is null && profile.Birthdate is not null) done.Add($"生日 {profile.Birthdate}");
                if (string.IsNullOrWhiteSpace(bio) && !string.IsNullOrWhiteSpace(profile.Intro)) done.Add("简介");
            }

            if (done.Count == 0)
                return (false, $"档案「{profile.Name}」里没有可补的字段");

            // 留一行审计：批量跑的时候这是唯一能回看"这些数据是从哪个档案抓来的"的地方
            _logger.LogInformation(
                "已抓取 {What}：演员 {ActorId}「{OurName}」← 档案「{ProfileName}」(https://av-wiki.net/av-actress/{Slug})",
                string.Join('/', done), id, name, profile.Name, profile.Slug);

            return (true, $"已抓到 {string.Join('、', done)}（来自「{profile.Name}」）");
        }

        return (false, "没找到能确认的档案");
    }

    /// <summary>
    /// 是不是"站点那边出问题了"。HttpClient 超时也抛 TaskCanceledException，
    /// 所以只能看调用方的 token 有没有被取消：没取消就是故障，取消了是要正常收尾。
    /// </summary>
    private static bool IsNetworkFault(Exception ex, CancellationToken ct)
        => ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested);

    /// <summary>含假名的曾用名排前面：站上的 tag 名基本就是假名/日文汉字写法</summary>
    private static List<string> OrderQueries(List<string> names)
    {
        return names
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct()
            .OrderByDescending(n => n.Any(c => c >= 0x3041 && c <= 0x30F6))
            .ThenByDescending(n => n.Length)
            .ToList();
    }
}

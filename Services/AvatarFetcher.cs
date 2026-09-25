using ckapi.Utils;
using Microsoft.Data.Sqlite;

namespace ckapi.Services;

/// <summary>
/// 按姓名与曾用名去 av-wiki 找头像并落盘。抓取判定"确定"的口径在 Utils.AvWiki.Certify，
/// 这里只管"查谁、放哪儿、要不要跳过"。
///
/// 从控制器里搬出来是因为批量任务要在请求之外跑，那段逻辑不能既服务一次点击又服务一个后台循环。
/// </summary>
public sealed class AvatarFetcher
{
    private readonly ILogger<AvatarFetcher> _logger;

    public AvatarFetcher(ILogger<AvatarFetcher> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 出现在查重候选里的演员。抓头像要跳过他们：同一张脸在两个名字下各存一份，
    /// 之后合并时还得处理两套磁盘目录，比先合并再抓麻烦得多。
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

    /// <summary>还没有任何图片、且不在查重候选里的演员，按影片数多的先来（先补最常用到的脸）</summary>
    public List<string> Candidates(SqliteConnection conn, HashSet<string> risk)
    {
        var list = new List<string>();
        using var cmd = new SqliteCommand(
            @"SELECT a.id FROM actors a
               WHERE NOT EXISTS (SELECT 1 FROM actor_images i WHERE i.actor_id = a.id)
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
    /// 给一位演员抓头像。返回的 Message 既是给用户看的说明，也是"为什么没抓"的记录。
    /// conn 由调用方持有：批量跑的时候复用一条连接，别一位演员开一次。
    /// </summary>
    public async Task<(bool Ok, string Message)> FetchOneAsync(
        SqliteConnection conn, string id, string posterDir, HashSet<string> risk, CancellationToken ct)
    {
        string? name = null;
        var ourNames = new List<string>();
        var slugs = new List<string>();

        using (var q = new SqliteCommand("SELECT name FROM actors WHERE id = @id", conn))
        {
            q.Parameters.Add(new SqliteParameter("@id", id));
            name = q.ExecuteScalar()?.ToString();
        }

        if (string.IsNullOrEmpty(name)) return (false, "演员不存在");
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

        int images;
        using (var q = new SqliteCommand("SELECT COUNT(*) FROM actor_images WHERE actor_id = @id", conn))
        {
            q.Parameters.Add(new SqliteParameter("@id", id));
            images = Convert.ToInt32(q.ExecuteScalar());
        }

        if (images > 0) return (false, $"已有 {images} 张照片，不动手");
        if (risk.Contains(id)) return (false, "该演员还在查重候选里，先合并再抓");

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
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException and not OperationCanceledException)
            {
                return (false, $"站点请求失败（{ex.Message}）");
            }

            var hit = AvWiki.Certify(hits, ourNames);
            if (!hit.Ok) continue;

            var profile = hit.Profile!.Value;
            byte[] bytes;
            string ext;
            try
            {
                var dl = await AvWiki.DownloadAsync(profile.Portrait!, ct);
                if (dl is null) return (false, $"档案「{profile.Name}」的头像下载失败");
                (bytes, ext) = dl.Value;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException and not OperationCanceledException)
            {
                return (false, $"头像下载失败（{ex.Message}）");
            }

            // 文件名固定用「默认」：ImageIndex 补选主图时第一个就认它，不用额外置位
            var dir = Path.Combine(posterDir, id);
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, $"默认{ext}");
            var temp = target + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes, ct);
            File.Move(temp, target, overwrite: true);

            ImageIndex.SyncActor(conn, id, dir);

            // 留一行审计：批量跑的时候这是唯一能回看"这张脸是从哪个档案抓来的"的地方
            _logger.LogInformation(
                "头像已抓取：演员 {ActorId}「{OurName}」← 档案「{ProfileName}」(https://av-wiki.net/av-actress/{Slug})",
                id, name, profile.Name, profile.Slug);

            return (true, $"已抓到「{profile.Name}」的头像（av-wiki.net/av-actress/{profile.Slug}）");
        }

        return (false, "没找到能确认的档案");
    }

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

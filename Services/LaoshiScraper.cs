using System.Text.RegularExpressions;
using ckapi.Utils;
using Microsoft.Data.Sqlite;

namespace ckapi.Services;

/// <summary>
/// 老师图鉴（laoshi.ink）这条源：只补三样 —— 出生日期、别名（日文名/罗马音/曾用名）、头像。
/// 简介按他的口径明确不接：那站冷门页的"简介"是「某某是日本演艺人物榜第 700 位收录人物」
/// 这类榜单套话，写进 bio 不如留空。
///
/// 与 av-wiki 那条源的两处关键差别：
///  · 档案地址是 actor-001 这种不透明编号，名字拼不出 URL，所以先取一次索引页在内存里对着查
///    （686 条姓名→编号，1 个请求；不落库，我们不把对方档案结构搬进自己库里）；
///  · 别名是往 actor_aliases 里加东西，而这张表同时是"重复演员候选"的依据 ——
///    所以只写没人占用的写法：某个写法已经是别人的姓名或别名，就不塞进来，免得凭空造出假候选。
/// </summary>
public sealed class LaoshiScraper : IActorSource
{
    public string Key => "laoshi";
    public string Label => "老师图鉴";
    public string Host => LaoshiWiki.Host;

    private readonly ILogger<LaoshiScraper> _logger;

    public LaoshiScraper(ILogger<LaoshiScraper> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 还缺东西、**并且站上有档案**的演员。"有没有罗马音写法"用 GLOB 判：
    /// 整条别名只由拉丁字母和空格组成才算有。
    ///
    /// 站上有没有这个人靠内存里的索引判（1 个请求换 686 个名字），必须先过一道：
    /// 批量任务的间隔是按每一位候选付的，1481 位里只有 119 位在站上有档案，
    /// 不过滤就是让 8 秒的刹车踩在一堆根本不会发请求的人身上。
    /// </summary>
    public async Task<List<string>> CandidatesAsync(
        SqliteConnection conn, HashSet<string> risk, Want want, CancellationToken ct)
    {
        const string noPortrait = "NOT EXISTS (SELECT 1 FROM actor_images i WHERE i.actor_id = a.id)";
        const string noLatin = @"NOT EXISTS (SELECT 1 FROM actor_aliases t WHERE t.actor_id = a.id
                        AND t.alias GLOB '[A-Za-z]*' AND t.alias NOT GLOB '*[^A-Za-z ]*')";
        var missing = want switch
        {
            Want.Avatar => noPortrait,
            Want.Profile => $"(a.birthdate IS NULL OR {noLatin})",
            _ => $"({noPortrait} OR a.birthdate IS NULL OR {noLatin})"
        };

        var rows = new List<(string Id, List<string> Names)>();
        using (var cmd = new SqliteCommand(
                   $@"SELECT a.id, a.name,
                             (SELECT group_concat(alias, char(31)) FROM actor_aliases aa WHERE aa.actor_id = a.id)
                        FROM actors a
                        WHERE {missing} AND {ActorGate.EligibleSql}
                        ORDER BY (SELECT COUNT(*) FROM video_actors va WHERE va.actor_id = a.id) DESC, a.name", conn))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var names = new List<string> { reader.GetString(1) };
                var aliases = reader.IsDBNull(2) ? null : reader.GetString(2);
                if (!string.IsNullOrEmpty(aliases)) names.AddRange(aliases.Split('\u001f'));
                rows.Add((reader.GetString(0), names));
            }
        }

        Dictionary<string, LaoshiWiki.Hit> index;
        try
        {
            index = await LaoshiWiki.IndexAsync(ct);
        }
        catch (Exception ex) when (ActorGate.IsNetworkFault(ex, ct))
        {
            throw new InvalidOperationException($"取不到老师图鉴的人物索引：{ex.Message}", ex);
        }

        return rows
            .Where(r => !risk.Contains(r.Id) && r.Names.Any(n => index.ContainsKey(LaoshiWiki.NoSpace(n))))
            .Select(r => r.Id)
            .ToList();
    }

    public async Task<(bool Ok, string Message)> ScrapeAsync(
        SqliteConnection conn, string id, string posterDir, HashSet<string> risk, Want want, CancellationToken ct)
    {
        string? name, birthdate;
        var ourNames = new List<string>();

        using (var q = new SqliteCommand("SELECT name, birthdate FROM actors WHERE id = @id", conn))
        {
            q.Parameters.Add(new SqliteParameter("@id", id));
            using var reader = q.ExecuteReader();
            if (!reader.Read()) return (false, "演员不存在");
            name = reader.GetString(0);
            birthdate = reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        if (string.IsNullOrWhiteSpace(name)) return (false, "演员姓名为空，没法查");
        ourNames.Add(name.Trim());

        using (var q = new SqliteCommand("SELECT alias FROM actor_aliases WHERE actor_id = @id", conn))
        {
            q.Parameters.Add(new SqliteParameter("@id", id));
            using var reader = q.ExecuteReader();
            while (reader.Read()) ourNames.Add(reader.GetString(0).Trim());
        }

        var images = 0;
        using (var q = new SqliteCommand("SELECT COUNT(*) FROM actor_images WHERE actor_id = @id", conn))
        {
            q.Parameters.Add(new SqliteParameter("@id", id));
            images = Convert.ToInt32(q.ExecuteScalar());
        }

        var needAvatar = want.HasFlag(Want.Avatar) && images == 0;
        var needProfile = want.HasFlag(Want.Profile) &&
                          (string.IsNullOrWhiteSpace(birthdate) || !HasLatinAlias(ourNames));

        var skip = ActorGate.SkipReason(conn, id);
        if (skip is not null) return (false, $"{skip}，跳过");
        if (risk.Contains(id)) return (false, "该演员还在查重候选里，先合并再抓");
        if (!needAvatar && !needProfile)
        {
            if (want.HasFlag(Want.Avatar) && images > 0) return (false, $"已有 {images} 张照片，不动手");
            return (false, "该填的都填好了");
        }

        // 姓名和每一条曾用名都去索引里对一遍；命中两个不同档案就放弃，宁可不写
        try
        {
            var found = Resolve(ourNames, await LaoshiWiki.IndexAsync(ct));
            if (found is null) return (false, "老师图鉴上没有唯一对得上的档案");
            var hit = found.Value;

            var profile = await LaoshiWiki.ProfileAsync(hit.Num, ct)
                          ?? throw new InvalidOperationException("档案页没解析出内容，站点结构可能变了");

            var done = new List<string>();

            if (needAvatar)
            {
                var dl = await LaoshiWiki.DownloadAsync(profile.Portrait ?? LaoshiWiki.PortraitUrl(hit.Display), ct);
                if (dl is null) return (false, "头像下载失败（站上图床没有这个人的文件）");

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

            if (needProfile)
            {
                var iso = ScrapeChannelService.JapaneseDate(profile.BirthdateRaw ?? "");
                if (string.IsNullOrWhiteSpace(birthdate) && !string.IsNullOrWhiteSpace(iso))
                {
                    using var upd = new SqliteCommand(
                        @"UPDATE actors SET birthdate = @b
                           WHERE id = @id AND (birthdate IS NULL OR TRIM(birthdate) = '')", conn);
                    upd.Parameters.AddWithValue("@b", iso);
                    upd.Parameters.AddWithValue("@id", id);
                    if (upd.ExecuteNonQuery() > 0) done.Add($"生日 {iso}");
                }

                var added = AddAliases(conn, id, name, profile.Aliases);
                if (added > 0) done.Add($"别名 {added} 条");
            }

            if (done.Count == 0) return (false, $"档案 #{hit.Num}「{profile.Name}」里没有可补的字段");

            // 数据本身不落库（连对方档案地址都不存），但日志要能回看这个人是从哪份档案补的
            _logger.LogInformation(
                "已抓取 {What}：演员 {ActorId}「{OurName}」← 老师图鉴 档案 #{Num}「{ProfileName}」",
                string.Join('/', done), id, name, hit.Num, profile.Name);

            return (true, $"已抓到 {string.Join('、', done)}（来自档案 #{hit.Num}「{profile.Name}」）");
        }
        catch (Exception ex) when (ActorGate.IsNetworkFault(ex, ct))
        {
            return (false, $"站点请求失败（{ex.Message}）");
        }
    }

    /// <summary>索引里对上的那几个档案；0 个或互相冲突都算没有</summary>
    private static LaoshiWiki.Hit? Resolve(IEnumerable<string> ourNames, Dictionary<string, LaoshiWiki.Hit> index)
    {
        LaoshiWiki.Hit? one = null;
        foreach (var raw in ourNames)
        {
            if (!index.TryGetValue(LaoshiWiki.NoSpace(raw), out var found)) continue;
            if (one is { } prev && prev.Num != found.Num) return null;
            one = found;
        }

        return one;
    }

    /// <summary>
    /// 把这些写法补成别名，但跳过已经是**别人**姓名或别名的那些。
    /// 没走 <see cref="Aliases.Normalize"/>：那条规则按空白再拆一次，会把 "Yua Mikami" 拆成两条，
    /// 而罗马音本来就是姓和名分开写的。
    /// </summary>
    private static int AddAliases(SqliteConnection conn, string id, string name, IEnumerable<string> raw)
    {
        var want = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { LaoshiWiki.NoSpace(name) };

        using (var q = new SqliteCommand("SELECT alias FROM actor_aliases WHERE actor_id = @id", conn))
        {
            q.Parameters.Add(new SqliteParameter("@id", id));
            using var reader = q.ExecuteReader();
            while (reader.Read()) seen.Add(LaoshiWiki.NoSpace(reader.GetString(0)));
        }

        foreach (var piece in raw)
        {
            var value = piece.Trim();
            if (value.Length is < 1 or > Aliases.MaxLength || seen.Contains(LaoshiWiki.NoSpace(value))) continue;
            seen.Add(LaoshiWiki.NoSpace(value));
            want.Add(value);
            if (want.Count >= Aliases.MaxCount) break;
        }

        const string sql = @"
            INSERT INTO actor_aliases (actor_id, alias)
            SELECT @id, @alias
            WHERE NOT EXISTS (SELECT 1 FROM actor_aliases WHERE alias = @alias)
              AND NOT EXISTS (SELECT 1 FROM actors WHERE name = @alias)";
        var added = 0;
        foreach (var alias in want)
        {
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@alias", alias);
            if (cmd.ExecuteNonQuery() > 0) added++;
        }

        return added;
    }

    /// <summary>有没有拉丁写法（罗马音）的别名：整条只由字母和空格组成才算</summary>
    private static bool HasLatinAlias(IEnumerable<string> names) =>
        names.Skip(1).Any(n => Regex.IsMatch(n, @"^[A-Za-z][A-Za-z .'\-]*$"));
}

using Microsoft.Data.Sqlite;

namespace ckapi.Services;

/// <summary>
/// 演员档案源的统一形状：av-wiki 与老师图鉴都实现它，批量任务和单位抓取只认这个接口。
/// 加第三个源时只需要再实现一份 + 种一条通道行，ScrapeJob 不用改。
/// </summary>
public interface IActorSource
{
    /// <summary>接口参数用的稳定键，别拿中文名当标识</summary>
    string Key { get; }

    /// <summary>给用户看的源名</summary>
    string Label { get; }

    /// <summary>用它在 scrape_channels 里找到自己的通道行（限速/配额/熔断都在那条行上）</summary>
    string Host { get; }

    /// <summary>
    /// 还缺东西的演员，按影片数多的先来。异步是因为有的源要先取一份站内索引才能定候选集 ——
    /// 批量任务的间隔是按"每一位候选"付的，把对不上号的人留在候选里等于白等。
    /// </summary>
    Task<List<string>> CandidatesAsync(
        SqliteConnection conn, HashSet<string> risk, Want want, CancellationToken ct);

    /// <summary>补一位演员。Message 既是给用户看的说明，也是"为什么什么都没抓"的记录</summary>
    Task<(bool Ok, string Message)> ScrapeAsync(
        SqliteConnection conn, string id, string posterDir, HashSet<string> risk, Want want, CancellationToken ct);
}

/// <summary>
/// 两个档案源共用的门槛：谁有资格被问、谁必须先合并。
///
/// 两个站都只收日本女优，所以资格判断一样；而"认错人"的代价也一样，
/// 与其各写一遍不如放在一起，改口径时只有一个地方要改。
/// </summary>
public static class ActorGate
{
    /// <summary>
    /// 只认国家：非日本的一律不去问。
    ///
    /// 不关联"演过 av 分类没有"——有人改过分类、也有人只挂了几部素人片，
    /// 那类关联会把真的日本女优误伤掉，而误伤的代价是这个人永远没有头像。
    /// 省时间只是顺带；主要是不给错配留机会：一个中国网红的名字恰好撞上某个档案，
    /// 就会把别人的脸和资料安到她身上，而这种错法长期没人会去核对。
    ///
    /// '日本' 是 设置 → 数据源 里的规范取值；在那里改掉这个名字，这里要跟着改。
    /// </summary>
    public const string EligibleSql = "a.country = '日本'";

    /// <summary>这位演员值不值得去问档案源；返回 null 表示值得</summary>
    public static string? SkipReason(SqliteConnection conn, string id)
    {
        using var cmd = new SqliteCommand("SELECT country FROM actors WHERE id = @id", conn);
        cmd.Parameters.Add(new SqliteParameter("@id", id));
        var country = cmd.ExecuteScalar() as string;

        return country == "日本"
            ? null
            : $"国家是「{(string.IsNullOrEmpty(country) ? "未填" : country)}」，这两个档案源只收日本女优";
    }

    /// <summary>
    /// 出现在查重候选里的演员，抓取要跳过他们：同一张脸在两个名字下各存一份、
    /// 资料也是两份，之后合并时还得处理两套磁盘目录，比先合并再抓麻烦得多。
    /// </summary>
    public static HashSet<string> DuplicateRiskIds(SqliteConnection conn)
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
    /// 是不是"站点那边出问题了"。HttpClient 超时也抛 TaskCanceledException，
    /// 所以只能看调用方的 token 有没有被取消：没取消就是故障，取消了是要正常收尾。
    /// </summary>
    public static bool IsNetworkFault(Exception ex, CancellationToken ct)
        => ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested);
}

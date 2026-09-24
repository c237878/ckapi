using ckapi.Utils;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using System.Text.Json.Serialization;

namespace ckapi.Controllers;

/// <summary>
/// 规范选项（地区 / 分类）管理。
///
/// 清单存在 system_settings 的 countries / categories 两行里，
/// /api/video/meta、/api/actor/countries、/api/series/countries 三个读取端共用同一份，
/// 所以这里改一次，影片 / 系列 / 演员的下拉框同时变化。
/// 改名要级联更新已经用着旧值的记录，否则清单和数据显示两套值，
/// 正是当初"每个页面地区选项不一样"的根源。
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class TaxonomyController : ControllerBase
{
    private readonly SQLiteHelper _db;
    private readonly ILogger<TaxonomyController> _logger;

    public TaxonomyController(SQLiteHelper db, ILogger<TaxonomyController> logger)
    {
        _db = db;
        _logger = logger;
    }

    private const int MaxValueLength = 20;
    private const int MaxItems = 200;

    /// <summary>清单 + 每个值的引用数 + 清单外仍在使用的值</summary>
    [HttpGet]
    public IActionResult Get()
    {
        try
        {
            using var conn = _db.GetConnection();
            conn.Open();

            var (countries, countryOrphans) = Build(conn, Options.Countries);
            var (categories, categoryOrphans) = Build(conn, Options.Categories);

            return Ok(new
            {
                success = true,
                data = new
                {
                    countries,
                    countryOrphans,
                    categories,
                    categoryOrphans
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取数据源失败");
            return Ok(new { success = false, message = Api.InternalErrorMessage });
        }
    }

    /// <summary>
    /// 一次事务保存：先按 renames 级联改记录，再把 items 整体写回清单。
    /// 新增、删除、上下移都只是 items 变化；重命名额外带一条 rename。
    /// </summary>
    [HttpPost]
    public IActionResult Save([FromBody] TaxonomySaveRequest? req)
    {
        var kind = req?.Kind?.Trim();
        if (kind != Options.Countries && kind != Options.Categories)
            return Ok(new { success = false, message = "未知的数据源类型" });

        var items = Normalize(req!.Items);
        if (items == null)
            return Ok(new { success = false, message = $"选项不能为空、不能重复，单个不超过 {MaxValueLength} 字" });
        if (items.Count > MaxItems)
            return Ok(new { success = false, message = $"最多 {MaxItems} 个选项" });

        try
        {
            using var conn = _db.GetConnection();
            conn.Open();

            var current = Build(conn, kind);
            var known = current.Items.Select(i => i.Value)
                .Concat(current.Orphans.Select(o => o.Value))
                .ToHashSet(StringComparer.Ordinal);

            var renames = (req.Renames ?? new List<TaxonomyRename>())
                .Where(r => !string.IsNullOrWhiteSpace(r?.From) && !string.IsNullOrWhiteSpace(r?.To))
                .Select(r => new TaxonomyRename { From = r.From.Trim(), To = r.To.Trim() })
                .ToList();

            // A→B 与 B→C 链式改名按顺序执行会得到 A→C，直接拒绝：前端一次只改一个值
            foreach (var r in renames)
            {
                if (r.From == r.To) continue;
                if (!known.Contains(r.From))
                    return Ok(new { success = false, message = $"「{r.From}」不在当前数据源里" });
                if (!items.Contains(r.To))
                    return Ok(new { success = false, message = $"「{r.From}」要改成的「{r.To}」不在待保存的清单里" });
                if (renames.Any(o => o != r && o.From == r.To))
                    return Ok(new { success = false, message = "不支持链式改名，请分两次保存" });
            }

            // 事务开启后这条连接上的命令必须带 tx，所以读一律放在事务之前
            var homeBefore = kind == Options.Categories
                ? Options.CommaList(conn, "homePageCategories")
                : new List<string>();

            var affected = new Dictionary<string, int>();
            using var tx = conn.BeginTransaction();
            try
            {
                foreach (var r in renames.Where(r => r.From != r.To))
                {
                    affected[$"videos.{Column(kind)}"] = Cascade(conn, tx, "videos", Column(kind), r);
                    if (kind == Options.Countries)
                    {
                        affected["actors.country"] = Cascade(conn, tx, "actors", "country", r);
                        affected["video_series.country"] = Cascade(conn, tx, "video_series", "country", r);
                    }
                }

                UpsertSetting(conn, tx, kind, string.Join(",", items));

                // 首页展示分类是另一份逗号清单：改了名要跟着改，删了要跟着删，
                // 否则首页会留一个永远为空的板块
                if (kind == Options.Categories)
                    SyncHomePageCategories(conn, tx, items, renames, homeBefore);

                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }

            if (renames.Any(r => r.From != r.To))
                _db.BackupDatabase("数据源改名", tag: "taxonomy");

            var changed = renames.Where(r => r.From != r.To)
                .Select(r => $"{r.From}→{r.To}")
                .ToList();

            _logger.LogInformation("保存数据源 {Kind}：{Count} 项，改名 {Changed}，影响 {Affected}",
                kind, items.Count, changed.Count == 0 ? "无" : string.Join("、", changed),
                affected.Count == 0 ? "0 行" : string.Join("、", affected.Select(a => $"{a.Key}={a.Value}")));

            return Ok(new { success = true, message = "已保存", data = new { affected } });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存数据源失败: {Kind}", kind);
            return Ok(new { success = false, message = Api.InternalErrorMessage });
        }
    }

    // ---------------------------------------------------------------- 内部

    /// <summary>
    /// 逗号清单本身 + 三张表里的实际用量。清单里没有但记录还在用的值单独返回，
    /// 让管理界面能看见"漏网"的取值并一键收进清单。
    /// </summary>
    private static (List<OptionRow> Items, List<OptionRow> Orphans) Build(SqliteConnection conn, string kind)
    {
        var list = Options.CommaList(conn, kind);
        var map = new Dictionary<string, OptionRow>(StringComparer.Ordinal);
        foreach (var v in list) map[v] = new OptionRow { Value = v };

        CountInto(conn, map, "videos", Column(kind), (row, c) => row.Videos = c);
        if (kind == Options.Countries)
        {
            CountInto(conn, map, "actors", "country", (row, c) => row.Actors = c);
            CountInto(conn, map, "video_series", "country", (row, c) => row.Series = c);
        }

        var known = list.ToHashSet(StringComparer.Ordinal);
        return (
            list.Select(v => map[v]).ToList(),
            map.Keys.Where(k => !known.Contains(k)).OrderBy(k => k, StringComparer.Ordinal)
                .Select(k => map[k]).ToList()
        );
    }

    /// <summary>表名/列名只由上面的 kind 分支决定，不接受调用方传入，因此拼接是安全的</summary>
    private static void CountInto(
        SqliteConnection conn, Dictionary<string, OptionRow> map, string table, string column, Action<OptionRow, int> assign)
    {
        using var cmd = new SqliteCommand(
            $"SELECT {column} AS v, COUNT(*) AS c FROM {table} " +
            $"WHERE {column} IS NOT NULL AND TRIM({column}) <> '' GROUP BY {column}", conn);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var value = reader.GetString(0).Trim();
            var count = reader.GetInt32(1);
            if (!map.TryGetValue(value, out var row))
            {
                row = new OptionRow { Value = value };
                map[value] = row;
            }
            assign(row, count);
        }
    }

    private static int Cascade(SqliteConnection conn, SqliteTransaction tx, string table, string column, TaxonomyRename r)
    {
        using var cmd = new SqliteCommand(
            $"UPDATE {table} SET {column} = @to WHERE {column} = @from", conn, tx);
        cmd.Parameters.Add(new SqliteParameter("@to", r.To));
        cmd.Parameters.Add(new SqliteParameter("@from", r.From));
        return cmd.ExecuteNonQuery();
    }

    private static void SyncHomePageCategories(
        SqliteConnection conn, SqliteTransaction tx, List<string> items, List<TaxonomyRename> renames, List<string> home)
    {
        if (home.Count == 0) return;

        var kept = home
            .Select(v => renames.FirstOrDefault(r => r.From == v)?.To ?? v)
            .Where(items.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        UpsertSetting(conn, tx, "homePageCategories", string.Join(",", kept));
    }

    private static void UpsertSetting(SqliteConnection conn, SqliteTransaction tx, string name, string content)
    {
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        using (var update = new SqliteCommand(
                   "UPDATE system_settings SET content = @c, utime = @u WHERE name = @n", conn, tx))
        {
            update.Parameters.Add(new SqliteParameter("@c", content));
            update.Parameters.Add(new SqliteParameter("@u", now));
            update.Parameters.Add(new SqliteParameter("@n", name));
            if (update.ExecuteNonQuery() > 0) return;
        }

        using var insert = new SqliteCommand(
            "INSERT INTO system_settings (id, name, content, ctime, utime) VALUES (@id, @n, @c, @u, @u)", conn, tx);
        insert.Parameters.Add(new SqliteParameter("@id", Guid.NewGuid().ToString("N").ToUpper()));
        insert.Parameters.Add(new SqliteParameter("@n", name));
        insert.Parameters.Add(new SqliteParameter("@c", content));
        insert.Parameters.Add(new SqliteParameter("@u", now));
        insert.ExecuteNonQuery();
    }

    private static string Column(string kind) => kind == Options.Countries ? "country" : "category";

    /// <summary>去空、去重、保序；不合法返回 null 交给调用方报错</summary>
    private static List<string>? Normalize(List<string>? raw)
    {
        if (raw == null) return new List<string>();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();
        foreach (var value in raw)
        {
            var v = value?.Trim() ?? "";
            if (v.Length == 0 || v.Length > MaxValueLength || v.Contains(',') || !seen.Add(v)) return null;
            list.Add(v);
        }
        return list;
    }
}

public class OptionRow
{
    public string Value { get; set; } = "";
    public int Videos { get; set; }
    public int Actors { get; set; }
    public int Series { get; set; }
    public int Total => Videos + Actors + Series;
}

public class TaxonomySaveRequest
{
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("items")] public List<string> Items { get; set; } = new();
    [JsonPropertyName("renames")] public List<TaxonomyRename> Renames { get; set; } = new();
}

public class TaxonomyRename
{
    [JsonPropertyName("from")] public string From { get; set; } = "";
    [JsonPropertyName("to")] public string To { get; set; } = "";
}

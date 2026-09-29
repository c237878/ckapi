using ckapi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using System.Text.Json.Serialization;

namespace ckapi.Controllers;

/// <summary>
/// 版本类型词表（设置里的「版本类型」分区）。
///
/// 类型说的是"这一版是谁做的 / 是什么性质"（湿姐、水果派、剪辑…），
/// 与片商不是一回事：片商是那部片的制片方，版本只属于这一份文件。
/// 库里那四家中文解说频道当初被当片商录，v11 搬到这里来了。
///
/// suffix 只用于新建版本时建议文件名尾巴（C/E/…），番号级联改名不参与，
/// 否则同类型的两版会算出同一个目标名（见 VideoFiles.CascadeRename）。
/// </summary>
[ApiController]
[Route("api/versiontype")]
public class VersionTypeController : ControllerBase
{
    private const int MaxNameLength = 20;
    private const int MaxSuffixLength = 8;

    private readonly ILogger<VersionTypeController> _logger;
    private readonly Utils.SQLiteHelper _db;

    public VersionTypeController(ILogger<VersionTypeController> logger, Utils.SQLiteHelper db)
    {
        _logger = logger;
        _db = db;
    }

    /// <summary>清单 + 每类下的版本行数（用在界面上判断能不能删）</summary>
    [HttpGet]
    public IActionResult List()
    {
        using var conn = _db.GetConnection();
        conn.Open();
        return Ok(new { success = true, data = VideoFiles.Types(conn) });
    }

    [HttpPost]
    public IActionResult Add([FromBody] TypeRequest req)
    {
        var (name, suffix, error) = Normalize(req);
        if (error is not null) return Ok(new { success = false, message = error });

        using var conn = _db.GetConnection();
        conn.Open();
        if (Scalar(conn, "SELECT id FROM version_types WHERE name = @n", ("@n", name!)) is not null)
            return Ok(new { success = false, message = $"已有「{name}」这个版本类型" });
        var suffixClash = SuffixTakenBy(conn, suffix!, null);
        if (suffixClash is not null)
            return Ok(new { success = false, message = $"尾巴「{suffix}」已经是「{suffixClash}」那一类的了，各类型要各用一个" });

        var sort = Convert.ToInt32(Scalar(conn, "SELECT IFNULL(MAX(sort), 0) + 1 FROM version_types") ?? 1);
        var id = VideoFiles.NewId();
        NonQuery(conn, "INSERT INTO version_types (id, name, suffix, sort) VALUES (@id, @n, @s, @sort)",
            ("@id", id), ("@n", name!), ("@s", suffix!), ("@sort", sort));

        _logger.LogInformation("新增版本类型 {Name}（文件名后缀 {Suffix}）", name, suffix);
        return Ok(new { success = true, data = new { id }, message = "已添加" });
    }

    [HttpPut("{id}")]
    public IActionResult Update(string id, [FromBody] TypeRequest req)
    {
        var (name, suffix, error) = Normalize(req);
        if (error is not null) return Ok(new { success = false, message = error });

        using var conn = _db.GetConnection();
        conn.Open();
        if (Scalar(conn, "SELECT id FROM version_types WHERE id = @i", ("@i", id)) is null)
            return NotFound(new { success = false, message = "版本类型不存在" });
        if (Scalar(conn, "SELECT id FROM version_types WHERE name = @n AND id <> @i",
                ("@n", name!), ("@i", id)) is not null)
            return Ok(new { success = false, message = $"已有「{name}」这个版本类型" });
        var suffixClash = SuffixTakenBy(conn, suffix!, id);
        if (suffixClash is not null)
            return Ok(new { success = false, message = $"尾巴「{suffix}」已经是「{suffixClash}」那一类的了，各类型要各用一个" });

        NonQuery(conn, "UPDATE version_types SET name = @n, suffix = @s WHERE id = @i",
            ("@n", name!), ("@s", suffix!), ("@i", id));
        return Ok(new { success = true, message = "已保存" });
    }

    /// <summary>
    /// 删除。已经有版本用着它就不许删——那些行会失去类型名，界面上变成一片空白；
    /// 真不想要这一类，先把用了它的版本改成别的类型。
    /// </summary>
    [HttpDelete("{id}")]
    public IActionResult Delete(string id)
    {
        using var conn = _db.GetConnection();
        conn.Open();
        var used = Convert.ToInt32(
            Scalar(conn, "SELECT COUNT(*) FROM video_files WHERE type_id = @i", ("@i", id)) ?? 0);
        if (used > 0)
            return Ok(new { success = false, message = $"还有 {used} 个版本用着这一类，先把它们改成别的类型" });

        var rows = NonQuery(conn, "DELETE FROM version_types WHERE id = @i", ("@i", id));
        return rows == 0
            ? NotFound(new { success = false, message = "版本类型不存在" })
            : Ok(new { success = true, message = "已删除" });
    }

    /// <summary>上下移动（详情页下拉与新增版本对话框按这个顺序排）</summary>
    [HttpPost("{id}/move")]
    public IActionResult Move(string id, [FromQuery] string dir = "up")
    {
        using var conn = _db.GetConnection();
        conn.Open();

        var mySort = Scalar(conn, "SELECT sort FROM version_types WHERE id = @i", ("@i", id));
        if (mySort is null) return NotFound(new { success = false, message = "版本类型不存在" });

        // 邻居取"这个方向上 sort 差得最小的一条"：中间删空过几项也还是相邻的两项在换。
        // 比较符只能由 dir 这一个内部取值决定（SQL 里运算符不能做成参数），不接受调用方传入原文
        var cmp = dir == "down" ? ">" : "<";
        string? neighborId = null;
        int neighborSort = 0;
        using (var cmd = new SqliteCommand($@"
            SELECT id, sort FROM version_types
            WHERE id <> @id AND sort {cmp} @sort
            ORDER BY ABS(sort - @sort) LIMIT 1", conn))
        {
            cmd.Parameters.Add(new SqliteParameter("@id", id));
            cmd.Parameters.Add(new SqliteParameter("@sort", mySort));
            using var r = cmd.ExecuteReader();
            if (r.Read()) { neighborId = r.GetString(0); neighborSort = r.GetInt32(1); }
        }
        if (neighborId is null) return Ok(new { success = true, message = "已经到头了" });

        NonQuery(conn, "UPDATE version_types SET sort = @s WHERE id = @i", ("@s", neighborSort), ("@i", id));
        NonQuery(conn, "UPDATE version_types SET sort = @s WHERE id = @i", ("@s", Convert.ToInt32(mySort)), ("@i", neighborId));
        return Ok(new { success = true, message = "已移动" });
    }

    // ---------------------------------------------------------------- 小工具

    /// <summary>
    /// 这个尾巴是不是已经被别的类型占了。
    ///
    /// 为什么要求各类型互不相同：一部片同一个类型只能有一条，所以第二版必然是另一个类型，
    /// 而新建时的行级标识是按「影片番号 + 该类型后缀」自动拼的——四家频道都填 C 的话，
    /// 第二版一保存就撞上第一版的标识，自动值形同虚设（2026-09-29 实测就撞在这）。
    /// 允许留空：不填尾巴的类型由界面要求手填标识，不参与自动拼接。
    /// </summary>
    private static string? SuffixTakenBy(SqliteConnection conn, string suffix, string? exceptId)
    {
        if (string.IsNullOrEmpty(suffix)) return null;
        using var cmd = new SqliteCommand(@"
            SELECT name FROM version_types
            WHERE suffix = @s AND (@self = '' OR id <> @self)
            LIMIT 1", conn);
        cmd.Parameters.Add(new SqliteParameter("@s", suffix));
        cmd.Parameters.Add(new SqliteParameter("@self", exceptId ?? ""));
        return cmd.ExecuteScalar()?.ToString();
    }

    /// <summary>去空白、限长度；不合法返回消息。suffix 允许为空（类型不一定要带尾巴）。</summary>
    private static (string? Name, string? Suffix, string? Error) Normalize(TypeRequest req)
    {
        var name = req.Name?.Trim() ?? "";
        var suffix = req.Suffix?.Trim() ?? "";
        if (name.Length == 0) return (null, null, "名称不能为空");
        if (name.Length > MaxNameLength) return (null, null, $"名称不超过 {MaxNameLength} 字");
        if (suffix.Length > MaxSuffixLength) return (null, null, $"文件名后缀不超过 {MaxSuffixLength} 字");
        return (name, suffix, null);
    }

    private static object? Scalar(SqliteConnection conn, string sql, params (string Name, object Value)[] ps)
    {
        using var cmd = new SqliteCommand(sql, conn);
        foreach (var (name, value) in ps) cmd.Parameters.Add(new SqliteParameter(name, value));
        return cmd.ExecuteScalar();
    }

    private static int NonQuery(SqliteConnection conn, string sql, params (string Name, object Value)[] ps)
    {
        using var cmd = new SqliteCommand(sql, conn);
        foreach (var (name, value) in ps) cmd.Parameters.Add(new SqliteParameter(name, value));
        return cmd.ExecuteNonQuery();
    }

    public sealed class TypeRequest
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("suffix")] public string? Suffix { get; set; }
    }
}

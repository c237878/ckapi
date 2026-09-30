using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace ckapi.Controllers;

/// <summary>
/// 破坏性操作的管理口令（设置里的「安全」分区）。
///
/// 校验逻辑在 <see cref="ckapi.Utils.AdminTokenFilter"/>，这里只负责口令本身：
/// 查状态、生成、替换、清除。
///
/// 这几个接口自己**不要求口令**，但要求**同源**——原因是要口令就变成鸡生蛋：
/// 第一次没口令可用。而同源检查正好补上唯一的绕过口子：陌生网页虽然能对你的服务发请求，
/// 但它发不出同源标记，所以既不能"先给自己生成一个口令再拿去删除"，也不能把口令清掉。
/// 非浏览器客户端（curl）不带 Origin/Referer，视为本机手操放行。
/// </summary>
[ApiController]
[Route("api/security")]
public class SecurityController : ControllerBase
{
    private readonly ILogger<SecurityController> _logger;
    private readonly Utils.SQLiteHelper _db;

    public SecurityController(ILogger<SecurityController> logger, Utils.SQLiteHelper db)
    {
        _logger = logger;
        _db = db;
    }

    /// <summary>口令状态。只回掩码——完整值只在生成那一次出现，之后不再从接口读出去。</summary>
    [HttpGet("status")]
    public IActionResult Status()
    {
        var token = Read();
        return Ok(new
        {
            success = true,
            data = new
            {
                enabled = !string.IsNullOrEmpty(token),
                masked = Mask(token),
                length = token?.Length ?? 0
            }
        });
    }

    /// <summary>
    /// 生成或替换口令。传 value 就是"用我给的这个"（换设备时把老的粘进来），
    /// 不传就随机生成一个。已经有口令时必须带对旧的，否则等于谁都能顶掉它。
    /// </summary>
    [HttpPost("token")]
    public IActionResult Set([FromBody] TokenRequest? request)
    {
        if (!SameOrigin()) return RejectCrossSite();

        var current = Read();
        if (!string.IsNullOrEmpty(current) && !Matches(current, request?.Current))
            return Ok(new { success = false, message = "需要先带上当前的管理口令" });

        var given = request?.Value?.Trim() ?? "";
        if (given.Length > 0 && given.Length < 8)
            return Ok(new { success = false, message = "口令至少 8 个字符" });

        var token = given.Length > 0 ? given : Generate();
        Write(token);
        // 完整值只在这里出现一次；界面把它存进本机 localStorage 后就不再回来取
        return Ok(new { success = true, data = new { token }, message = given.Length > 0 ? "口令已更新" : "口令已生成" });
    }

    /// <summary>关掉门禁。同样要求带对当前口令——不然绕过就只是"先清空再删除"。</summary>
    [HttpDelete("token")]
    public IActionResult Clear([FromQuery] string? current = null)
    {
        if (!SameOrigin()) return RejectCrossSite();

        var token = Read();
        if (string.IsNullOrEmpty(token)) return Ok(new { success = true, message = "本来就没启用" });

        if (!Matches(token, current))
            return Ok(new { success = false, message = "需要先带上当前的管理口令" });

        Write("");
        _logger.LogWarning("管理口令已清除，破坏性接口重新回到无门禁状态");
        return Ok(new { success = true, message = "已关闭口令校验" });
    }

    /// <summary>
    /// 请求是不是从本站页面发出来的。
    /// 浏览器对非同源的 POST/DELETE 一定会带 Origin，所以拿它跟"我这边的主机名"比；
    /// **只比主机名，不比端口** —— 前端常走一层反代（vite 的 changeOrigin 会把 Host 改成后端端口），
    /// 连端口一起比的话正常操作会被误判成跨站（实测踩过）。真正的威胁是 evil.example 这种不同主机，
    /// 它过不了主机名这一关。没有 Origin 也没有 Referer 的就是 curl / 服务端调用，视为本机手操放行。
    /// </summary>
    private bool SameOrigin()
    {
        var origin = Request.Headers["Origin"].ToString();
        if (string.IsNullOrWhiteSpace(origin))
        {
            var referer = Request.Headers["Referer"].ToString();
            if (string.IsNullOrWhiteSpace(referer)) return true;
            if (!Uri.TryCreate(referer, UriKind.Absolute, out var refUri)) return false;
            origin = refUri.Host;
        }
        else if (!Uri.TryCreate(origin, UriKind.Absolute, out var oUri))
        {
            return false;
        }
        else
        {
            origin = oUri.Host;
        }

        // 反代转过来时真实主机在 X-Forwarded-Host 里（形如 host:port），取主机名部分
        var forwarded = Request.Headers["X-Forwarded-Host"].ToString();
        var hereHost = forwarded.Contains(':') ? forwarded.Split(':')[0] : forwarded;
        if (string.IsNullOrWhiteSpace(hereHost)) hereHost = Request.Host.Host;

        return string.Equals(origin, hereHost, StringComparison.OrdinalIgnoreCase);
    }

    private IActionResult RejectCrossSite()
    {
        _logger.LogWarning("拒绝跨站修改管理口令：Origin={Origin}", Request.Headers["Origin"].ToString());
        return StatusCode(StatusCodes.Status403Forbidden,
            new { success = false, message = "只能从本站页面修改管理口令" });
    }

    private string? Read()
    {
        using var conn = _db.GetConnection();
        conn.Open();
        using var cmd = new SqliteCommand(
            "SELECT content FROM system_settings WHERE name = @n", conn);
        cmd.Parameters.Add(new SqliteParameter("@n", ckapi.Utils.AdminTokenFilter.SettingName));
        return cmd.ExecuteScalar()?.ToString();
    }

    /// <summary>
    /// 覆盖式写入。system_settings.name 上没有唯一约束，ON CONFLICT 会直接报错，
    /// 所以按现有惯例先删再插（同 Mcp.Setting、TaxonomyController 的写法）。
    /// </summary>
    private void Write(string token)
    {
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        using var conn = _db.GetConnection();
        conn.Open();
        using var tx = conn.BeginTransaction();
        using (var del = new SqliteCommand("DELETE FROM system_settings WHERE name = @n", conn, tx))
        {
            del.Parameters.Add(new SqliteParameter("@n", ckapi.Utils.AdminTokenFilter.SettingName));
            del.ExecuteNonQuery();
        }
        using var ins = new SqliteCommand(
            "INSERT INTO system_settings (id, name, content, ctime, utime) VALUES (@id, @n, @c, @t, @t)", conn, tx);
        ins.Parameters.Add(new SqliteParameter("@id", Guid.NewGuid().ToString("N")));
        ins.Parameters.Add(new SqliteParameter("@n", ckapi.Utils.AdminTokenFilter.SettingName));
        ins.Parameters.Add(new SqliteParameter("@c", token));
        ins.Parameters.Add(new SqliteParameter("@t", now));
        ins.ExecuteNonQuery();
        tx.Commit();
    }

    private static bool Matches(string expected, string? given)
        => !string.IsNullOrWhiteSpace(given) && given.Trim() == expected;

    private static string Mask(string? token)
    {
        if (string.IsNullOrEmpty(token)) return "";
        if (token.Length <= 6) return new string('•', token.Length);
        return $"{token[..3]}{new string('•', token.Length - 6)}{token[^3..]}";
    }

    /// <summary>
    /// 20 位随机口令。去掉了容易看错的字符（0/O、1/l/I），因为要人手抄到另一台设备上；
    /// 用 RandomNumberGenerator，不是 Random——可预测的口令等于没锁。
    /// </summary>
    private static string Generate()
    {
        const string alphabet = "23456789abcdefghjkmnpqrstuvwxyzABCDEFGHJKMNPQRSTUVWXYZ";
        var bytes = new byte[20];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return new string(bytes.Select(b => alphabet[b % alphabet.Length]).ToArray());
    }
}

public class TokenRequest
{
    /// <summary>想要的口令内容；留空则随机生成</summary>
    [System.Text.Json.Serialization.JsonPropertyName("value")]
    public string? Value { get; set; }

    /// <summary>当前口令（已经启用时必须带）</summary>
    [System.Text.Json.Serialization.JsonPropertyName("current")]
    public string? Current { get; set; }
}

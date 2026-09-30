using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ckapi.Utils;

/// <summary>
/// 破坏性接口的口令门禁。
///
/// 为什么不是全站登录：这个项目是本机自用，浏览/搜索/播放占日常操作的绝大多数，
/// 加一层登录只会让每次换设备、清缓存都要填一遍，而真正需要防的是
/// "局域网里另一个设备、或浏览器里某个陌生网页的一个请求，把片和图删掉"——
/// 那些动作全都走删除 / 改名 / 批量写这几类接口，只锁它们就够了。
///
/// 两个刻意的选择：
/// - **没设置口令时接口照旧放行**。否则等于先把他锁在自己的库里；开关由他自己按。
/// - **只认自定义请求头 X-Admin-Token**。浏览器跨站发不出自定义头（要先过预检），
///   而攻击者也猜不到这个值——所以它同时挡住了 drive-by 删除和口令被猜。
///   查询参数 ?token= 也收，是为了 curl 能手操；代价是可能进访问日志，界面上不推荐。
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public class AdminTokenAttribute : ServiceFilterAttribute
{
    public AdminTokenAttribute() : base(typeof(AdminTokenFilter)) { }
}

public class AdminTokenFilter : IAuthorizationFilter
{
    /// <summary>口令存在 system_settings 里（这张表本来就是"界面可改的运行配置"的落点）</summary>
    public const string SettingName = "destructive_token";

    private readonly SQLiteHelper _db;
    private readonly ILogger<AdminTokenFilter> _logger;

    public AdminTokenFilter(SQLiteHelper db, ILogger<AdminTokenFilter> logger)
    {
        _db = db;
        _logger = logger;
    }

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var expected = ReadToken();
        if (string.IsNullOrEmpty(expected)) return;   // 没启用：不拦

        var request = context.HttpContext.Request;
        var given = request.Headers["X-Admin-Token"].ToString();
        if (string.IsNullOrWhiteSpace(given)) given = request.Query["token"].ToString();

        // 定长比较，避免提前返回泄露长度信息——这里是本机服务，但也没必要留个侧信道
        if (!string.IsNullOrEmpty(given) && FixedTimeEquals(given, expected)) return;

        _logger.LogWarning("破坏性操作被拒：{Method} {Path}（{Reason}）",
            request.Method, request.Path, string.IsNullOrWhiteSpace(given) ? "没带管理口令" : "口令不匹配");

        context.Result = new ObjectResult(new
        {
            success = false,
            needToken = true,
            message = "这个操作需要管理口令：在设置「安全」里生成，或粘贴已有的"
        })
        { StatusCode = StatusCodes.Status401Unauthorized };
    }

    public string ReadToken()
    {
        using var conn = _db.GetConnection();
        conn.Open();
        using var cmd = new Microsoft.Data.Sqlite.SqliteCommand(
            "SELECT content FROM system_settings WHERE name = @n", conn);
        cmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@n", SettingName));
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        var diff = 0;
        for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }
}

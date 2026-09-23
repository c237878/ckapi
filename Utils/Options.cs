using Microsoft.Data.Sqlite;

namespace ckapi.Utils;

/// <summary>
/// 规范选项列表（地区、分类）的唯一读取入口。
///
/// 此前 /api/actor/countries、/api/series/countries、/api/video/meta 各自
/// SELECT DISTINCT 自己的表，于是"系列"下拉比"演员"少一项（因为库里还没有韩国系列），
/// 而且没记录过的取值根本选不出来。改成统一读 system_settings。
/// </summary>
public static class Options
{
    public const string Countries = "countries";
    public const string Categories = "categories";

    /// <summary>
    /// 读逗号分隔的列表。空值就返回空列表，**不回退到表内 DISTINCT** ——
    /// 否则管理员清空列表后，不同页面又会各自冒出不同的候选，回到老问题。
    /// </summary>
    public static List<string> CommaList(SqliteConnection conn, string name)
    {
        using var cmd = new SqliteCommand("SELECT content FROM system_settings WHERE name = @n", conn);
        cmd.Parameters.AddWithValue("@n", name);
        var raw = cmd.ExecuteScalar() as string;

        if (string.IsNullOrWhiteSpace(raw)) return new List<string>();

        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct()
            .ToList();
    }
}

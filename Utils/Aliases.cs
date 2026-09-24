namespace ckapi.Utils;

/// <summary>
/// 演员曾用名（actor_aliases）的统一清洗规则。
/// 迁移与增删改共用一份，避免"迁移能过、界面保存却被拒"这类两边规则漂移。
/// </summary>
public static class Aliases
{
    public const int MaxLength = 60;
    public const int MaxCount = 20;

    /// <summary>
    /// 去空白、去重、丢掉与本人姓名相同的项（没有信息量）、丢掉超长项（多半是把整句简介塞了进来），
    /// 最多保留 <see cref="MaxCount"/> 个。输入可以是整串按空格拆出的片段，也可以是前端传来的数组。
    /// </summary>
    public static List<string> Normalize(IEnumerable<string>? raw, string? selfName)
    {
        var self = selfName?.Trim();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();

        foreach (var piece in raw ?? Array.Empty<string>())
        {
            // 一条里再出现空格，说明是旧数据没拆干净，按空白再拆一次
            foreach (var token in piece.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                var value = token.Trim();
                if (value.Length == 0 || value.Length > MaxLength) continue;
                if (string.Equals(value, self, StringComparison.Ordinal)) continue;
                if (!seen.Add(value)) continue;
                if (list.Count >= MaxCount) return list;
                list.Add(value);
            }
        }

        return list;
    }
}

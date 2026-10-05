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
    /// 最多保留 <see cref="MaxCount"/> 个。
    ///
    /// **默认不按空格再拆一次。**一条别名就是一个名字，"Emiri Momota" 是一整个艺名，
    /// 拆成 "Emiri" + "Momota" 两条既不是名字、也让人认不出本人（2026-10-05 他实测报上来的）。
    /// 片商别名同理，"Soft On Demand" 不该变成三段。
    ///
    /// 只有一处需要拆开：从旧的那一个字段迁到 actor_aliases 时（DataService 里那条），
    /// 历史数据确实是"多值挤在一串里、用空格分隔"，那时才传 <paramref name="splitOnWhitespace"/>。
    /// </summary>
    public static List<string> Normalize(
        IEnumerable<string>? raw, string? selfName, bool splitOnWhitespace = false)
    {
        var self = selfName?.Trim();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();

        foreach (var piece in raw ?? Array.Empty<string>())
        {
            foreach (var token in splitOnWhitespace
                         ? piece.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                         : new[] { piece })
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

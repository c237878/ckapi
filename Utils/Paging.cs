namespace ckapi.Utils;

/// <summary>
/// 分页参数归一。此前 pageIndex=0 会算出负 OFFSET，pageSize 无上界。
/// </summary>
public static class Paging
{
    /// <summary>单次请求允许的最大条数</summary>
    public const int MaxPageSize = 500;

    public static int ClampSize(int pageSize, int fallback = 20)
    {
        if (pageSize <= 0) return fallback;
        return Math.Min(pageSize, MaxPageSize);
    }

    /// <summary>返回从 1 开始的页码</summary>
    public static int ClampPage(int page) => page < 1 ? 1 : page;

    public static int Offset(int page, int pageSize) => (ClampPage(page) - 1) * ClampSize(pageSize);

    /// <summary>返回给前端的 top-N 条数（如今日推荐、相关推荐）</summary>
    public static int ClampCount(int count, int fallback = 12, int max = 100)
    {
        if (count <= 0) return fallback;
        return Math.Min(count, max);
    }
}

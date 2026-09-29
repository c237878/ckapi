namespace ckapi.Utils;

/// <summary>
/// 片源两维的取值口径，前后端各存一份枚举容易漂，后端这边以这里为准：
/// 标记接口按它校验，列表筛选按它校验（不在表里的值一律当"没筛"，不让用户拼进来的字符串进 SQL）。
///
/// 文案与配色在 ckweb/src/scripts/constants.js，两边取值必须一一对应。
/// </summary>
public static class SourceStates
{
    public const string Unknown = "unknown";

    /// <summary>字幕：unknown 未标 · none 发行版本就没字幕 · missing 这片有字幕但这份没带上 · has 有字幕</summary>
    public static readonly string[] Subtitle = { Unknown, "none", "missing", "has" };

    /// <summary>广告水印：unknown 未标 · none 画面干净 · light 角落台标 · heavy 满屏广告</summary>
    public static readonly string[] Watermark = { Unknown, "none", "light", "heavy" };

    public static bool IsSubtitle(string? value) => value is not null && Array.IndexOf(Subtitle, value) >= 0;

    public static bool IsWatermark(string? value) => value is not null && Array.IndexOf(Watermark, value) >= 0;

    /// <summary>
    /// "没看过"的 SQL 口径：**默认版本那一行的两维都没给结论**（都还是 unknown）。
    /// 今日推荐与首页排序用它挑片，不再单开一列存"看过"——
    /// 扫描只写分辨率、不写字幕，所以"有结论"这件事只可能来自人工标记，反推是可靠的。
    /// 反过来也提醒一句：哪天让扫描或别的自动途径去填这两个状态，这个口径就当场失效了
    /// （全站都会变成"有结论"，今日推荐没有片可推）。
    /// 表别名固定为 v（影片）与 df（默认版本行，见 VideoCardQuery.FileJoin，查询必须挂上它）。
    /// IFNULL 是兜底：万一某部片没了默认行，按"没给过结论"算，宁可让它多出现在推荐里，
    /// 也别让一个 NULL 把它从全站榜单上悄悄抹掉。
    /// </summary>
    public const string Unrated =
        "(IFNULL(df.subtitle_state, 'unknown') = 'unknown' AND IFNULL(df.watermark_state, 'unknown') = 'unknown')";

    /// <summary>
    /// 分辨率档位 → SQL 条件。全是写死的常量，没有一处拼用户输入。
    ///
    /// 按**短边**（min(宽,高)）切档，不按高度：横屏的短边就是高度，两种口径对横屏完全等价；
    /// 差别只在竖屏那 188 条——720×1280 按高度会报成"1280p"、1440×2560 更是直接掉进 4K 档，
    /// 而手机视频通行的叫法就是按短边叫 720p / 1440p。
    /// 两个值都在库里存着，详情页原样显示"720×1280"，这里只管归档。
    /// 未扫描的单开一档，好让人看出还有哪些没量过。
    /// 分辨率是每一份文件自己的事（v11 起在 video_files），筛的是默认那一版。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Resolutions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["unscanned"] = "df.res_h IS NULL",
            ["sd"] = "MIN(df.res_w, df.res_h) < 480",
            ["480"] = "MIN(df.res_w, df.res_h) >= 480 AND MIN(df.res_w, df.res_h) < 720",
            ["720"] = "MIN(df.res_w, df.res_h) >= 720 AND MIN(df.res_w, df.res_h) < 1080",
            ["1080"] = "MIN(df.res_w, df.res_h) >= 1080 AND MIN(df.res_w, df.res_h) < 2048",
            ["2160"] = "MIN(df.res_w, df.res_h) >= 2048"
        };
}

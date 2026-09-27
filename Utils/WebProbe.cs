using System.Text.RegularExpressions;

namespace ckapi.Utils;

/// <summary>
/// 对端应答的通用判断。放 Utils 是因为抓取通道和各个内置档案源都要用同一份指纹：
/// 把"被拦"误判成"没有这条数据"，就会继续按原计划再敲几千次。
/// </summary>
public static class WebProbe
{
    /// <summary>挑战页/拒答页的指纹。命中就当"被拦"，不当"没有这条数据"</summary>
    public static string? Challenge(string body)
    {
        if (string.IsNullOrEmpty(body)) return "空响应";
        if (body.Contains("Imunify360", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("bot-protection", StringComparison.OrdinalIgnoreCase))
            return "被反爬拦下（Imunify360 bot-protection）—— 站方要求把自动化 IP 加白名单";
        if (body.Length < 4096 &&
            (body.Contains("One moment, please", StringComparison.OrdinalIgnoreCase) ||
             body.Contains("cf-browser-verification", StringComparison.OrdinalIgnoreCase) ||
             body.Contains("Just a moment", StringComparison.OrdinalIgnoreCase)))
            return "被反爬拦下（浏览器挑战页）";
        return null;
    }
}

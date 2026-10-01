using System.Text;

namespace ckapi.Utils;

/// <summary>
/// 字幕格式转换：把盘上那份文件变成浏览器 <track> 认的东西。
///
/// 为什么必须转一道：原生 <code>&lt;track&gt;</code> 只吃 WebVTT，而且判定很硬——
/// 第一行要以 <code>WEBVTT</code> 开头，时间戳的小数点必须是句点。
/// SRT 恰好两条都不满足（<code>00:00:02,586 --&gt; ...</code>），直接把 .srt 发给它，
/// Chrome 只会报一次 track parse error，界面上一行字幕都不出现，而 HTTP 那边是 200，
/// 从日志里根本看不出问题。所以在这儿转成 VTT，浏览器拿到的永远是它能解析的东西。
///
/// 编码也是在这儿处理：中文圈的 .srt 一半是 UTF-8、一半是 GBK/GB18030，
/// 而 WebVTT 规定只能按 UTF-8 解 —— 服务端不解码就没法让浏览器解，
/// 直接发 GBK 字节的结果是满屏乱码。这里先用严格 UTF-8 探一次（非法字节会抛），
/// 抛了就退回 GB18030，输出统一是 UTF-8。
/// </summary>
public static class Subtitles
{
    /// <summary>能点亮字幕轨的扩展名。ASS/SSA 的样式与标签语法跟 VTT 差得远，不做自动转换</summary>
    public static readonly string[] DisplayableExtensions = { ".srt", ".vtt" };

    public static bool IsDisplayable(string? extension)
        => DisplayableExtensions.Contains((extension ?? "").ToLowerInvariant());

    /// <summary>GB18030 这类代码页不在 .NET 默认自带的编码表里，得先注册 provider 才取到</summary>
    static Subtitles() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>
    /// 整份读成 WebVTT 文本。已经是 VTT 的也过一遍收拾（补头、时间戳里的逗号、掉标签），
    /// 因为不少"vtt"是别处转了一半就扔这儿了，缺头一样解析失败。
    /// 不支持的格式返回 null。
    /// </summary>
    public static string? ToWebVtt(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (!IsDisplayable(ext)) return null;

        var text = ReadText(path);
        return ext == ".srt" ? SrtToVtt(text) : EnsureVtt(text);
    }

    /// <summary>
    /// 读文本并统一成 UTF-8。UTF-8 用"抛异常"当探测器（<see cref="DecoderFallbackException"/>
    /// 只在真遇到非法字节时来），比数 BOM 可靠：GBK 文件同样可能没有 BOM，反过来 UTF-8 常常带一个。
    /// </summary>
    public static string ReadText(string path)
    {
        var bytes = System.IO.File.ReadAllBytes(path);
        try
        {
            var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
            var text = strict.GetString(bytes);
            // BOM 会被当成第一个字符留在文首，"WEBVTT" 就不再是开头四个字节了
            return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
        }
        catch (DecoderFallbackException)
        {
            try
            {
                return Encoding.GetEncoding("GB18030").GetString(bytes);
            }
            catch
            {
                // 取不到代码页（比如 provider 没注册）就退回原始 UTF-8 解读，
                // 宁可乱码也别让接口 500 —— 乱码人眼一看就知道是怎么回事
                return Encoding.UTF8.GetString(bytes);
            }
        }
    }

    /// <summary>SRT → WebVTT。逐行改写，不改字幕内容本身。</summary>
    public static string SrtToVtt(string srt)
    {
        var sb = new StringBuilder("WEBVTT\n\n");
        foreach (var line in Normalize(srt))
        {
            if (line.Contains("-->")) { sb.Append(FixTimeLine(line)); sb.Append('\n'); continue; }
            sb.Append(CleanText(line)); sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>补上缺的头，并顺手把时间戳与标签收拾一遍</summary>
    public static string EnsureVtt(string vtt)
    {
        var lines = Normalize(vtt);
        var head = lines.Length > 0 ? lines[0].TrimStart() : "";
        var sb = new StringBuilder();
        if (!head.StartsWith("WEBVTT", StringComparison.OrdinalIgnoreCase)) sb.Append("WEBVTT\n\n");
        foreach (var line in lines)
        {
            if (line.Contains("-->")) sb.Append(FixTimeLine(line));
            else sb.Append(CleanText(line));
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string[] Normalize(string text)
        => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    /// <summary>
    /// 时间行：<code>0:01:02,500 --&gt; 0:01:05,000</code> → <code>00:01:02.500 --&gt; 00:01:05.000</code>。
    /// 后面可能还跟着 VTT 的轨道设置（<code>line:80%</code> 之类），原样留着——那是合法也有用的。
    /// </summary>
    private static string FixTimeLine(string line)
    {
        var parts = line.Split("-->", 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return line;

        var right = parts[1].Split(' ', 2, StringSplitOptions.TrimEntries);
        var tail = right.Length > 1 ? " " + right[1] : "";
        return $"{NormalizeStamp(parts[0])} --> {NormalizeStamp(right[0])}{tail}";
    }

    /// <summary>
    /// 单个时刻：允许 2 段（分:秒）或 3 段（时:分:秒）、逗号或句点当毫秒分隔，一律补成
    /// <code>HH:MM:SS.mmm</code>。写不回去就原样返回——宁可让这一行不显示，也别把整份字幕弄坏。
    /// </summary>
    private static string NormalizeStamp(string raw)
    {
        var s = raw.Trim();
        var dot = s.IndexOfAny(new[] { ',', '.' });
        var ms = dot >= 0 ? s[(dot + 1)..] : "000";
        if (dot >= 0) s = s[..dot];
        if (ms.Length == 1) ms += "00";
        if (ms.Length == 2) ms += "0";
        if (ms.Length != 3 || !long.TryParse(ms, out _)) ms = "000";

        var hms = s.Split(':');
        return hms.Length switch
        {
            3 => $"{Pad(hms[0])}:{Pad(hms[1])}:{Pad(hms[2])}.{ms}",
            2 => $"00:{Pad(hms[0])}:{Pad(hms[1])}.{ms}",
            _ => raw
        };
    }

    private static string Pad(string part)
        => int.TryParse(part, out var n) ? n.ToString("D2") : part;

    /// <summary>
    /// 文本行清两样东西：ASS 风格的一次性覆盖块 <code>{\an8}</code>（VTT 不认识，会原样显示出来），
    /// 以及 <code>&lt;i&gt;&lt;b&gt;&lt;u&gt;&lt;br&gt;</code> 之外的 HTML 标签——
    /// 字幕组爱用 <code>&lt;font #0099CC&gt;</code>，而 VTT 里非法标签会让整条 cue 解析失败。
    /// </summary>
    private static string CleanText(string line)
    {
        var text = Curly(line);
        return Tags(text);
    }

    private static string Curly(string line)
    {
        if (!line.Contains('{') || !line.Contains('}')) return line;
        var sb = new StringBuilder(line.Length);
        var depth = 0;
        foreach (var c in line)
        {
            if (c == '{') { depth++; continue; }
            if (c == '}') { if (depth > 0) depth--; continue; }
            if (depth == 0) sb.Append(c);
        }
        return sb.ToString();
    }

    private static string Tags(string line)
    {
        if (!line.Contains('<') || !line.Contains('>')) return line;
        var sb = new StringBuilder(line.Length);
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] != '<') { sb.Append(line[i]); continue; }
            var end = line.IndexOf('>', i);
            if (end < 0) { sb.Append(line, i, line.Length - i); break; }
            var tag = line[(i + 1)..end].TrimStart('/', '!').Trim();
            // VTT 允许的内联标签就这几个，剩下的（font/span 带属性的）一律摘掉但留下里面的字
            var name = new string(tag.TakeWhile(char.IsLetter).ToArray()).ToLowerInvariant();
            if (name is "i" or "b" or "u" or "br" or "c" or "v" or "lang")
                sb.Append(line[i..(end + 1)]);
            i = end;
        }
        return sb.ToString();
    }
}

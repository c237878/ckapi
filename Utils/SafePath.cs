namespace ckapi.Utils;

/// <summary>
/// 对外暴露的文件读取接口做路径校验：这几个控制器会把 URL 里的路径/文件名直接拼进磁盘路径，
/// 未校验时可读到库外的任意文件。
/// </summary>
public static class SafePath
{
    private static readonly HashSet<string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp"
    };

    /// <summary>
    /// 校验"必须是单层文件名"。含路径分隔符、.. 或以 . 开头（隐藏文件）时返回 null。
    /// </summary>
    public static string? AsFileName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (raw.Contains("..") || raw.Contains('/') || raw.Contains('\\')) return null;
        if (raw.StartsWith('.')) return null;
        if (raw.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        if (!string.Equals(Path.GetFileName(raw), raw, StringComparison.Ordinal)) return null;
        return raw;
    }

    /// <summary>
    /// candidate 解析成绝对路径后是否位于 root 目录之内。
    /// </summary>
    public static bool IsInside(string? candidate, string? root)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(root)) return false;

        try
        {
            var full = Path.GetFullPath(candidate);
            var fullRoot = Path.GetFullPath(root);
            if (!fullRoot.EndsWith(Path.DirectorySeparatorChar.ToString()))
                fullRoot += Path.DirectorySeparatorChar;
            return full.StartsWith(fullRoot, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsImageFile(string? path)
        => ImageTypes.Contains(Path.GetExtension(path ?? string.Empty));

    /// <summary>
    /// 图片扩展名 → Content-Type；非图片返回 null（调用方应直接拒绝，别再回退成 octet-stream）。
    /// </summary>
    public static string? ImageContentType(string? path) => Path.GetExtension(path ?? string.Empty).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        _ => null
    };
}

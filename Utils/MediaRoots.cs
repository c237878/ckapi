namespace ckapi.Utils;

/// <summary>
/// 媒体根目录白名单。图片代理类接口接受调用方传来的磁盘路径，
/// 不做约束时可以读取服务器上任意文件（如 ~/.ssh/、/etc/），因此统一限定在挂载卷内。
/// </summary>
public static class MediaRoots
{
    private static readonly string[] Fallback = { "/Volumes" };

    public static bool IsAllowed(IConfiguration config, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        var roots = config.GetSection("Media:Roots").Get<string[]>();
        if (roots is not { Length: > 0 }) roots = Fallback;

        return roots.Any(root => SafePath.IsInside(path, root));
    }
}

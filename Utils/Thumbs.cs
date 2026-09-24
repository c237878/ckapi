using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace ckapi.Utils;

/// <summary>
/// 演员图片的缩略图：按需生成、落盘缓存、源文件更新即失效。
///
/// 为什么要缩略图：演员目录里的原图平均 500KB、最大 2MB 一张，而相册画框只有 400px 宽，
/// 列表页的头像更是 160px 都嫌多。艳图那种一面墙几十个格子直接拉原图，
/// 等于每次浏览都要从挂载卷搬几十兆字节。
///
/// 缓存键用「目标宽度 + 源文件名」，失效判断只比 mtime：缩略图比源文件旧就重生成。
/// 所以替换磁盘上的同名图片不需要任何"清缓存"动作，也不需要往库里塞签名列。
/// </summary>
public static class Thumbs
{
    /// <summary>尺寸档位：路由里就用这两个字母，别传任意宽度，否则缓存目录会被无限撑大</summary>
    public static readonly (string Name, int Width)[] Sizes =
    {
        ("s", 160),   // 列表页头像
        ("m", 400),   // 详情页相册
    };

    public static int WidthOf(string? size)
        => Sizes.FirstOrDefault(x => x.Name == size).Width;

    /// <summary>
    /// 生成（或复用）一张缩略图，返回落盘路径与源图尺寸；源图读不出来时返回 null。
    /// 源图尺寸顺带回填给 actor_images，省掉一次单独的 Image.Identify。
    /// 复用已有缓存时源尺寸不重新解码，返回 0 表示"这次没读到"。
    /// </summary>
    public static (string Path, int SourceWidth, int SourceHeight)? Ensure(
        string sourceFile, string cacheRoot, string actorId, string fileName, int width,
        out int sourceWidth, out int sourceHeight)
    {
        sourceWidth = sourceHeight = 0;

        var source = new FileInfo(sourceFile);
        if (!source.Exists || source.Length == 0) return null;

        // 文件名里带上原扩展名：同目录下 a.jpg 与 a.png 是两个不同的源
        var target = Path.Combine(cacheRoot, actorId, $"{width}-{fileName}.webp");
        var targetInfo = new FileInfo(target);
        if (targetInfo.Exists && targetInfo.LastWriteTimeUtc >= source.LastWriteTimeUtc)
            return (target, 0, 0);

        try
        {
            using var image = Image.Load(sourceFile);
            sourceWidth = image.Width;
            sourceHeight = image.Height;

            // 手机拍的图方向信息常在 EXIF 里，不 AutoOrient 会存出一堆躺倒的缩略图
            image.Mutate(x => x.AutoOrient().Resize(new ResizeOptions
            {
                Mode = ResizeMode.Max,
                // 高度给足 = 只按宽收缩，且永不放大（Max 不会超出源图）
                Size = new Size(width, width * 20)
            }));

            Directory.CreateDirectory(targetInfo.DirectoryName!);

            // 先写临时文件再改名：两个请求同时命中同一张图时不会互相读到半截文件
            var temp = target + $".{Environment.ProcessId}.tmp";
            image.Save(temp, new WebpEncoder { Quality = 80 });
            File.Move(temp, target, overwrite: true);

            return (target, sourceWidth, sourceHeight);
        }
        catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException or IOException)
        {
            return null;
        }
    }
}

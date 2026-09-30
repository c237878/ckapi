using System.Security.Cryptography;

namespace ckapi.Utils;

/// <summary>
/// 文件内容指纹：头尾各 64KB + 文件大小，SHA256。
///
/// 为什么不整片算 MD5：库在 SMB 共享上，一个 4GB 的文件读全片就是几分钟，
/// 三千多个文件根本扫不完。而"同一部片重复入库"这种场景，大小相同 + 头尾各 64KB 相同
/// 已经足够说明问题（头是容器盒子和轨信息，尾是 moov/mdat 的收梢），Stash 也是同样的思路。
///
/// 前缀写算法名（ht64:）：以后换算法或改窗口大小时，新旧指纹能共存也不会互相误判成重复。
/// </summary>
public static class FileFingerprint
{
    public const string Prefix = "ht64:";
    private const int Window = 64 * 1024;

    /// <summary>算不出来（文件不在、没权限）返回 null，调用方保留原值别清空。</summary>
    public static string? Compute(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var size = fs.Length;
            if (size <= 0) return null;

            using var sha = SHA256.Create();
            var buffer = new byte[Window];

            var head = ReadAt(fs, 0, buffer);
            sha.TransformBlock(head, 0, head.Length, null, 0);

            // 小文件只有一份，头尾会重叠——重叠也算进来：同一算法对同一文件必须给同一个值
            var tailStart = Math.Max(0, size - Window);
            if (size > Window)
            {
                fs.Position = tailStart;
                var tail = ReadAt(fs, tailStart, buffer);
                sha.TransformBlock(tail, 0, tail.Length, null, 0);
            }

            // 大小按 8 字节并进哈希：只比内容块不够，截断过的文件头尾可能完全一样
            var len = BitConverter.GetBytes(size);
            sha.TransformFinalBlock(len, 0, len.Length);

            return Prefix + Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>从固定位置读满一块；读不满就用实际读到的长度。</summary>
    private static byte[] ReadAt(FileStream fs, long offset, byte[] buffer)
    {
        fs.Position = offset;
        var read = 0;
        while (read < buffer.Length)
        {
            var n = fs.Read(buffer, read, buffer.Length - read);
            if (n <= 0) break;
            read += n;
        }
        return read == buffer.Length ? buffer : buffer[..read];
    }
}

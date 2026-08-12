namespace ckapi.Models;

/// <summary>
/// 漫画解密配置
/// </summary>
public class ComicDecryptConfig
{
    /// <summary>
    /// 切割行数（将图片纵向切割为多少行）
    /// </summary>
    public int Rows { get; set; } = 3;

    /// <summary>
    /// 行顺序数组，例如 [2,0,1] 表示打乱后第3行在第1位，第1行在第2位，第2行在第3位
    /// 还原时按此顺序逆向拼接
    /// </summary>
    public List<int> Order { get; set; } = new();
}

/// <summary>
/// 批量解密任务请求
/// </summary>
public class DecryptTaskRequest
{
    public string? ChapterId { get; set; }
    /// <summary>
    /// 加密图片的文件名列表（为空则处理目录下所有图片）
    /// </summary>
    public List<string>? ImageNames { get; set; }
    /// <summary>
    /// 解密配置
    /// </summary>
    public ComicDecryptConfig? Config { get; set; }
    /// <summary>
    /// 是否覆盖已解密的文件
    /// </summary>
    public bool Overwrite { get; set; } = false;
}

/// <summary>
/// 单张图片解密请求
/// </summary>
public class DecryptImageRequest
{
    public string? ChapterId { get; set; }
    public string? ImageName { get; set; }
    public ComicDecryptConfig? Config { get; set; }
    public bool Overwrite { get; set; } = false;
}
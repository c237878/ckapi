namespace ckapi.Models;

/// <summary>
/// 影视系列模型
/// </summary>
public class VideoSeries
{
    /// <summary>
    /// 主键ID
    /// </summary>
    public string? Id { get; set; }

    /// <summary>
    /// 名称
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// 别名
    /// </summary>
    public string? Alias { get; set; }

    /// <summary>
    /// 链接
    /// </summary>
    public string? Link { get; set; }

    /// <summary>
    /// 所属国家
    /// </summary>
    public string? Country { get; set; }

    /// <summary>
    /// 创建时间
    /// </summary>
    public string? CTime { get; set; }

    /// <summary>
    /// 修改时间
    /// </summary>
    public string? UTime { get; set; }

    /// <summary>
    /// 关联影片数量
    /// </summary>
    public int VideoCount { get; set; }
}

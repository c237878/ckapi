namespace ckapi.Models;

/// <summary>
/// 影片模型
/// </summary>
public class Video
{
    /// <summary>
    /// 主键ID
    /// </summary>
    public string? Id { get; set; }

    /// <summary>
    /// 番号
    /// </summary>
    public string? Code { get; set; }

    /// <summary>
    /// 名称
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// 所属国家
    /// </summary>
    public string? Country { get; set; }

    /// <summary>
    /// 封面图片地址
    /// </summary>
    public string? CoverUrl { get; set; }

    /// <summary>
    /// 主机地址
    /// </summary>
    public string? VideoUrl { get; set; }

    /// <summary>
    /// 视频大小（字节）
    /// </summary>
    public long VideoSize { get; set; }

    /// <summary>
    /// 视频质量标记（如：4K, 1080P, 720P等）
    /// </summary>
    public string? Quality { get; set; }

    /// <summary>
    /// 所属系列ID
    /// </summary>
    public string? SeriesId { get; set; }

    /// <summary>
    /// 排序序号
    /// </summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// 创建时间
    /// </summary>
    public string? CTime { get; set; }

    /// <summary>
    /// 修改时间
    /// </summary>
    public string? UTime { get; set; }

    /// <summary>
    /// 关联演员列表
    /// </summary>
    public List<Actor>? Actors { get; set; }
}

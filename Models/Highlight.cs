namespace ckapi.Models;

/// <summary>
/// 精彩集锦模型
/// </summary>
public class Highlight
{
    /// <summary>
    /// 主键ID
    /// </summary>
    public string? Id { get; set; }

    /// <summary>
    /// 图片（Base64）
    /// </summary>
    public string? Image { get; set; }

    /// <summary>
    /// 对应演员ID
    /// </summary>
    public string? ActorId { get; set; }

    /// <summary>
    /// 对应影片ID
    /// </summary>
    public string? VideoId { get; set; }

    /// <summary>
    /// 标题
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// 描述
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// 创建时间
    /// </summary>
    public string? CTime { get; set; }

    /// <summary>
    /// 修改时间
    /// </summary>
    public string? UTime { get; set; }
}

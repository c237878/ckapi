namespace ckapi.Models;

/// <summary>
/// 视频-演员关联模型
/// </summary>
public class VideoActor
{
    /// <summary>
    /// 主键ID
    /// </summary>
    public string? Id { get; set; }

    /// <summary>
    /// 视频ID
    /// </summary>
    public string? VideoId { get; set; }

    /// <summary>
    /// 演员ID
    /// </summary>
    public string? ActorId { get; set; }

    /// <summary>
    /// 创建时间
    /// </summary>
    public string? CTime { get; set; }

    /// <summary>
    /// 修改时间
    /// </summary>
    public string? UTime { get; set; }
}

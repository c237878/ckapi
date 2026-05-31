namespace ckapi.Models;

/// <summary>
/// 点赞记录模型
/// </summary>
public class LikeRecord
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
    /// 点赞时间
    /// </summary>
    public string? LikeTime { get; set; }

    /// <summary>
    /// 用户标识（可选，用于防重复点赞）
    /// </summary>
    public string? UserToken { get; set; }

    /// <summary>
    /// 创建时间
    /// </summary>
    public string? CTime { get; set; }

    /// <summary>
    /// 修改时间
    /// </summary>
    public string? UTime { get; set; }
}

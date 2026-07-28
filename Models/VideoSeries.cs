using System.Text.Json.Serialization;

namespace ckapi.Models;

/// <summary>
/// 影视系列模型
/// </summary>
public class VideoSeries
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>
    /// 名称
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    /// 别名
    /// </summary>
    [JsonPropertyName("alias")]
    public string? Alias { get; set; }

    /// <summary>
    /// 链接
    /// </summary>
    [JsonPropertyName("link")]
    public string? Link { get; set; }

    /// <summary>
    /// 所属国家
    /// </summary>
    [JsonPropertyName("country")]
    public string? Country { get; set; }

    /// <summary>
    /// 创建时间
    /// </summary>
    [JsonPropertyName("ctime")]
    public string? CTime { get; set; }

    /// <summary>
    /// 修改时间
    /// </summary>
    [JsonPropertyName("utime")]
    public string? UTime { get; set; }

    /// <summary>
    /// 关联影片数量
    /// </summary>
    public int VideoCount { get; set; }

    /// <summary>
    /// 获赞总数
    /// </summary>
    public int LikeCount { get; set; }

    /// <summary>
    /// 未下载影片数量
    /// </summary>
    [JsonPropertyName("unloadedCount")]
    public int UnloadedCount { get; set; }
}

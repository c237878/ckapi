namespace ckapi.Models;

/// <summary>
/// 演员模型
/// </summary>
public class Actor
{
    /// <summary>
    /// 主键ID
    /// </summary>
    public string? Id { get; set; }

    /// <summary>
    /// 姓名
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// 别名
    /// </summary>
    public string? Alias { get; set; }

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
    /// 参演影片数量
    /// </summary>
    public int VideoCount { get; set; }
}

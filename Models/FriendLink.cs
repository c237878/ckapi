namespace ckapi.Models;

/// <summary>
/// 友情链接模型
/// </summary>
public class FriendLink
{
    /// <summary>
    /// 主键ID
    /// </summary>
    public string? Id { get; set; }

    /// <summary>
    /// 网站名称
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// 网站链接
    /// </summary>
    public string? Link { get; set; }

    /// <summary>
    /// Logo（Base64或URL）
    /// </summary>
    public string? Logo { get; set; }

    /// <summary>
    /// 描述
    /// </summary>
    public string? Description { get; set; }

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
}

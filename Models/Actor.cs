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
    /// 头像路径
    /// </summary>
    public string? AvatarPath { get; set; }

    /// <summary>
    /// 所属国家
    /// </summary>
    public string? Country { get; set; }

    /// <summary>
    /// 个人简介
    /// </summary>
    public string? Bio { get; set; }

    /// <summary>
    /// 添加时间
    /// </summary>
    public string? AddedAt { get; set; }

    /// <summary>
    /// 参演影片数量（非数据库字段）
    /// </summary>
    public int VideoCount { get; set; }
}

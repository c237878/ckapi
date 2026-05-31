namespace ckapi.Models;

/// <summary>
/// 系统设置模型
/// </summary>
public class SystemSetting
{
    /// <summary>
    /// 主键ID
    /// </summary>
    public string? Id { get; set; }

    /// <summary>
    /// 设置名称
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// 设置内容
    /// </summary>
    public string? Content { get; set; }

    /// <summary>
    /// 创建时间
    /// </summary>
    public string? CTime { get; set; }

    /// <summary>
    /// 修改时间
    /// </summary>
    public string? UTime { get; set; }
}

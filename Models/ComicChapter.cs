namespace ckapi.Models;

/// <summary>
/// 修改章节请求
/// </summary>
public class UpdateChapterRequest
{
    public string? Title { get; set; }
    public string? Directory { get; set; }
    public int? SortOrder { get; set; }
}

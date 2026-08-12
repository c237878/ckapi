namespace ckapi.Models;

/// <summary>
/// 漫画章节模型
/// </summary>
public class ComicChapter
{
    public string? Id { get; set; }
    public string? ComicId { get; set; }
    public string? Title { get; set; }
    public string? Directory { get; set; }
    public int SortOrder { get; set; }
    public int ImageCount { get; set; }
    public string? CTime { get; set; }
}

/// <summary>
/// 漫画章节图片模型
/// </summary>
public class ComicChapterImage
{
    public string? Id { get; set; }
    public string? ChapterId { get; set; }
    public string? FileName { get; set; }
    public int SortOrder { get; set; }
    public string? DecryptedPath { get; set; }
}

/// <summary>
/// 修改章节请求
/// </summary>
public class UpdateChapterRequest
{
    public string? Title { get; set; }
    public string? Directory { get; set; }
    public int? SortOrder { get; set; }
}
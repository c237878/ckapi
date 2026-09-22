namespace ckapi.Models;

/// <summary>
/// 添加漫画请求
/// </summary>
public class AddComicRequest
{
    public string? Name { get; set; }
    public string? Author { get; set; }
    public string? Description { get; set; }
    public string? Url { get; set; }
    public string? CoverPath { get; set; }
    public string? Directory { get; set; }
    public int Status { get; set; } = 0;
}

/// <summary>
/// 更新漫画请求
/// </summary>
public class UpdateComicRequest
{
    public string? Name { get; set; }
    public string? Author { get; set; }
    public string? Description { get; set; }
    public string? Url { get; set; }
    public string? CoverPath { get; set; }
    public string? Directory { get; set; }
    public int Status { get; set; } = 0;
}

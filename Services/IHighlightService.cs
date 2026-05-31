using ckapi.Models;

namespace ckapi.Services;

/// <summary>
/// 精彩集锦服务接口
/// </summary>
public interface IHighlightService
{
    Task<List<Highlight>> GetHighlightsAsync(int page, int pageSize, string? actorId, string? videoId);
    Task<Highlight?> GetHighlightByIdAsync(string id);
    Task<Highlight> AddHighlightAsync(Highlight highlight);
    Task<bool> UpdateHighlightAsync(string id, Highlight highlight);
    Task<bool> DeleteHighlightAsync(string id);
}

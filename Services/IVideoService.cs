using ckapi.Models;

namespace ckapi.Services;

/// <summary>
/// 视频服务接口
/// </summary>
public interface IVideoService
{
    Task<List<Video>> GetVideosAsync(int page, int pageSize, string? seriesId, string? country);
    Task<Video?> GetVideoByIdAsync(string id);
    Task<List<Video>> GetRecommendAsync(int limit);
    Task<List<Video>> GetLatestAsync(int limit);
    Task<List<object>> GetMostLikedAsync(int limit);
    Task<Video> AddVideoAsync(Video video);
    Task<bool> UpdateVideoAsync(string id, Video video);
    Task<bool> DeleteVideoAsync(string id);
}

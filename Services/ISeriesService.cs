using ckapi.Models;

namespace ckapi.Services;

/// <summary>
/// 系列服务接口
/// </summary>
public interface ISeriesService
{
    Task<List<VideoSeries>> GetSeriesAsync(int page, int pageSize, string? country);
    Task<VideoSeries?> GetSeriesByIdAsync(string id);
    Task<List<Video>> GetSeriesVideosAsync(string seriesId, int page, int pageSize);
    Task<VideoSeries> AddSeriesAsync(VideoSeries series);
    Task<bool> UpdateSeriesAsync(string id, VideoSeries series);
    Task<bool> DeleteSeriesAsync(string id);
}

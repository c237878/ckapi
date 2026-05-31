using ckapi.Models;

namespace ckapi.Services;

/// <summary>
/// 点赞服务接口
/// </summary>
public interface ILikeService
{
    Task<bool> LikeVideoAsync(string videoId, string? userToken);
    Task<bool> UnlikeVideoAsync(string videoId, string? userToken);
    Task<int> GetLikeCountAsync(string videoId);
    Task<bool> CheckLikedAsync(string videoId, string? userToken);
}

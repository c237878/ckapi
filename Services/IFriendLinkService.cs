using ckapi.Models;

namespace ckapi.Services;

/// <summary>
/// 友情链接服务接口
/// </summary>
public interface IFriendLinkService
{
    Task<List<FriendLink>> GetFriendLinksAsync();
    Task<FriendLink?> GetFriendLinkByIdAsync(string id);
    Task<FriendLink> AddFriendLinkAsync(FriendLink link);
    Task<bool> UpdateFriendLinkAsync(string id, FriendLink link);
    Task<bool> DeleteFriendLinkAsync(string id);
}

using ckapi.Models;

namespace ckapi.Services;

/// <summary>
/// 演员服务接口
/// </summary>
public interface IActorService
{
    Task<List<Actor>> GetActorsAsync(int page, int pageSize, string? country);
    Task<Actor?> GetActorByIdAsync(string id);
    Task<Actor> AddActorAsync(Actor actor);
    Task<bool> UpdateActorAsync(string id, Actor actor);
    Task<bool> DeleteActorAsync(string id);
    Task<List<Video>> GetActorVideosAsync(string actorId, int page, int pageSize);
}

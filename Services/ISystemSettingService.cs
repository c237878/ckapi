using ckapi.Models;

namespace ckapi.Services;

/// <summary>
/// 系统设置服务接口
/// </summary>
public interface ISystemSettingService
{
    Task<List<SystemSetting>> GetAllSettingsAsync();
    Task<SystemSetting?> GetSettingByNameAsync(string name);
    Task<SystemSetting> SetSettingAsync(string name, string content);
    Task<bool> DeleteSettingAsync(string id);
}

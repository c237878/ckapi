using System.Diagnostics;

namespace ckapi.Services;

/// <summary>
/// Samba共享服务 - 调用macOS sharing命令管理文件共享
/// </summary>
public class SambaService
{
    private readonly ILogger<SambaService> _logger;

    public SambaService(ILogger<SambaService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 获取所有共享点列表
    /// </summary>
    public async Task<List<SharePointInfo>> GetSharePointsAsync()
    {
        var result = new List<SharePointInfo>();
        var output = await RunSharingAsync("-l -f json");
        if (string.IsNullOrEmpty(output)) return result;

        try
        {
            var json = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, SharePointRaw>>(output);
            if (json == null) return result;

            foreach (var kvp in json)
            {
                result.Add(new SharePointInfo
                {
                    Name = kvp.Value.smb_name ?? kvp.Key,
                    Path = kvp.Value.path,
                    SMBShared = kvp.Value.smb_shared == 1,
                    GuestAccess = kvp.Value.smb_guest_access == 1,
                    ReadOnly = kvp.Value.smb_read_only == 1
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "解析sharing -l JSON失败: {output}", output);
        }

        return result;
    }

    /// <summary>
    /// 添加共享目录
    /// </summary>
    public async Task<(bool Success, string Message, string? ShareName)> AddShareAsync(
        string path, string? shareName = null, bool smbEnabled = true,
        bool guestAccess = true, bool readOnly = false)
    {
        if (!Directory.Exists(path))
        {
            return (false, $"目录不存在: {path}", null);
        }

        // 检查是否已存在
        var existing = await GetSharePointsAsync();
        var existingOne = existing.FirstOrDefault(s => s.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (existingOne != null)
        {
            return (false, $"该目录已被共享为: {existingOne.Name}", existingOne.Name);
        }

        // 生成共享名
        var name = shareName ?? Path.GetFileName(path);

        // 使用 -n 设置记录名，-S 设置SMB名称
        var args = $"-a \"{path}\" -n \"{name}\" -S \"{name}\"";
        var output = await RunSharingAsync(args);
        if (!string.IsNullOrEmpty(output) && output.Contains("error", StringComparison.OrdinalIgnoreCase))
        {
            return (false, output, null);
        }

        // 配置 SMB 权限
        var smbFlag = smbEnabled ? "001" : "000";
        var guestFlag = guestAccess ? "001" : "000";

        await RunSharingAsync($"-e \"{name}\" -s {smbFlag} -g {guestFlag} -R {(readOnly ? "1" : "0")}");

        return (true, "共享添加成功", name);
    }

    /// <summary>
    /// 更新共享配置
    /// </summary>
    public async Task<(bool Success, string Message)> UpdateShareAsync(
        string shareName, bool? smbEnabled = null, bool? guestAccess = null, bool? readOnly = null)
    {
        var smbFlag = smbEnabled.HasValue ? (smbEnabled.Value ? "001" : "000") : null;
        var guestFlag = guestAccess.HasValue ? (guestAccess.Value ? "001" : "000") : null;
        var readOnlyVal = readOnly.HasValue ? (readOnly.Value ? "1" : "0") : null;

        var args = $"-e \"{shareName}\"";
        if (smbFlag != null) args += $" -s {smbFlag}";
        if (guestFlag != null) args += $" -g {guestFlag}";
        if (readOnlyVal != null) args += $" -R {readOnlyVal}";

        var output = await RunSharingAsync(args);
        if (!string.IsNullOrEmpty(output) && output.Contains("error", StringComparison.OrdinalIgnoreCase))
        {
            return (false, output);
        }

        return (true, "共享配置已更新");
    }

    /// <summary>
    /// 删除共享目录
    /// </summary>
    public async Task<(bool Success, string Message)> RemoveShareAsync(string shareName)
    {
        var output = await RunSharingAsync($"-r \"{shareName}\"");
        if (!string.IsNullOrEmpty(output) && output.Contains("error", StringComparison.OrdinalIgnoreCase))
        {
            return (false, output);
        }
        return (true, "共享已删除");
    }

    /// <summary>
    /// 测试SMB连接
    /// </summary>
    public async Task<(bool Success, string Message)> TestConnectionAsync(string host)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "smbutil",
            Arguments = $"status {host}",
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        try
        {
            using var process = Process.Start(psi);
            if (process == null) return (false, "无法启动smbutil");

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode == 0)
            {
                return (true, $"连接成功: {output}");
            }
            return (false, error.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task<string> RunSharingAsync(string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "/usr/sbin/sharing",
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        try
        {
            using var process = Process.Start(psi);
            if (process == null) return "无法启动sharing命令";

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            return string.IsNullOrEmpty(error) ? output.Trim() : error.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "执行sharing命令失败: {args}", args);
            return ex.Message;
        }
    }
}

public class SharePointInfo
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool SMBShared { get; set; }
    public bool GuestAccess { get; set; }
    public bool ReadOnly { get; set; }
}

public class SharePointRaw
{
    public string path { get; set; } = "";
    public string smb_name { get; set; } = "";
    public int smb_shared { get; set; }
    public int smb_guest_access { get; set; }
    public int smb_read_only { get; set; }
    public int smb_sealed { get; set; }
}
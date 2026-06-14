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
        var (exitCode, output) = await RunSharingAsync("-l -f json");
        if (exitCode != 0 || string.IsNullOrEmpty(output)) return result;

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
        var (exitCode, output) = await RunSharingAsync(args);
        if (exitCode != 0)
        {
            return (false, $"创建共享失败: {output}", null);
        }

        // 配置 SMB 权限
        var smbFlag = smbEnabled ? "001" : "000";
        var guestFlag = guestAccess ? "001" : "000";

        var (exitCode2, output2) = await RunSharingAsync($"-e \"{name}\" -s {smbFlag} -g {guestFlag} -R {(readOnly ? "1" : "0")}");
        if (exitCode2 != 0)
        {
            _logger.LogWarning("配置SMB权限失败: {output}", output2);
        }

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

        var (exitCode, output) = await RunSharingAsync(args);
        if (exitCode != 0)
        {
            return (false, $"更新失败: {output}");
        }

        return (true, "共享配置已更新");
    }

    /// <summary>
    /// 删除共享目录
    /// </summary>
    public async Task<(bool Success, string Message)> RemoveShareAsync(string shareName)
    {
        var (exitCode, output) = await RunSharingAsync($"-r \"{shareName}\"");
        if (exitCode != 0)
        {
            return (false, $"删除失败: {output}");
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

    /// <summary>
    /// 执行sharing命令。写操作（-a/-e/-r）需要管理员权限，通过osascript获取；
    /// 读操作（-l）不需要管理员权限，直接执行。
    /// </summary>
    private async Task<(int ExitCode, string Output)> RunSharingAsync(string args)
    {
        var isReadOnly = args.TrimStart().StartsWith("-l");
        string? tmpScript = null;

        ProcessStartInfo psi;
        if (isReadOnly)
        {
            // 查询操作不需要root
            psi = new ProcessStartInfo
            {
                FileName = "/usr/sbin/sharing",
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
        }
        else
        {
            // 写操作需要管理员权限，通过osascript获取
            // 弹出macOS管理员授权对话框
            // 写临时脚本文件，避免命令行引号转义问题
            var script = $"do shell script \"/usr/sbin/sharing {args}\" with administrator privileges";
            tmpScript = Path.Combine(Path.GetTempPath(), $"sharing-{Guid.NewGuid():N}.scpt");
            await File.WriteAllTextAsync(tmpScript, script);
            psi = new ProcessStartInfo
            {
                FileName = "/usr/bin/osascript",
                Arguments = tmpScript,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
        }

        try
        {
            using var process = Process.Start(psi);
            if (process == null) return (-1, "无法启动sharing命令");

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            var combined = string.IsNullOrEmpty(error) ? output.Trim() : error.Trim();
            return (process.ExitCode, combined);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "执行sharing命令失败: {args}", args);
            return (-1, ex.Message);
        }
        finally
        {
            if (tmpScript != null && File.Exists(tmpScript))
            {
                try { File.Delete(tmpScript); } catch { }
            }
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

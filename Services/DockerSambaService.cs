using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ckapi.Services;

/// <summary>
/// Docker Samba 共享服务 - 读写 /tmp/samba-config/smb.conf 并重启容器
/// </summary>
public class DockerSambaService
{
    private readonly ILogger<DockerSambaService> _logger;
    private readonly string _configPath;
    private readonly string _containerName;

    public DockerSambaService(ILogger<DockerSambaService> logger, IConfiguration config)
    {
        _logger = logger;
        _configPath = config["DockerSamba:ConfigPath"] ?? "/tmp/samba-config/smb.conf";
        _containerName = config["DockerSamba:ContainerName"] ?? "samba-server";
    }

    /// <summary>
    /// 获取所有 Docker Samba 共享
    /// </summary>
    public List<SharePointInfo> GetShares()
    {
        var result = new List<SharePointInfo>();
        if (!File.Exists(_configPath)) return result;

        var lines = File.ReadAllLines(_configPath);
        SharePointInfo? current = null;
        string? currentRawName = null;

        foreach (var line in lines)
        {
            var trim = line.Trim();
            if (string.IsNullOrEmpty(trim) || trim.StartsWith("#") || trim.StartsWith(";")) continue;

            var sectionMatch = Regex.Match(trim, @"^\[(.+)\]$");
            if (sectionMatch.Success)
            {
                if (current != null && currentRawName != null && !currentRawName.Equals("global", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(current);
                }

                currentRawName = sectionMatch.Groups[1].Value;
                if (currentRawName.Equals("global", StringComparison.OrdinalIgnoreCase))
                {
                    current = null;
                    continue;
                }

                current = new SharePointInfo
                {
                    Name = currentRawName,
                    Path = "",
                    SMBShared = true,
                    GuestAccess = true,
                    ReadOnly = false
                };
                continue;
            }

            if (current == null) continue;

            var parts = trim.Split(new[] { '=' }, 2);
            if (parts.Length < 2) continue;

            var key = parts[0].Trim().ToLowerInvariant();
            var value = parts[1].Trim();

            switch (key)
            {
                case "path":
                    current.Path = value;
                    break;
                case "guest ok":
                    current.GuestAccess = value.Equals("yes", StringComparison.OrdinalIgnoreCase);
                    break;
                case "read only":
                    current.ReadOnly = value.Equals("yes", StringComparison.OrdinalIgnoreCase);
                    break;
                case "browsable":
                case "browseable":
                    current.SMBShared = value.Equals("yes", StringComparison.OrdinalIgnoreCase);
                    break;
            }
        }

        if (current != null && currentRawName != null && !currentRawName.Equals("global", StringComparison.OrdinalIgnoreCase))
        {
            result.Add(current);
        }

        // 将容器内路径转换回宿主机路径
        var mounts = GetContainerMounts();
        foreach (var share in result)
        {
            share.Path = ContainerPathToHostPath(share.Path);
        }

        return result;
    }

    /// <summary>
    /// 获取 Docker 容器当前的所有挂载（容器内路径 → 宿主机路径）
    /// </summary>
    private Dictionary<string, string> GetContainerMounts()
    {
        var mounts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var (exitCode, output) = RunShell("docker", $"inspect {_containerName}");
            if (exitCode != 0) return mounts;

            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0) return mounts;

            var mountsElement = root[0].GetProperty("Mounts");
            foreach (var mount in mountsElement.EnumerateArray())
            {
                if (mount.TryGetProperty("Source", out var sourceEl) &&
                    mount.TryGetProperty("Destination", out var destEl))
                {
                    var src = sourceEl.GetString()?.Replace("\\", "/") ?? "";
                    var dst = destEl.GetString()?.Replace("\\", "/") ?? "";
                    if (!string.IsNullOrEmpty(dst) && !mounts.ContainsKey(dst))
                        mounts[dst] = src;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "获取 Docker 容器挂载信息失败");
        }
        return mounts;
    }

    /// <summary>
    /// 将容器内路径转换为宿主机路径
    /// </summary>
    private string ContainerPathToHostPath(string containerPath)
    {
        var mounts = GetContainerMounts();
        if (mounts.TryGetValue(containerPath, out var hostPath))
            return hostPath;

        // 子路径前缀匹配（如容器内 /share/movies/subdir 匹配挂载点 /share/movies → 宿主机 /Volumes/wdc4t）
        var sorted = mounts.OrderByDescending(m => m.Key.Length);
        foreach (var kvp in sorted)
        {
            if (containerPath.StartsWith(kvp.Key + "/", StringComparison.OrdinalIgnoreCase))
            {
                var suffix = containerPath.Substring(kvp.Key.Length).Replace("\\", "/");
                return kvp.Value.TrimEnd('/') + suffix;
            }
        }

        // 没找到匹配挂载，返回原路径
        return containerPath;
    }

    /// <summary>
    /// 将宿主路径转换为容器内路径
    /// </summary>
    private string HostPathToContainerPath(string hostPath)
    {
        var mounts = GetContainerMounts();
        // 精确匹配
        foreach (var kvp in mounts)
        {
            if (kvp.Value.Equals(hostPath, StringComparison.OrdinalIgnoreCase))
                return kvp.Key;
        }
        // 前缀匹配（子路径）
        var sorted = mounts.OrderByDescending(m => m.Value.Length);
        foreach (var kvp in sorted)
        {
            if (hostPath.StartsWith(kvp.Value + "/", StringComparison.OrdinalIgnoreCase) ||
                hostPath.Equals(kvp.Value, StringComparison.OrdinalIgnoreCase))
            {
                var suffix = hostPath.Substring(kvp.Value.Length);
                return kvp.Key.TrimEnd('/') + suffix.Replace("/", "\\");
            }
        }
        // 没找到匹配挂载，返回原路径（由调用方判断是否有效）
        return hostPath;
    }

    /// <summary>
    /// 添加或更新 Docker Samba 共享
    /// </summary>
    public (bool Success, string Message) UpsertShare(string name, string hostPath, bool guestAccess = true, bool readOnly = false)
    {
        if (!Directory.Exists(hostPath))
        {
            return (false, $"宿主路径目录不存在: {hostPath}");
        }

        // 将宿主路径转为容器内路径
        var containerPath = HostPathToContainerPath(hostPath);

        // 校验：如果转换后的路径与宿主路径相同，说明没有找到对应的容器挂载
        if (containerPath.Equals(hostPath, StringComparison.OrdinalIgnoreCase))
        {
            var mounts = GetContainerMounts();
            var mountList = mounts.Any() 
                ? string.Join(", ", mounts.Select(m => $"{m.Value}→{m.Key}")) 
                : "（无）";
            return (false, $"宿主路径未挂载到 Docker 容器，无法创建共享。\n宿主路径: {hostPath}\n当前已挂载到容器的路径: {mountList}\n\n请先将 {hostPath} 挂载到 Docker 容器，或修改共享路径为已挂载的目录。");
        }

        if (!File.Exists(_configPath))
        {
            var dir = Path.GetDirectoryName(_configPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_configPath, GetDefaultConfig());
        }

        var lines = File.ReadAllLines(_configPath).ToList();
        var sections = ParseSections(lines);

        var sectionName = name.Trim();
        var sectionLines = new List<string>
        {
            $"[{sectionName}]",
            $"   path = {containerPath}",
            "   browsable = yes",
            $"   read only = {(readOnly ? "yes" : "no")}",
            $"   guest ok = {(guestAccess ? "yes" : "no")}",
            "   create mask = 0644",
            "   directory mask = 0755"
        };

        if (sections.TryGetValue(sectionName, out var existing))
        {
            var start = existing.Start;
            var end = existing.End;
            lines.RemoveRange(start, end - start + 1);
            lines.InsertRange(start, sectionLines);
        }
        else
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1])) lines.Add("");
            lines.AddRange(sectionLines);
        }

        File.WriteAllLines(_configPath, lines);
        _logger.LogInformation("Docker Samba 配置已写入: {name} -> 宿主:{hostPath} 容器:{containerPath}", sectionName, hostPath, containerPath);

        return RestartContainer();
    }

    /// <summary>
    /// 启用 Docker Samba 共享（将配置写回 smb.conf 并重启容器）
    /// </summary>
    public (bool Success, string Message) EnableShare(string name, string hostPath, bool guestAccess = true, bool readOnly = false)
    {
        return UpsertShare(name, hostPath, guestAccess, readOnly);
    }

    /// <summary>
    /// 禁用 Docker Samba 共享（从 smb.conf 删除对应 section 并重启容器）
    /// </summary>
    public (bool Success, string Message) DisableShare(string name)
    {
        return RemoveShare(name);
    }

    /// <summary>
    /// 删除 Docker Samba 共享
    /// </summary>
    public (bool Success, string Message) RemoveShare(string name)
    {
        if (!File.Exists(_configPath)) return (false, "配置文件不存在");

        var lines = File.ReadAllLines(_configPath).ToList();
        var sections = ParseSections(lines);
        var sectionName = name.Trim();

        if (!sections.TryGetValue(sectionName, out var existing))
        {
            return (false, $"配置中不存在共享: {name}");
        }

        lines.RemoveRange(existing.Start, existing.End - existing.Start + 1);

        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1])) lines.RemoveAt(lines.Count - 1);

        File.WriteAllLines(_configPath, lines);
        _logger.LogInformation("Docker Samba 共享已删除: {name}", sectionName);

        return RestartContainer();
    }

    /// <summary>
    /// 重启 Docker Samba 容器
    /// </summary>
    public (bool Success, string Message) RestartContainer()
    {
        try
        {
            var (exitCode, output) = RunShell("docker", $"restart {_containerName}");
            if (exitCode != 0)
            {
                return (false, $"重启容器失败: {output}");
            }
            _logger.LogInformation("Docker Samba 容器已重启: {container}", _containerName);
            return (true, output.Trim());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "重启 Docker Samba 容器失败");
            return (false, ex.Message);
        }
    }

    private Dictionary<string, (int Start, int End)> ParseSections(List<string> lines)
    {
        var result = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
        int? currentStart = null;
        string? currentName = null;

        for (int i = 0; i < lines.Count; i++)
        {
            var trim = lines[i].Trim();
            var match = Regex.Match(trim, @"^\[(.+)\]$");
            if (match.Success)
            {
                if (currentStart.HasValue && currentName != null)
                {
                    result[currentName] = (currentStart.Value, i - 1);
                }
                currentStart = i;
                currentName = match.Groups[1].Value;
            }
        }

        if (currentStart.HasValue && currentName != null)
        {
            result[currentName] = (currentStart.Value, lines.Count - 1);
        }

        return result;
    }

    private static (int ExitCode, string Output) RunShell(string command, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null) return (-1, "无法启动进程");
        process.WaitForExit();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        return (process.ExitCode, string.IsNullOrEmpty(output) ? error : output);
    }

    private static string GetDefaultConfig()
    {
        return @"[global]
   workgroup = WORKGROUP
   server string = Samba Server (Docker)
   server min protocol = NT1
   client min protocol = NT1
   ntlm auth = yes
   lanman auth = yes
   client lanman auth = yes
   server signing = auto
   server smb encrypt = disabled
   log level = 3
   log file = /var/log/samba/log.%m
   max log size = 50
   security = user
   map to guest = Bad User
   socket options = TCP_NODELAY SO_RCVBUF=65536 SO_SNDBUF=65536
";
    }
}

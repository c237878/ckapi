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
            // 检查是匿名卷 /data（不需要挂载）还是真正的未挂载路径
            if (!hostPath.StartsWith("/Volumes/") && !hostPath.StartsWith("/Users/") && !hostPath.StartsWith("/tmp/"))
            {
                // 非标准路径，跳过自动重建（可能是匿名卷）
                return (false, $"无法识别的路径格式: {hostPath}");
            }

            // 自动重建容器，将新路径挂载进去
            var (rebuildOk, rebuildMsg) = RebuildContainerWithNewMount(hostPath);
            if (!rebuildOk)
            {
                return (false, $"宿主路径未挂载到容器，自动重建容器失败: {rebuildMsg}");
            }

            // 重建成功后，重新获取容器挂载（用新的 container 实例查询）
            var newMounts = GetContainerMounts();
            containerPath = newMounts.TryGetValue($"/share/{hostPath.Split('/').Last()}", out var cp) 
                ? cp 
                : hostPath;

            // 如果仍未找到挂载，说明该路径在宿主机上不存在
            if (containerPath.Equals(hostPath, StringComparison.OrdinalIgnoreCase))
            {
                return (false, $"容器已重建，但新路径 {hostPath} 在容器中仍不可见（可能该路径在宿主机上不存在或容器未正常启动），请检查。");
            }
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

        // 读取被删除共享的路径
        var removedPath = GetSectionValue(lines, existing.Start, existing.End, "path");

        lines.RemoveRange(existing.Start, existing.End - existing.Start + 1);

        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1])) lines.RemoveAt(lines.Count - 1);

        File.WriteAllLines(_configPath, lines);
        _logger.LogInformation("Docker Samba 共享已删除: {name}", sectionName);

        // 检查是否有其他共享还在使用同一路径的容器挂载
        var remainingSections = ParseSections(File.ReadAllLines(_configPath).ToList());
        var removedContainerPath = removedPath;
        var pathStillUsed = false;
        foreach (var sec in remainingSections)
            {
                var secPath = GetSectionValue(File.ReadAllLines(_configPath).ToList(), sec.Value.Start, sec.Value.End, "path");
                if (!string.IsNullOrEmpty(secPath) && secPath.Equals(removedContainerPath, StringComparison.OrdinalIgnoreCase))
                {
                    pathStillUsed = true;
                    break;
                }
            }

        if (!pathStillUsed && !string.IsNullOrEmpty(removedContainerPath))
        {
            // 该路径没有其他共享引用了，重建容器释放挂载
            _logger.LogInformation("路径 {path} 不再有共享引用，重建容器释放挂载", removedContainerPath);
            var (ok, msg) = RebuildContainerWithoutMount(removedContainerPath);
            if (!ok)
            {
                return (false, $"共享已删除，但释放挂载失败: {msg}（请手动执行 docker stop samba-server 后拔出磁盘）");
            }
            return (true, msg);
        }

        return RestartContainer();
    }

    /// <summary>
    /// 重建容器，去掉指定的容器内路径挂载（保留其他所有挂载）
    /// </summary>
    public (bool Success, string Message) RebuildContainerWithoutMount(string containerPathToRemove)
    {
        try
        {
            var currentMounts = GetContainerMounts();

            // 1. 停止并删除容器
            _logger.LogInformation("停止 Docker Samba 容器准备重建（去掉挂载 {path}）...", containerPathToRemove);
            RunShell("docker", $"stop {_containerName}");
            RunShell("docker", $"rm {_containerName}");

            // 2. 构建新的 docker run 命令，排除要移除的挂载和 smb.conf 自身
            var bindMounts = new List<string>();
            foreach (var m in currentMounts)
            {
                // 跳过非 bind 类型（匿名卷等）
                if (!m.Value.StartsWith("/")) continue;
                // 跳过 smb.conf 自身
                if (m.Value == _configPath) continue;
                // 跳过要移除的容器路径
                if (m.Key.Equals(containerPathToRemove, StringComparison.OrdinalIgnoreCase)) continue;
                // 跳过不存在的旧挂载
                if (!Directory.Exists(m.Value)) continue;

                bindMounts.Add($"-v {m.Value}:{m.Key}");
            }

            // 3. 重新创建容器
            var bindArgs = string.Join(" ", bindMounts);
            var result = RunShell("docker",
                $"run -d --name {_containerName} --restart unless-stopped -p 1445:445 " +
                $"-v /tmp/samba-config/smb.conf:/etc/samba/smb.conf:ro {bindArgs} " +
                $"-e SAMBA_SERVER_STRING=\"Docker Samba\" " +
                $"-e SAMBA_WORKGROUP=WORKGROUP crazymax/samba:latest");

            if (result.ExitCode != 0)
            {
                return (false, $"重建容器失败: {result.Output}");
            }

            _logger.LogInformation("Docker Samba 容器已重建，已移除挂载: {path}", containerPathToRemove);
            return (true, $"共享已删除，容器已重建释放磁盘挂载: {containerPathToRemove}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "重建 Docker Samba 容器失败（移除挂载）");
            return (false, $"重建容器异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 获取指定 section 的配置值
    /// </summary>
    private string GetSectionValue(List<string> lines, int start, int end, string key)
    {
        for (int i = start + 1; i <= end; i++)
        {
            var trim = lines[i].Trim();
            if (trim.StartsWith("[")) break; // 下一个 section
            if (trim.StartsWith(key + " =", StringComparison.OrdinalIgnoreCase) ||
                trim.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
            {
                var eqIdx = trim.IndexOf('=');
                return eqIdx >= 0 ? trim.Substring(eqIdx + 1).Trim() : "";
            }
        }
        return "";
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

    /// <summary>
    /// 将新的宿主路径挂载到容器，并重建容器（保留 smb.conf 和所有共享配置）
    /// </summary>
    /// <param name="newHostPath">要新增挂载的宿主路径</param>
    public (bool Success, string Message) RebuildContainerWithNewMount(string newHostPath)
    {
        try
        {
            // 1. 获取当前容器的所有挂载
            var currentMounts = GetContainerMounts();

            // 2. 检查是否已挂载
            if (currentMounts.Any(m => m.Value.Equals(newHostPath, StringComparison.OrdinalIgnoreCase)))
            {
                return (true, "路径已挂载，无需重建");
            }

            // 3. 停止并删除容器（smb.conf 是独立文件挂载，不会丢失）
            _logger.LogInformation("停止 Docker Samba 容器准备重建...");
            RunShell("docker", $"stop {_containerName}");
            RunShell("docker", $"rm {_containerName}");

            // 4. 构建新的 docker run 命令
            //    从现有挂载中提取：类型为 bind 的需要重新挂载（排除 smb.conf 自身）
            var bindMounts = new List<string>();
            foreach (var m in currentMounts)
            {
                // 跳过非 bind 类型的挂载（如匿名卷），只处理宿主目录挂载
                // smb.conf 单独处理，后面追加
                if (!m.Value.StartsWith("/") || m.Value == _configPath) continue;
                // 跳过不存在的旧挂载（如已拔出的磁盘）
                if (!Directory.Exists(m.Value)) continue;
                // 转换为 Docker run 的 -v 参数
                var containerPath = m.Key;
                bindMounts.Add($"-v {m.Value}:{containerPath}");
            }

            // 5. 添加新的挂载：宿主路径 → 容器内同名路径（如 /Volumes/Seagate8T → /share/Seagate8T）
            var volumeName = newHostPath.Split('/').Last();
            if (string.IsNullOrEmpty(volumeName)) volumeName = "data";
            var newContainerPath = $"/share/{volumeName}";
            bindMounts.Add($"-v {newHostPath}:{newContainerPath}");

            // 6. 重新创建容器
            var bindArgs = string.Join(" ", bindMounts);
            var newContainerId = RunShell("docker",
                $"run -d --name {_containerName} --restart unless-stopped -p 1445:445 " +
                $"-v /tmp/samba-config/smb.conf:/etc/samba/smb.conf:ro {bindArgs} " +
                $"-e SAMBA_SERVER_STRING=\"Docker Samba\" " +
                $"-e SAMBA_WORKGROUP=WORKGROUP crazymax/samba:latest");

            if (newContainerId.ExitCode != 0)
            {
                return (false, $"重建容器失败: {newContainerId.Output}");
            }

            _logger.LogInformation("Docker Samba 容器已重建，新挂载: {host} → {container}", newHostPath, newContainerPath);
            return (true, $"容器已重建，新磁盘已挂载: {newHostPath} → {newContainerPath}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "重建 Docker Samba 容器失败");
            return (false, $"重建容器异常: {ex.Message}");
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

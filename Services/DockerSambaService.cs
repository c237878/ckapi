using System.Diagnostics;
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

        return result;
    }

    /// <summary>
    /// 添加或更新 Docker Samba 共享
    /// </summary>
    public (bool Success, string Message) UpsertShare(string name, string path, bool guestAccess = true, bool readOnly = false)
    {
        if (!Directory.Exists(path))
        {
            return (false, $"目录不存在: {path}");
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
            $"   path = {path}",
            "   browsable = yes",
            $"   read only = {(readOnly ? "yes" : "no")}",
            $"   guest ok = {(guestAccess ? "yes" : "no")}",
            "   create mask = 0644",
            "   directory mask = 0755"
        };

        if (sections.TryGetValue(sectionName, out var existing))
        {
            // 替换已有 section
            var start = existing.Start;
            var end = existing.End;
            lines.RemoveRange(start, end - start + 1);
            lines.InsertRange(start, sectionLines);
        }
        else
        {
            // 在文件末尾添加
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1])) lines.Add("");
            lines.AddRange(sectionLines);
        }

        File.WriteAllLines(_configPath, lines);
        _logger.LogInformation("Docker Samba 配置已写入: {name} -> {path}", sectionName, path);

        return RestartContainer();
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

        // 清理末尾空行
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

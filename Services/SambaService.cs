using Microsoft.Extensions.Options;
using ckapi.Models;

namespace ckapi.Services
{
    /// <summary>
    /// Samba 路径解析服务
    /// 根据配置组合出视频/封面的真实访问路径
    /// </summary>
    public class SambaService
    {
        private readonly SambaConfig _config;

        public SambaService(IOptions<SambaConfig> config)
        {
            _config = config.Value;
        }

        /// <summary>
        /// Samba 配置是否启用
        /// </summary>
        public bool IsEnabled => _config.Enabled;

        /// <summary>
        /// 获取视频文件的本地真实路径（用于文件流传输）
        /// 当 MountPath 配置时，直接返回本地挂载路径
        /// </summary>
        public string? GetVideoLocalPath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                return null;

            // 如果已挂载 Samba，直接拼接本地路径
            if (!string.IsNullOrWhiteSpace(_config.MountPath))
            {
                // relativePath 如 "/libsF/视频库/xxx.mp4"，去掉开头的 "/"
                var cleanPath = relativePath.TrimStart('/');
                return Path.Combine(_config.MountPath, cleanPath);
            }

            return null;
        }

        /// <summary>
        /// 获取封面文件的本地真实路径
        /// </summary>
        public string? GetCoverLocalPath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                return null;

            if (!string.IsNullOrWhiteSpace(_config.MountPath))
            {
                var cleanPath = relativePath.TrimStart('/');
                return Path.Combine(_config.MountPath, cleanPath);
            }

            return null;
        }

        /// <summary>
        /// 将数据库中的相对路径转换为 Samba UNC 路径（用于调试/日志）
        /// </summary>
        public string GetSambaUncPath(string relativePath)
        {
            // relativePath: "/libsF/视频库/xxx.mp4"
            // → "\\192.168.110.6\lib\视频库\xxx.mp4"
            var cleanPath = relativePath.TrimStart('/').Replace('/', '\\');
            return $"\\\\{_config.Server}\\{_config.ShareName}\\{cleanPath}";
        }

        /// <summary>
        /// 解析数据库中的 URL，提取相对路径
        /// 支持两种格式：
        /// 1. HTTP URL: "http://192.168.110.6/libsF/视频库/xxx.mp4"
        /// 2. 相对路径: "/libsF/视频库/xxx.mp4"
        /// </summary>
        public static string ExtractRelativePath(string urlOrPath)
        {
            if (string.IsNullOrWhiteSpace(urlOrPath))
                return "";

            // 已经是相对路径
            if (urlOrPath.StartsWith("/"))
                return urlOrPath;

            // HTTP URL: "http://192.168.110.6/libsF/视频库/xxx.mp4"
            // → 提取 "/libsF/视频库/xxx.mp4"
            var uri = new Uri(urlOrPath);
            return uri.AbsolutePath;
        }

        /// <summary>
        /// 构建视频流式访问 URL（供前端播放用）
        /// 返回后端代理地址："/api/video/stream/{id}"
        /// </summary>
        public static string BuildStreamUrl(string videoId)
        {
            return $"/api/video/stream/{videoId}";
        }
    }
}

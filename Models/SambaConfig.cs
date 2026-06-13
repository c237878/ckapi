namespace ckapi.Models
{
    /// <summary>
    /// Samba 配置项
    /// </summary>
    public class SambaConfig
    {
        /// <summary>
        /// Samba 服务器地址（IP 或主机名）
        /// </summary>
        public string Server { get; set; } = "";

        /// <summary>
        /// Samba 用户名（匿名访问时留空）
        /// </summary>
        public string Username { get; set; } = "";

        /// <summary>
        /// Samba 密码（匿名访问时留空）
        /// </summary>
        public string Password { get; set; } = "";

        /// <summary>
        /// Samba 共享名称（如 "lib", "share"）
        /// </summary>
        public string ShareName { get; set; } = "";

        /// <summary>
        /// Samba 共享根路径（URL 中的根前缀，如 "/" 或 "/libsF"）
        /// 用于解析数据库中存储的相对路径
        /// </summary>
        public string BasePath { get; set; } = "/";

        /// <summary>
        /// 本地挂载路径（使用 mount -t smbfs 挂载后的本地目录）
        /// 如 "/mnt/smb"，留空则使用 SMB 协议直连
        /// </summary>
        public string MountPath { get; set; } = "";

        /// <summary>
        /// 视频文件在共享中的子目录名
        /// </summary>
        public string VideoRoot { get; set; } = "视频库";

        /// <summary>
        /// 封面文件在共享中的子目录名
        /// </summary>
        public string CoverRoot { get; set; } = "封面图";

        /// <summary>
        /// 是否启用 Samba 模式（false 时仍使用 HTTP URL）
        /// </summary>
        public bool Enabled { get; set; } = false;
    }
}

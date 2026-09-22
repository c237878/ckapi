using Microsoft.Data.Sqlite;
using System.Data;

namespace ckapi.Utils;

/// <summary>
/// SQLite数据库帮助类
/// </summary>
public class SQLiteHelper
{
    private readonly string _connectionString;
    private readonly string _dbPath;
    private readonly string? _backupPath;
    private readonly ILogger<SQLiteHelper> _logger;

    public SQLiteHelper(IConfiguration configuration, ILogger<SQLiteHelper> logger)
    {
        _logger = logger;
        var connStr = configuration.GetConnectionString("DefaultConnection") ?? "Data Source=ckweb.db";
        _backupPath = NullIfEmpty(configuration["ConnectionStrings:BackupPath"]);

        // 从连接串解析数据库文件，避免对 "Data Source=x;Mode=..." 这类多参串做字符串替换
        var builder = new SqliteConnectionStringBuilder(connStr);
        _dbPath = string.IsNullOrWhiteSpace(builder.DataSource) ? "ckweb.db" : builder.DataSource;
        _connectionString = $"Data Source={_dbPath}";

        var dir = Path.GetDirectoryName(Path.GetFullPath(_dbPath));
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _logger.LogInformation("SQLite数据库: {DbPath}", _dbPath);
    }

    public string GetDbPath() => _dbPath;

    /// <summary>备份目录，未配置时返回 null。</summary>
    public string? GetBackupPath() => _backupPath;

    public SqliteConnection GetConnection()
    {
        return new SqliteConnection(_connectionString);
    }

    public int ExecuteNonQuery(string sql, params SqliteParameter[] parameters)
    {
        using var connection = GetConnection();
        connection.Open();
        using var command = new SqliteCommand(sql, connection);
        if (parameters is { Length: > 0 })
        {
            command.Parameters.AddRange(parameters);
        }
        return command.ExecuteNonQuery();
    }

    public DataTable ExecuteDataTable(string sql, params SqliteParameter[] parameters)
    {
        using var connection = GetConnection();
        connection.Open();
        using var command = new SqliteCommand(sql, connection);
        if (parameters is { Length: > 0 })
        {
            command.Parameters.AddRange(parameters);
        }
        using var reader = command.ExecuteReader();
        var dt = new DataTable();
        dt.Load(reader);
        return dt;
    }

    public object? ExecuteScalar(string sql, params SqliteParameter[] parameters)
    {
        using var connection = GetConnection();
        connection.Open();
        using var command = new SqliteCommand(sql, connection);
        if (parameters is { Length: > 0 })
        {
            command.Parameters.AddRange(parameters);
        }
        return command.ExecuteScalar();
    }

    public bool TableExists(string tableName)
    {
        const string sql = "SELECT name FROM sqlite_master WHERE type='table' AND name=@tableName";
        return ExecuteScalar(sql, new SqliteParameter("@tableName", tableName)) != null;
    }

    public void CreateTable(string tableName, string fieldStr)
    {
        ExecuteNonQuery($"CREATE TABLE IF NOT EXISTS [{tableName}] ({fieldStr})");
    }

    /// <summary>
    /// 用 SQLite 原生在线备份接口把当前库快照到备份目录，每天一份（同名文件已存在则跳过）。
    /// 原先每次写操作都 File.Copy 整库，批量写入会退化成 N 次全库拷贝，且失败被静默吞掉。
    /// </summary>
    /// <param name="reason">写入日志的触发原因</param>
    /// <param name="force">忽略"当天已备份"直接覆盖</param>
    /// <param name="tag">文件名后缀，用于结构迁移前的即时快照 —— 它不能被当天的常规快照顶掉</param>
    public bool BackupDatabase(string reason, bool force = false, string? tag = null)
    {
        if (string.IsNullOrEmpty(_backupPath))
            return false;

        if (!Directory.Exists(_backupPath))
        {
            _logger.LogWarning("备份目录不存在，跳过备份（{BackupPath}）", _backupPath);
            return false;
        }

        var fileName = $"{Path.GetFileNameWithoutExtension(_dbPath)}_{DateTime.Now:yyyy-MM-dd}";
        if (!string.IsNullOrEmpty(tag)) fileName += $"_{tag}";
        var backupFile = Path.Combine(_backupPath, fileName + Path.GetExtension(_dbPath));

        if (!force && File.Exists(backupFile))
            return false;

        try
        {
            using var source = GetConnection();
            source.Open();
            using var target = new SqliteConnection($"Data Source={backupFile}");
            target.Open();
            source.BackupDatabase(target);
            _logger.LogInformation("已备份数据库到 {BackupFile}（原因: {Reason}）", backupFile, reason);
            return true;
        }
        catch (Exception ex)
        {
            // 备份失败不阻断启动，但必须留下可见痕迹
            _logger.LogError(ex, "数据库备份失败: {BackupFile}", backupFile);
            return false;
        }
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

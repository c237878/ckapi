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
        _dbPath = connStr.Replace("Data Source=", "").Trim();
        _backupPath = configuration["ConnectionStrings:BackupPath"];

        // 确保数据库所在目录存在
        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        // Microsoft.Data.Sqlite 连接时会自动创建空数据库文件（带正确 header）
        // 不要手动 File.WriteAllBytes 创建 0 字节空文件！

        _connectionString = connStr;
        _logger.LogInformation("SQLite数据库连接字符串: {ConnectionString}", connStr);
    }

    /// <summary>
    /// 获取数据库路径
    /// </summary>
    public string GetDbPath() => _dbPath;

    /// <summary>
    /// 创建数据库连接
    /// </summary>
    public SqliteConnection GetConnection()
    {
        return new SqliteConnection(_connectionString);
    }

    /// <summary>
    /// 执行非查询SQL语句
    /// </summary>
    public int ExecuteNonQuery(string sql, params SqliteParameter[] parameters)
    {
        using var connection = GetConnection();
        connection.Open();
        using var command = new SqliteCommand(sql, connection);
        if (parameters != null && parameters.Length > 0)
        {
            command.Parameters.AddRange(parameters);
        }
        int rows = command.ExecuteNonQuery();

        // 数据修改后自动备份
        if (rows > 0 && !string.IsNullOrEmpty(_backupPath))
        {
            try
            {
                // 目录不存在时跳过备份（不创建目录）
                if (!Directory.Exists(_backupPath))
                    return rows;

                // 按当天日期生成备份文件名：log_yyyy-MM-dd.db
                var dbFileName = Path.GetFileName(_dbPath);
                var nameWithoutExt = Path.GetFileNameWithoutExtension(dbFileName);
                var ext = Path.GetExtension(dbFileName);
                var today = DateTime.Now.ToString("yyyy-MM-dd");
                var backupFileName = $"{nameWithoutExt}_{today}{ext}";
                var backupFilePath = Path.Combine(_backupPath, backupFileName);

                File.Copy(_dbPath, backupFilePath, true);
            }
            catch { /* 备份失败不影响主操作 */ }
        }

        return rows;
    }

    /// <summary>
    /// 执行查询并返回DataTable
    /// </summary>
    public DataTable ExecuteDataTable(string sql, params SqliteParameter[] parameters)
    {
        using var connection = GetConnection();
        connection.Open();
        using var command = new SqliteCommand(sql, connection);
        if (parameters != null && parameters.Length > 0)
        {
            command.Parameters.AddRange(parameters);
        }
        using var reader = command.ExecuteReader();
        var dt = new DataTable();
        dt.Load(reader);
        return dt;
    }

    /// <summary>
    /// 执行查询并返回第一行第一列
    /// </summary>
    public object? ExecuteScalar(string sql, params SqliteParameter[] parameters)
    {
        using var connection = GetConnection();
        connection.Open();
        using var command = new SqliteCommand(sql, connection);
        if (parameters != null && parameters.Length > 0)
        {
            command.Parameters.AddRange(parameters);
        }
        return command.ExecuteScalar();
    }

    /// <summary>
    /// 检查表是否存在
    /// </summary>
    public bool TableExists(string tableName)
    {
        const string sql = "SELECT name FROM sqlite_master WHERE type='table' AND name=@tableName";
        var result = ExecuteScalar(sql, new SqliteParameter("@tableName", tableName));
        return result != null;
    }

    /// <summary>
    /// 创建表
    /// </summary>
    public void CreateTable(string tableName, string fieldStr)
    {
        var sql = $"CREATE TABLE {tableName} ({fieldStr})";
        ExecuteNonQuery(sql);
        _logger.LogInformation("表 {TableName} 创建成功", tableName);
    }
}

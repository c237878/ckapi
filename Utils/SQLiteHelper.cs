using Microsoft.Data.Sqlite;
using System.Data;

namespace ckapi.Utils;

/// <summary>
/// SQLite数据库帮助类
/// </summary>
public class SQLiteHelper
{
    private readonly string _connectionString;
    private readonly ILogger<SQLiteHelper> _logger;

    public SQLiteHelper(IConfiguration configuration, ILogger<SQLiteHelper> logger)
    {
        _logger = logger;
        var dbPath = configuration.GetConnectionString("DefaultConnection")
            ?? "Data Source=ckweb.db";
        _connectionString = dbPath;
        _logger.LogInformation("SQLite数据库连接字符串: {ConnectionString}", dbPath);
    }

    /// <summary>
    /// 获取数据库路径
    /// </summary>
    public string GetDbPath()
    {
        return _connectionString.Replace("Data Source=", "");
    }

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
        return command.ExecuteNonQuery();
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

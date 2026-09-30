using Microsoft.Data.Sqlite;

namespace ckapi.Utils;

/// <summary>
/// system_settings 这张通用键值表的读写。
///
/// 为什么单拎一个出来：这张表是"界面可改的运行配置"的落点（口令、体检节奏、快照目录、词表清单……），
/// 每个用到它的地方都各写一遍"先删再插"太容易写歪 —— name 上**没有唯一约束**，
/// 想当然地写 `ON CONFLICT DO UPDATE` 会直接报错（已经被咬过一次）。
/// </summary>
public static class SettingStore
{
    public static string? Read(SqliteConnection conn, string name)
    {
        using var cmd = new SqliteCommand("SELECT content FROM system_settings WHERE name = @n", conn);
        cmd.Parameters.AddWithValue("@n", name);
        return cmd.ExecuteScalar()?.ToString();
    }

    /// <summary>覆盖式写入：一个事务里先删同名的旧行再插新行。</summary>
    public static void Upsert(SqliteConnection conn, string name, string content)
    {
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        using var tx = conn.BeginTransaction();
        using (var del = new SqliteCommand("DELETE FROM system_settings WHERE name = @n", conn, tx))
        {
            del.Parameters.AddWithValue("@n", name);
            del.ExecuteNonQuery();
        }
        using var ins = new SqliteCommand(
            "INSERT INTO system_settings (id, name, content, ctime, utime) VALUES (@id, @n, @c, @t, @t)", conn, tx);
        ins.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("N"));
        ins.Parameters.AddWithValue("@n", name);
        ins.Parameters.AddWithValue("@c", content);
        ins.Parameters.AddWithValue("@t", now);
        ins.ExecuteNonQuery();
        tx.Commit();
    }

    /// <summary>清掉一个键（回到"没在界面上设过"，也就是回落到配置文件的默认值）</summary>
    public static void Remove(SqliteConnection conn, string name)
    {
        using var cmd = new SqliteCommand("DELETE FROM system_settings WHERE name = @n", conn);
        cmd.Parameters.AddWithValue("@n", name);
        cmd.ExecuteNonQuery();
    }
}

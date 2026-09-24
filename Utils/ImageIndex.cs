using Microsoft.Data.Sqlite;

namespace ckapi.Utils;

/// <summary>
/// 目录 → 图片索引表 的一次对齐。演员图片（actor_images）与艳图（highlight_images）共用这一份口径：
/// 只 stat 不打开文件；同名文件被换过（mtime 变了）就刷新 size 并清掉宽高，等下次出缩略图时回填；
/// 磁盘上没了的行跟着删。
///
/// 之所以抽出来：两处各自实现时最容易漂的是"什么算变化"——
/// 一边按 mtime 刷新、另一边只增不删，就会出现演员图片跟手、艳图永远删不掉的怪状态。
/// </summary>
public static class ImageIndex
{
    public readonly record struct Result(int Added, int Updated, int Removed, int Total);

    /// <summary>
    /// 艳图目录（system_settings.posterDir）。演员图片放在 &lt;该目录&gt;/&lt;演员ID&gt;/，
    /// 艳图池放在 &lt;该目录&gt;/default/。
    /// </summary>
    public static string? PosterDir(SqliteConnection conn)
    {
        using var cmd = new SqliteCommand("SELECT content FROM system_settings WHERE name = 'posterDir'", conn);
        return cmd.ExecuteScalar()?.ToString();
    }

    /// <summary>
    /// 同步 dir 到 table。ownerColumn 传 null 表示这张表没有归属列（艳图整池就一份），
    /// 否则按 ownerColumn = ownerValue 圈定本次同步的范围。
    /// table / ownerColumn 只由代码传字面量，不接受任何请求参数。
    /// </summary>
    public static Result Sync(
        SqliteConnection conn, string table, string? ownerColumn, string? ownerValue, string dir)
    {
        var onDisk = new HashSet<string>(StringComparer.Ordinal);
        if (Directory.Exists(dir))
        {
            foreach (var path in Directory.GetFiles(dir))
            {
                var name = Path.GetFileName(path);
                // AsFileName 顺手挡掉 .DS_Store 这类点开头的隐藏文件
                if (SafePath.AsFileName(name) is not null && SafePath.IsImageFile(name))
                    onDisk.Add(name);
            }
        }

        var ownerWhere = ownerColumn is null ? "" : $" WHERE {ownerColumn} = @owner";
        var known = new Dictionary<string, string?>(StringComparer.Ordinal);
        using (var read = new SqliteCommand($"SELECT file_name, mtime FROM [{table}]{ownerWhere}", conn))
        {
            if (ownerColumn is not null)
                read.Parameters.Add(new SqliteParameter("@owner", ownerValue));
            using var reader = read.ExecuteReader();
            while (reader.Read())
                known[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        var added = 0;
        var updated = 0;
        var removed = 0;
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        using var tx = conn.BeginTransaction();

        foreach (var file in onDisk.OrderBy(x => x, StringComparer.Ordinal))
        {
            var info = new FileInfo(Path.Combine(dir, file));
            if (!info.Exists) continue;   // 列完目录到 stat 之间被删掉的小概率窗口，跳过即可

            var mtime = info.LastWriteTimeUtc.ToString("yyyy-MM-dd HH:mm:ss");
            if (!known.TryGetValue(file, out var old))
            {
                added++;
                var columns = ownerColumn is null
                    ? "(file_name, size, mtime, ctime)"
                    : $"({ownerColumn}, file_name, size, mtime, ctime)";
                var values = ownerColumn is null
                    ? "(@file, @size, @mtime, @now)"
                    : "(@owner, @file, @size, @mtime, @now)";
                using var ins = new SqliteCommand(
                    $"INSERT OR IGNORE INTO [{table}] {columns} VALUES {values}", conn, tx);
                AddOwner(ins, ownerColumn, ownerValue);
                Add(ins, "@file", file);
                Add(ins, "@size", info.Length);
                Add(ins, "@mtime", mtime);
                Add(ins, "@now", now);
                ins.ExecuteNonQuery();
            }
            else if (old != mtime)
            {
                updated++;
                using var upd = new SqliteCommand(
                    $"UPDATE [{table}] SET size = @size, mtime = @mtime, width = NULL, height = NULL" +
                    $" WHERE file_name = @file{(ownerColumn is null ? "" : $" AND {ownerColumn} = @owner")}", conn, tx);
                Add(upd, "@file", file);
                Add(upd, "@size", info.Length);
                Add(upd, "@mtime", mtime);
                AddOwner(upd, ownerColumn, ownerValue);
                upd.ExecuteNonQuery();
            }
        }

        foreach (var file in known.Keys.Where(k => !onDisk.Contains(k)).OrderBy(x => x, StringComparer.Ordinal).ToList())
        {
            removed++;
            using var del = new SqliteCommand(
                $"DELETE FROM [{table}] WHERE file_name = @file{(ownerColumn is null ? "" : $" AND {ownerColumn} = @owner")}",
                conn, tx);
            Add(del, "@file", file);
            AddOwner(del, ownerColumn, ownerValue);
            del.ExecuteNonQuery();
        }

        tx.Commit();

        using var total = new SqliteCommand($"SELECT COUNT(*) FROM [{table}]{ownerWhere}", conn);
        if (ownerColumn is not null)
            total.Parameters.Add(new SqliteParameter("@owner", ownerValue));
        return new Result(added, updated, removed, Convert.ToInt32(total.ExecuteScalar()));
    }

    private static void AddOwner(SqliteCommand cmd, string? ownerColumn, string? ownerValue)
    {
        if (ownerColumn is not null) cmd.Parameters.Add(new SqliteParameter("@owner", ownerValue));
    }

    private static void Add(SqliteCommand cmd, string name, object value)
        => cmd.Parameters.Add(new SqliteParameter(name, value));
}

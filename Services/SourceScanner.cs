using ckapi.Utils;
using Microsoft.Data.Sqlite;

namespace ckapi.Services;

/// <summary>
/// 片源扫描：**只量分辨率**。读视频文件的容器头拿显示宽高，落到 res_w / res_h / scan_time。
///
/// 字幕与广告水印两维不由扫描参与，全部人在界面上给结论。
/// 之前扫描会顺带把"容器里有字幕轨 / 字幕目录里有同番号文件"填成 has，撤掉了：
/// 容器里有字幕轨不等于那份字幕能用，反过来没有也不等于"这片子本来就没字幕"——
/// 这一维真正在问的是"我要不要去补字幕"，只有他自己知道。
///
/// 探测与写库分成两步（Inspect / Write），因为文件在 SMB 共享上、一个来回几百毫秒，
/// 而 SQLite 连接不能多个线程共用：并发只放在探测那一步，写库回到单线程一个事务里连着做。
///
/// 扫描不会把 watched 置 1：那是"他看过并给过结论"的暗号，今日推荐靠它挑片。
/// 自动填字段就把这个暗号一起改了，全站推荐会当场变成空片池。
/// </summary>
public sealed class SourceScanner
{
    /// <summary>一行待扫描的影片</summary>
    public sealed record Row(string Id, string FilePath, long FileSize);

    /// <param name="Ok">探到了宽高</param>
    /// <param name="Width">显示宽</param>
    /// <param name="Height">显示高</param>
    /// <param name="Codec">视频轨 fourcc，只用于日志</param>
    /// <param name="Error">探不出来的原因</param>
    public sealed record Inspection(
        bool Ok, int Width, int Height, string? Codec, string? Error);

    /// <summary>
    /// 候选清单：有实体文件的都算，默认跳过已经量过的。
    /// 按 id 排是为了中途停掉再起跑时进度看起来"接着往下走"，而不是重新洗牌。
    /// </summary>
    public List<Row> Pending(SqliteConnection conn, bool force)
    {
        var sql = $@"
            SELECT id, file_path, IFNULL(file_size, 0) AS fs
            FROM videos
            WHERE IFNULL(file_size, 0) > 0
              AND file_path IS NOT NULL AND file_path <> '' AND file_path NOT LIKE 'manual://%'
              {(force ? "" : "AND (res_w IS NULL OR res_h IS NULL OR res_h <= 0)")}
            ORDER BY id";

        var rows = new List<Row>();
        using var cmd = new SqliteCommand(sql, conn);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new Row(reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
        }
        return rows;
    }

    /// <summary>纯探测，不碰数据库，可以并发</summary>
    public Inspection Inspect(Row row)
    {
        var info = MediaProbe.Probe(row.FilePath);
        if (info is null)
            return new Inspection(false, 0, 0, null, "文件不在，或读不出元数据（可能没下完／不是 MP4）");

        var (w, h, codec, _) = info.Value;
        if (w <= 0 || h <= 0)
            return new Inspection(false, 0, 0, codec, "容器头里没有画面宽高");

        return new Inspection(true, w, h, codec, null);
    }

    /// <summary>把探测结果写进去。tx 由批量任务给，一整批一个事务。</summary>
    public void Write(SqliteConnection conn, SqliteTransaction? tx, Row row, Inspection ins)
    {
        const string sql = @"
            UPDATE videos SET res_w = @w, res_h = @h, scan_time = @t WHERE id = @id";

        using var cmd = new SqliteCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("@w", ins.Width);
        cmd.Parameters.AddWithValue("@h", ins.Height);
        cmd.Parameters.AddWithValue("@t", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.Parameters.AddWithValue("@id", row.Id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>单条重扫：给详情页的「扫描」按钮用，不走后台任务</summary>
    public Inspection ScanOne(SqliteConnection conn, string id)
    {
        var row = Load(conn, id);
        if (row is null) return new Inspection(false, 0, 0, null, "影片不存在");
        if (row.FileSize <= 0 || string.IsNullOrEmpty(row.FilePath))
            return new Inspection(false, 0, 0, null, "这条影片没有登记文件");

        var ins = Inspect(row);
        if (ins.Ok) Write(conn, null, row, ins);
        return ins;
    }

    /// <summary>按 id 取一行；单条重扫不必把全表读一遍</summary>
    private Row? Load(SqliteConnection conn, string id)
    {
        const string sql = "SELECT id, file_path, IFNULL(file_size, 0) FROM videos WHERE id = @id";
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read()
            ? new Row(reader.GetString(0), reader.GetString(1), reader.GetInt64(2))
            : null;
    }
}

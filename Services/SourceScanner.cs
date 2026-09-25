using ckapi.Utils;
using Microsoft.Data.Sqlite;

namespace ckapi.Services;

/// <summary>
/// 片源扫描：读视频文件的容器头，把分辨率与"有没有字幕"落到 videos 表上。
///
/// 探测与写库分成两步（Inspect / Write），因为文件在 SMB 共享上、一个来回几百毫秒，
/// 而 SQLite 连接不能多个线程共用：并发只放在探测那一步，写库回到单线程一个事务里连着做。
///
/// 落库的规矩：
///   · res_w / res_h / scan_time 是客观量出来的，任何时候都覆盖（换了片源就该跟着变）。
///   · subtitle_state 只在 unknown（没标过）与 missing（他标过"缺字幕"，之后补了字幕文件）这两种状态下
///     被填成 has。人工判过 none（发行版本就没字幕）与 has 的一律不动——
///     机器只能证明"这里有字幕"，证明不了"这片子本来就没有"。
///
/// 扫描不会把 watched 置 1：那是"他看过并给过结论"的暗号，今日推荐靠它挑片。
/// 自动填字段时把这个暗号一起改了，全站推荐会当场变成空片池。
/// </summary>
public sealed class SourceScanner
{
    private readonly ILogger<SourceScanner> _logger;

    public SourceScanner(ILogger<SourceScanner> logger) => _logger = logger;

    /// <summary>一行待扫描的影片。SubtitleState 带着走，写库时不用再查一次。</summary>
    public sealed record Row(string Id, string? Code, string FilePath, long FileSize, string SubtitleState);

    /// <param name="Ok">探到了宽高</param>
    /// <param name="Width">显示宽</param>
    /// <param name="Height">显示高</param>
    /// <param name="Subtitle">容器里有字幕轨，或字幕目录里有同番号的文件</param>
    /// <param name="SetSubtitle">这条字幕证据是否该写进 subtitle_state（人工判过的不覆盖）</param>
    /// <param name="Codec">视频轨 fourcc，只用于日志</param>
    /// <param name="Error">探不出来的原因</param>
    public sealed record Inspection(
        bool Ok, int Width, int Height, bool Subtitle, bool SetSubtitle, string? Codec, string? Error);

    /// <summary>
    /// 候选清单：有实体文件的都算，默认跳过已经量过的。
    /// 按 id 排是为了中途停掉再起跑时进度看起来"接着往下走"，而不是重新洗牌。
    /// </summary>
    public List<Row> Pending(SqliteConnection conn, bool force)
    {
        var sql = $@"
            SELECT id, code, file_path, IFNULL(file_size, 0) AS fs, subtitle_state
            FROM videos
            WHERE IFNULL(file_size, 0) > 0
              AND file_path IS NOT NULL AND file_path <> '' AND file_path NOT LIKE 'manual://%'
              {(force ? "" : "AND (res_w IS NULL OR res_h IS NULL OR res_h <= 0)")}
            ORDER BY id";

        var rows = new List<Row>();
        using var cmd = new SqliteCommand(sql, conn);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) rows.Add(ReadRow(reader));
        return rows;
    }

    /// <summary>
    /// 字幕目录里已有的番号（不含扩展名）。整目录一次列完，
    /// 逐条查会变成四千多次文件系统往返。目录没配或读不到时返回空集，扫描照常只量分辨率。
    /// </summary>
    public HashSet<string> SidecarCodes(SqliteConnection conn)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? dir = null;
        try
        {
            dir = Scalar(conn, "SELECT path FROM scan_directories WHERE category = '字幕' LIMIT 1") as string;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return result;

            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var stem = Path.GetFileNameWithoutExtension(file);
                if (stem.Length > 0) result.Add(stem);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "字幕目录 {Dir} 列不出来，本次扫描只量分辨率", dir);
            result.Clear();
        }
        return result;
    }

    /// <summary>纯探测，不碰数据库，可以并发</summary>
    public Inspection Inspect(Row row, HashSet<string> sidecars)
    {
        var info = MediaProbe.Probe(row.FilePath);
        if (info is null)
            return new Inspection(false, 0, 0, false, false, null, "文件不在，或读不出元数据（可能没下完／不是 MP4）");

        var (w, h, codec, embedded, _) = info.Value;
        if (w <= 0 || h <= 0)
            return new Inspection(false, 0, 0, false, false, codec, "容器头里没有画面宽高");

        var hasSub = embedded || (row.Code is not null && sidecars.Contains(row.Code));
        var setSub = hasSub && row.SubtitleState is "unknown" or "missing";
        return new Inspection(true, w, h, hasSub, setSub, codec, null);
    }

    /// <summary>把探测结果写进去。tx 由批量任务给，一整批一个事务。</summary>
    public void Write(SqliteConnection conn, SqliteTransaction? tx, Row row, Inspection ins)
    {
        const string sql = @"
            UPDATE videos
            SET res_w = @w,
                res_h = @h,
                scan_time = @t,
                subtitle_state = CASE WHEN @setSub = 1 THEN 'has' ELSE subtitle_state END
            WHERE id = @id";

        using var cmd = new SqliteCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("@w", ins.Width);
        cmd.Parameters.AddWithValue("@h", ins.Height);
        cmd.Parameters.AddWithValue("@t", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.Parameters.AddWithValue("@setSub", ins.SetSubtitle ? 1 : 0);
        cmd.Parameters.AddWithValue("@id", row.Id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>单条重扫：给详情页的「扫描」按钮用，不走后台任务</summary>
    public Inspection ScanOne(SqliteConnection conn, string id)
    {
        var row = Load(conn, id);
        if (row is null) return new Inspection(false, 0, 0, false, false, null, "影片不存在");
        if (row.FileSize <= 0 || string.IsNullOrEmpty(row.FilePath))
            return new Inspection(false, 0, 0, false, false, null, "这条影片没有登记文件");

        var ins = Inspect(row, SidecarCodes(conn));
        if (ins.Ok) Write(conn, null, row, ins);
        return ins;
    }

    /// <summary>按 id 取一行；单条重扫不必把全表读一遍</summary>
    private Row? Load(SqliteConnection conn, string id)
    {
        const string sql = @"
            SELECT id, code, file_path, IFNULL(file_size, 0) AS fs, subtitle_state
            FROM videos WHERE id = @id";
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRow(reader) : null;
    }

    private static Row ReadRow(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.IsDBNull(1) ? null : reader.GetString(1),
        reader.GetString(2),
        reader.GetInt64(3),
        reader.IsDBNull(4) ? "unknown" : reader.GetString(4));

    private static object? Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = new SqliteCommand(sql, conn);
        return cmd.ExecuteScalar();
    }
}

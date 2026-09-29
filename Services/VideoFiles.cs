using Microsoft.Data.Sqlite;

namespace ckapi.Services;

/// <summary>
/// 文件层（video_files）的共用读写：版本清单、默认版本切换、番号级联改名。
///
/// 一部片 → 一到多个版本行，每部恰好一条 is_default=1（列表筛选、统计、卡片字段都走那一行）。
/// 把这些动作收在一个类里，是因为"改番号要顺带动盘上文件名"这件事在编辑与改名工具两处都要做，
/// 分头实现最容易出的问题是两套判定标准——一份按行级 code 对齐、另一份又拿影片番号去猜。
/// </summary>
public static class VideoFiles
{
    /// <summary>一个版本行的列清单；表别名固定为 f，查询用它拼 SELECT。</summary>
    public const string Columns = """
        f.id, f.video_id, f.code, f.type_id, f.label, f.file_path, IFNULL(f.file_size, 0) AS file_size,
        f.res_w, f.res_h, f.subtitle_state, f.watermark_state, f.scan_time, f.is_default, f.ctime,
        (SELECT vt.name FROM version_types vt WHERE vt.id = f.type_id) AS type_name,
        (SELECT COUNT(*) FROM video_likes l WHERE l.file_id = f.id AND l.target_type = 'video') AS like_count
        """;

    /// <summary>
    /// 一部片的全部版本，默认版本排最前，其余按行级番号排。
    /// 详情页的下拉框、设为默认的校验、级联改名的遍历都用它。
    /// </summary>
    public static List<Dictionary<string, object?>> OfMovie(SqliteConnection conn, string videoId)
    {
        var list = new List<Dictionary<string, object?>>();
        using var cmd = new SqliteCommand($@"
            SELECT {Columns} FROM video_files f
            WHERE f.video_id = @videoId
            ORDER BY f.is_default DESC, f.code", conn);
        cmd.Parameters.Add(new SqliteParameter("@videoId", videoId));
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(Map(reader));
        return list;
    }

    /// <summary>取一个版本行本身（不属于该片 / 不存在时返回 null）</summary>
    public static Dictionary<string, object?>? Find(SqliteConnection conn, string fileId)
    {
        using var cmd = new SqliteCommand($"SELECT {Columns} FROM video_files f WHERE f.id = @id", conn);
        cmd.Parameters.Add(new SqliteParameter("@id", fileId));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public static string? DefaultId(SqliteConnection conn, string videoId)
        => Scalar(conn, "SELECT id FROM video_files WHERE video_id = @v AND is_default = 1",
            new SqliteParameter("@v", videoId))?.ToString();

    /// <summary>
    /// 界面显示的版本名：**类型名优先**（湿姐 / 水果派 / 剪辑…），没有类型才用他填的版本名称，
    /// 两者都没有就是"原版"（2026-09-29 定）。
    ///
    /// 为什么类型名压过 label：解说行当初是按"新增影片"录的，label 里存的是整条解说片的长标题，
    /// 下拉框和卡片标识都放不下；那个长标题不丢，作为副标题单独显示（label 仍在返回里）。
    /// </summary>
    public static string DisplayName(string? label, string? typeName)
        => !string.IsNullOrWhiteSpace(typeName) ? typeName.Trim()
           : !string.IsNullOrWhiteSpace(label) ? label.Trim()
           : "原版";

    /// <summary>
    /// 新建影片时建它的原版行。file_path 用空串表示"还没上传"，不用任何前缀暗号。
    /// </summary>
    public static string AddOriginal(SqliteConnection conn, string videoId, string? code,
        string? filePath, long fileSize, SqliteTransaction? tx = null)
    {
        var id = NewId();
        using var cmd = new SqliteCommand(@"
            INSERT INTO video_files (id, video_id, code, type_id, label, file_path, file_size,
                                     subtitle_state, watermark_state, is_default, ctime)
            VALUES (@id, @videoId, @code, '', '', @filePath, @fileSize, 'unknown', 'unknown', 1, @ctime)", conn, tx);
        cmd.Parameters.Add(new SqliteParameter("@id", id));
        cmd.Parameters.Add(new SqliteParameter("@videoId", videoId));
        cmd.Parameters.Add(new SqliteParameter("@code", code ?? ""));
        cmd.Parameters.Add(new SqliteParameter("@filePath", filePath ?? ""));
        cmd.Parameters.Add(new SqliteParameter("@fileSize", fileSize));
        cmd.Parameters.Add(new SqliteParameter("@ctime", Now()));
        cmd.ExecuteNonQuery();
        return id;
    }

    /// <summary>
    /// 把某一版设为影片口径。同一部片只能有一条默认行，所以先清后设放在一个事务里，
    /// 中途失败不会出现"整部片没有默认版本"的空窗（列表筛选与统计都靠它）。
    /// </summary>
    public static void SetDefault(SqliteConnection conn, string fileId)
    {
        var videoId = Scalar(conn, "SELECT video_id FROM video_files WHERE id = @id",
            new SqliteParameter("@id", fileId))?.ToString();
        if (string.IsNullOrEmpty(videoId))
            throw new MetaException("版本不存在");

        using var tx = conn.BeginTransaction();
        NonQuery(conn, "UPDATE video_files SET is_default = 0 WHERE video_id = @v", new SqliteParameter("@v", videoId), tx);
        var hit = NonQuery(conn, "UPDATE video_files SET is_default = 1 WHERE id = @id", new SqliteParameter("@id", fileId), tx);
        tx.Commit();
        if (hit == 0) throw new MetaException("版本不存在");
    }

    /// <summary>
    /// 番号级联改名：影片番号变了，这一部片的所有版本行按规则跟着改文件名。
    ///
    /// 两条限定（2026-09-29 定），否则规则会咬人：
    ///
    /// 1. **只换前缀，不重算后缀。** 新 code = 新番号 + 该行现有 code 去掉旧番号的那段尾巴，
    ///    所以 ATID-428C 改成 SONE-201 会走成 SONE-201C。不按 version_types.suffix 重算是因为
    ///    同一个类型下可能有好几个频道，都按 suffix 拼会算出同一个目标名；换前缀天然保持各行之间的相对差异。
    /// 2. **只跟随本来就按规则命名的行。** 判据是该行 code 以旧番号开头；手工起过名的一律不动，
    ///    只在返回里报"N 行未跟随改名"。不然盘上手起的名字会被保存按钮悄悄改掉。
    ///
    /// 逐行"改成功才更新该行路径"，绝不先把整批文件动完再写库——部分失败不会留下半套状态。
    /// 三道闸门沿用编辑影片原有的那套：旧路径没被别的记录共用、目标名不存在、失败只记一条不中断。
    /// </summary>
    public static (int Renamed, int Skipped, List<Dictionary<string, object?>> Details) CascadeRename(
        SqliteConnection conn, string videoId, string oldCode, string newCode)
    {
        var details = new List<Dictionary<string, object?>>();
        var renamed = 0;
        var skipped = 0;
        if (string.IsNullOrWhiteSpace(oldCode) || string.IsNullOrWhiteSpace(newCode) || oldCode == newCode)
            return (0, 0, details);

        foreach (var row in OfMovie(conn, videoId))
        {
            var fileId = row["id"]?.ToString() ?? "";
            var code = row["code"] as string ?? "";
            var filePath = row["filePath"] as string ?? "";

            // 限定 2：不按规则命名的行不动
            if (!code.StartsWith(oldCode, StringComparison.Ordinal) || string.IsNullOrEmpty(filePath)
                || !System.IO.File.Exists(filePath))
            {
                skipped++;
                details.Add(new Dictionary<string, object?>
                {
                    ["fileId"] = fileId, ["code"] = code, ["renamed"] = false,
                    ["reason"] = string.IsNullOrEmpty(filePath) ? "这一版还没有文件"
                               : !System.IO.File.Exists(filePath) ? "文件不在盘上"
                               : "行级番号不跟随旧番号，按手起的名字留着"
                });
                continue;
            }

            // 限定 1：只换前缀，尾巴原样带走
            var target = oldCode.Length == code.Length
                ? newCode
                : newCode + code[oldCode.Length..];
            var dir = System.IO.Path.GetDirectoryName(filePath)!;
            var ext = System.IO.Path.GetExtension(filePath);
            var targetPath = System.IO.Path.Combine(dir, target + ext);

            // 闸门：目标名已被别的文件占着就不动，别覆盖
            if (targetPath != filePath && System.IO.File.Exists(targetPath))
            {
                skipped++;
                details.Add(new Dictionary<string, object?>
                {
                    ["fileId"] = fileId, ["code"] = code, ["renamed"] = false,
                    ["reason"] = $"目标名已被占用：{target}{ext}"
                });
                continue;
            }

            try
            {
                if (targetPath != filePath) System.IO.File.Move(filePath, targetPath);
            }
            catch (Exception ex)
            {
                skipped++;
                details.Add(new Dictionary<string, object?>
                {
                    ["fileId"] = fileId, ["code"] = code, ["renamed"] = false,
                    ["reason"] = "重命名失败：" + ex.Message
                });
                continue;
            }

            // 改成功了才写库：盘上动了但库里没动，或反过来，都比不动更难查
            NonQuery(conn, "UPDATE video_files SET code = @code, file_path = @path WHERE id = @id",
                new SqliteParameter("@code", target),
                new SqliteParameter("@path", targetPath),
                new SqliteParameter("@id", fileId));
            renamed++;
            details.Add(new Dictionary<string, object?>
            {
                ["fileId"] = fileId, ["code"] = target, ["renamed"] = true,
                ["oldFile"] = filePath, ["newFile"] = targetPath
            });
        }

        return (renamed, skipped, details);
    }

    /// <summary>
    /// 改名工具用：把某一版的文件名对齐到它的行级番号（盘上文件 + 库里的路径）。
    /// 目标名已被占用、文件不在盘上都只回原因，不抛。
    ///
    /// dryRun=true 只算"会改成什么"，一个字节都不动盘、也不写库。
    /// 这是改名类操作的默认形态：动 4800 个文件之前得先看见清单，
    /// 2026-09-29 就是在自测里直接跑了真改名，三个文件被改名而库里还指着旧名字。
    /// </summary>
    public static (bool Renamed, string NewPath, string? Error) AlignFileName(SqliteConnection conn, string fileId, bool dryRun = false)
    {
        var row = Find(conn, fileId);
        if (row is null) return (false, "", "版本不存在");
        var code = row["code"] as string ?? "";
        var filePath = row["filePath"] as string ?? "";
        if (string.IsNullOrEmpty(code)) return (false, filePath, "这一版没有行级番号，无法对齐");
        if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath))
            return (false, filePath, "文件不在盘上");

        var dir = System.IO.Path.GetDirectoryName(filePath)!;
        var ext = System.IO.Path.GetExtension(filePath);
        if (System.IO.Path.GetFileNameWithoutExtension(filePath) == code) return (false, filePath, null);

        var targetPath = System.IO.Path.Combine(dir, code + ext);
        if (targetPath != filePath && System.IO.File.Exists(targetPath))
            return (false, filePath, $"目标名已被占用：{code}{ext}");

        if (dryRun) return (true, targetPath, null);

        try
        {
            System.IO.File.Move(filePath, targetPath);
        }
        catch (Exception ex)
        {
            return (false, filePath, "重命名失败：" + ex.Message);
        }

        // 改成功了才写库：反过来先写库再动盘，中途失败就留下一个指向不存在文件的路径
        NonQuery(conn, "UPDATE video_files SET file_path = @p WHERE id = @id",
            new SqliteParameter("@p", targetPath), new SqliteParameter("@id", fileId));
        return (true, targetPath, null);
    }

    public static string NewId() => Guid.NewGuid().ToString("N").ToUpper();

    public static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

    public static Dictionary<string, object?> Map(SqliteDataReader reader)
    {
        var typeId = Str(reader, "type_id");
        var label = Str(reader, "label");
        var typeName = Str(reader, "type_name");
        return new Dictionary<string, object?>
        {
            ["id"] = reader["id"].ToString(),
            ["videoId"] = Str(reader, "video_id"),
            ["code"] = Str(reader, "code") ?? "",
            ["typeId"] = string.IsNullOrEmpty(typeId) ? null : typeId,
            ["typeName"] = typeName,
            ["label"] = label ?? "",
            // 界面直接显示这一个：版本名称 → 类型名 → "原版"，不再让前端各写一份判断
            ["displayName"] = DisplayName(label, typeName),
            ["filePath"] = Str(reader, "file_path") ?? "",
            ["fileSize"] = reader["file_size"] == DBNull.Value ? 0 : Convert.ToInt64(reader["file_size"]),
            ["resW"] = Int(reader, "res_w"),
            ["resH"] = Int(reader, "res_h"),
            ["subtitleState"] = Str(reader, "subtitle_state") ?? Utils.SourceStates.Unknown,
            ["watermarkState"] = Str(reader, "watermark_state") ?? Utils.SourceStates.Unknown,
            ["scanTime"] = Str(reader, "scan_time"),
            ["isDefault"] = Int(reader, "is_default") == 1,
            ["ctime"] = Str(reader, "ctime"),
            ["likeCount"] = Int(reader, "like_count"),
        };
    }

    /// <summary>版本类型词表（设置里维护）；含每个类型下的版本行数，好让人看出能不能删。</summary>
    public static List<Dictionary<string, object?>> Types(SqliteConnection conn)
    {
        var list = new List<Dictionary<string, object?>>();
        using var cmd = new SqliteCommand(@"
            SELECT vt.id, vt.name, vt.suffix, vt.sort,
                   (SELECT COUNT(*) FROM video_files f WHERE f.type_id = vt.id) AS used
            FROM version_types vt ORDER BY vt.sort, vt.name", conn);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new Dictionary<string, object?>
            {
                ["id"] = reader["id"].ToString(),
                ["name"] = reader["name"].ToString(),
                ["suffix"] = Str(reader, "suffix") ?? "",
                ["sort"] = Int(reader, "sort"),
                ["used"] = Int(reader, "used"),
            });
        }
        return list;
    }

    /// <summary>类型 id 认不出就明确报错，别静默当成"没有类型"（与片商同一口径）。</summary>
    public static string ResolveType(SqliteConnection conn, string? typeId)
    {
        if (string.IsNullOrWhiteSpace(typeId)) return "";
        var name = Scalar(conn, "SELECT name FROM version_types WHERE id = @id", new SqliteParameter("@id", typeId));
        if (name is null) throw new MetaException($"版本类型 {typeId} 不存在");
        return typeId;
    }

    /// <summary>调用方可以自救的错（id 认不出、格式不对），控制器原样报文案，不丢进 500。</summary>
    public class MetaException : Exception
    {
        public MetaException(string message) : base(message) { }
    }

    private static object? Scalar(SqliteConnection conn, string sql, SqliteParameter p)
    {
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.Add(p);
        return cmd.ExecuteScalar();
    }

    private static int NonQuery(SqliteConnection conn, string sql, SqliteParameter p, SqliteTransaction? tx = null)
    {
        using var cmd = new SqliteCommand(sql, conn, tx);
        cmd.Parameters.Add(p);
        return cmd.ExecuteNonQuery();
    }

    private static int NonQuery(SqliteConnection conn, string sql, SqliteParameter p1, SqliteParameter p2)
    {
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.Add(p1);
        cmd.Parameters.Add(p2);
        return cmd.ExecuteNonQuery();
    }

    private static int NonQuery(SqliteConnection conn, string sql, SqliteParameter p1, SqliteParameter p2, SqliteParameter p3)
    {
        using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.Add(p1);
        cmd.Parameters.Add(p2);
        cmd.Parameters.Add(p3);
        return cmd.ExecuteNonQuery();
    }

    private static int Int(SqliteDataReader reader, string column)
    {
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (reader.GetName(i).Equals(column, StringComparison.OrdinalIgnoreCase))
                return reader.IsDBNull(i) ? 0 : Convert.ToInt32(reader.GetValue(i));
        }
        return 0;
    }

    private static string? Str(SqliteDataReader reader, string column)
    {
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (reader.GetName(i).Equals(column, StringComparison.OrdinalIgnoreCase))
                return reader.IsDBNull(i) ? null : reader.GetValue(i)?.ToString();
        }
        return null;
    }
}

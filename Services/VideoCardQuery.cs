using Microsoft.Data.Sqlite;

namespace ckapi.Services;

/// <summary>
/// 影片卡片的列定义与 reader→JSON 映射。
///
/// 之前同一份 SELECT 列表在 7 处各写一遍，映射另有 4 套实现且字段名互不一致
/// （有的叫 addedAt 有的叫 ctime，有的缺 mediaAttrFlags，like_count 子查询有的漏了
/// target_type 过滤），前端 VideoCard 只能按最保守的交集渲染。统一到这里。
///
/// 约定：videos 表别名固定为 v，系列表别名固定为 s，**默认版本行别名固定为 df**。
/// 卡片上的文件大小/分辨率/两维状态都是"默认那一版"的属性（v11 起文件层在 video_files），
/// 所以凡是用 Columns / ColumnsWithSeries / SourceStates 谓词的查询，
/// FROM 后面都必须挂上 <see cref="FileJoin"/>。漏挂会直接报 no such column，不会静默算错。
/// </summary>
public static class VideoCardQuery
{
    /// <summary>
    /// 把默认版本行挂进查询。每部片恰好一条 is_default=1（由代码保证，v11 迁移与新建影片都会建），
    /// 所以 LEFT JOIN 的结果就是一对一；用 LEFT 而不是 INNER，是为了别让版本行出问题的片整条消失。
    /// </summary>
    public const string FileJoin = "LEFT JOIN video_files df ON df.video_id = v.id AND df.is_default = 1";

    /// <summary>
    /// 同一条 JOIN，但影片用了别的表别名（演员/片商列表里的统计子查询里影片叫 v2 之类）。
    /// 拼出来的字符串仍固定用 df 当版本行别名，所以 SourceStates 那些谓词原样可用。
    /// </summary>
    public static string FileJoinFor(string videoAlias)
        => $"LEFT JOIN video_files df ON df.video_id = {videoAlias}.id AND df.is_default = 1";

    /// <summary>不含系列名</summary>
    public const string Columns = """
        v.id, v.code, v.name, v.category, v.country, v.cover_path,
        v.seriesid, v.ctime, v.original_name, v.release_date,
        df.id AS file_id, df.file_path, df.file_size,
        df.subtitle_state, df.watermark_state, df.res_w, df.res_h,
        (SELECT COUNT(*) FROM video_files f WHERE f.video_id = v.id) AS version_count,
        v.studioid, (SELECT st.name FROM studios st WHERE st.id = v.studioid) AS studio_name,
        (SELECT COUNT(*) FROM video_likes WHERE video_id = v.id AND target_type='video') AS like_count,
        (SELECT GROUP_CONCAT(a.id || '|' || a.name, ',') FROM actors a
         JOIN video_actors va ON a.id = va.actor_id WHERE va.video_id = v.id) AS actor_names
        """;

    /// <summary>含系列名（需要 LEFT JOIN video_series s）</summary>
    public const string ColumnsWithSeries = Columns + ",\n        s.name AS series_name";

    /// <summary>
    /// 映射一行影片。缺失的可选列（like_count / actor_names / series_name / 文件层那几列）不会抛异常，
    /// 因此 `SELECT v.*` 这类部分列查询也能复用——文件层在 video_files，没挂 FileJoin 的查询就拿默认值。
    /// </summary>
    public static Dictionary<string, object?> Map(SqliteDataReader reader)
    {
        var result = new Dictionary<string, object?>
        {
            ["id"] = reader["id"].ToString(),
            ["code"] = Str(reader, "code"),
            ["name"] = reader["name"].ToString(),
            ["category"] = Str(reader, "category") ?? "",
            ["country"] = Str(reader, "country") ?? "",
            ["filePath"] = Str(reader, "file_path") ?? "",
            ["fileSize"] = HasColumn(reader, "file_size") && reader["file_size"] != DBNull.Value
                ? Convert.ToInt64(reader["file_size"]) : 0L,
            ["coverPath"] = Str(reader, "cover_path"),
            ["seriesId"] = Str(reader, "seriesid"),
            // 两维都给了默认值：前端只要拿到字符串就能直接查文案表，不必再判空
            ["subtitleState"] = Str(reader, "subtitle_state") ?? Utils.SourceStates.Unknown,
            ["watermarkState"] = Str(reader, "watermark_state") ?? Utils.SourceStates.Unknown,
            ["resW"] = Int(reader, "res_w"),
            ["resH"] = Int(reader, "res_h"),
            // 卡片上的文件字段都属于"默认那一版"（v11）：带出版本行 id 与总版本数，
            // 前端据此决定要不要显示版本切换与"共 N 版"的标识
            ["fileId"] = Str(reader, "file_id"),
            ["versionCount"] = HasColumn(reader, "version_count") ? Int(reader, "version_count") : 1,
            // 原名与发行日期只有详情页用得到，卡片不占地方；没有就返回 null
            ["originalName"] = Str(reader, "original_name"),
            ["releaseDate"] = Str(reader, "release_date"),
            // 片商是一部片的一个值，与 seriesId/seriesName 同形
            ["studioId"] = Str(reader, "studioid"),
            ["studioName"] = Str(reader, "studio_name"),
            // 官网只有详情页要把片商做成超链接时才用得到，卡片不带这一列
            ["studioLink"] = Str(reader, "studio_link"),
        };

        // 默认版本行自己的身份：详情页要标出"现在显示的是哪一版"
        if (HasColumn(reader, "file_code")) result["fileCode"] = Str(reader, "file_code");
        if (HasColumn(reader, "file_type_id")) result["fileTypeId"] = Str(reader, "file_type_id");
        if (HasColumn(reader, "file_label")) result["fileLabel"] = Str(reader, "file_label");
        if (HasColumn(reader, "file_type_name")) result["fileTypeName"] = Str(reader, "file_type_name");
        if (HasColumn(reader, "file_label") || HasColumn(reader, "file_type_name"))
            result["fileDisplayName"] = VideoFiles.DisplayName(Str(reader, "file_label"), Str(reader, "file_type_name"));

        if (HasColumn(reader, "scan_time")) result["scanTime"] = Str(reader, "scan_time");
        if (HasColumn(reader, "like_count")) result["likeCount"] = Int(reader, "like_count");
        if (HasColumn(reader, "series_name")) result["seriesName"] = Str(reader, "series_name");
        if (HasColumn(reader, "actor_names")) result["actorNames"] = Str(reader, "actor_names");
        // 点赞榜与最近点赞多带的三列：这一次赞的是哪一版、那一版的赞数、点赞时间。
        // 卡片本身不显示它们，只有这两个榜单的视图会读（前端按 likedFileId == fileId 决定要不要打版本标）
        if (HasColumn(reader, "liked_file_id")) result["likedFileId"] = Str(reader, "liked_file_id");
        if (HasColumn(reader, "liked_version")) result["likedVersion"] = Str(reader, "liked_version");
        if (HasColumn(reader, "version_like_count")) result["versionLikeCount"] = Int(reader, "version_like_count");
        if (HasColumn(reader, "like_time")) result["likeTime"] = Str(reader, "like_time");

        return result;
    }

    public static bool HasColumn(SqliteDataReader reader, string columnName)
    {
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (reader.GetName(i).Equals(columnName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 列表页共用的筛选项。字段名与 query 参数一一对应，
    /// 四个调用点（影片列表 / 演员详情 / 系列详情 / 片商详情）都构造这一个对象，
    /// 免得参数越加越长、各调用点漏传一个还看不出来。
    /// </summary>
    public sealed class SourceFilter
    {
        public string? Subtitle { get; set; }
        public string? Watermark { get; set; }
        public string? Resolution { get; set; }
        public string? StudioId { get; set; }
        public bool? HasFile { get; set; }
    }

    /// <summary>
    /// 拼接片源两维 / 分辨率档 / 片商 / 有没有文件这几项共用筛选。
    /// 三个字符串条件都过白名单，取值不在口径里一律当"没筛"——不能让前端传来的字符串进 SQL。
    /// 表别名固定为 v（影片）与 df（默认版本行，见 <see cref="FileJoin"/>），与 Columns 一致；
    /// 文件层这几项筛的是"当前作为影片口径的那一版"，不是"任一版本"。返回的片段以 AND 开头。
    /// </summary>
    public static string AppendCommonFilters(
        ref string where,
        List<SqliteParameter> parameters,
        SourceFilter f)
    {
        if (Utils.SourceStates.IsSubtitle(f.Subtitle))
        {
            where += " AND df.subtitle_state = @subtitleState";
            parameters.Add(new SqliteParameter("@subtitleState", f.Subtitle));
        }

        if (Utils.SourceStates.IsWatermark(f.Watermark))
        {
            where += " AND df.watermark_state = @watermarkState";
            parameters.Add(new SqliteParameter("@watermarkState", f.Watermark));
        }

        if (f.Resolution is not null && Utils.SourceStates.Resolutions.TryGetValue(f.Resolution, out var clause))
        {
            where += $" AND ({clause})";
        }

        // 片商是一部片的一个值，直接比列即可
        if (!string.IsNullOrWhiteSpace(f.StudioId))
        {
            where += " AND v.studioid = @studioId";
            parameters.Add(new SqliteParameter("@studioId", f.StudioId));
        }

        if (f.HasFile == true)
        {
            where += " AND df.file_size > 0";
        }
        else if (f.HasFile == false)
        {
            where += " AND IFNULL(df.file_size, 0) <= 0";
        }

        return where;
    }

    private static int Int(SqliteDataReader reader, string column)
        => HasColumn(reader, column) && reader[column] != DBNull.Value ? Convert.ToInt32(reader[column]) : 0;

    private static string? Str(SqliteDataReader reader, string column)
        => HasColumn(reader, column) && reader[column] != DBNull.Value ? reader[column].ToString() : null;
}

using Microsoft.Data.Sqlite;

namespace ckapi.Services;

/// <summary>
/// 影片卡片的列定义与 reader→JSON 映射。
///
/// 之前同一份 SELECT 列表在 7 处各写一遍，映射另有 4 套实现且字段名互不一致
/// （有的叫 addedAt 有的叫 ctime，有的缺 mediaAttrFlags，like_count 子查询有的漏了
/// target_type 过滤），前端 VideoCard 只能按最保守的交集渲染。统一到这里。
///
/// 约定：videos 表别名固定为 v，系列表别名固定为 s。
/// </summary>
public static class VideoCardQuery
{
    /// <summary>不含系列名</summary>
    public const string Columns = """
        v.id, v.code, v.name, v.category, v.country, v.cover_path, v.file_path, v.file_size,
        v.seriesid, v.ctime, v.subtitle_state, v.watermark_state, v.res_w, v.res_h,
        (SELECT COUNT(*) FROM video_likes WHERE video_id = v.id AND target_type='video') AS like_count,
        (SELECT GROUP_CONCAT(a.id || '|' || a.name, ',') FROM actors a
         JOIN video_actors va ON a.id = va.actor_id WHERE va.video_id = v.id) AS actor_names
        """;

    /// <summary>含系列名（需要 LEFT JOIN video_series s）</summary>
    public const string ColumnsWithSeries = Columns + ",\n        s.name AS series_name";

    /// <summary>
    /// 映射一行影片。缺失的可选列（like_count / actor_names / series_name）不会抛异常，
    /// 因此 `SELECT v.*` 这类部分列查询也能复用。
    /// </summary>
    public static Dictionary<string, object?> Map(SqliteDataReader reader)
    {
        var result = new Dictionary<string, object?>
        {
            ["id"] = reader["id"].ToString(),
            ["code"] = reader["code"] == DBNull.Value ? null : reader["code"].ToString(),
            ["name"] = reader["name"].ToString(),
            ["category"] = reader["category"] == DBNull.Value ? "" : reader["category"].ToString(),
            ["country"] = reader["country"] == DBNull.Value ? "" : reader["country"].ToString(),
            ["filePath"] = reader["file_path"].ToString(),
            ["fileSize"] = reader["file_size"] == DBNull.Value ? 0 : Convert.ToInt64(reader["file_size"]),
            ["coverPath"] = reader["cover_path"] == DBNull.Value ? null : reader["cover_path"].ToString(),
            ["seriesId"] = reader["seriesid"] == DBNull.Value ? null : reader["seriesid"].ToString(),
            // 两维都给了默认值：前端只要拿到字符串就能直接查文案表，不必再判空
            ["subtitleState"] = Str(reader, "subtitle_state") ?? Utils.SourceStates.Unknown,
            ["watermarkState"] = Str(reader, "watermark_state") ?? Utils.SourceStates.Unknown,
            ["resW"] = Int(reader, "res_w"),
            ["resH"] = Int(reader, "res_h"),
            // 原名与发行日期只有详情页用得到，卡片不占地方；没有就返回 null
            ["originalName"] = Str(reader, "original_name"),
            ["releaseDate"] = Str(reader, "release_date"),
        };

        if (HasColumn(reader, "scan_time")) result["scanTime"] = Str(reader, "scan_time");
        if (HasColumn(reader, "like_count")) result["likeCount"] = Int(reader, "like_count");
        if (HasColumn(reader, "series_name")) result["seriesName"] = Str(reader, "series_name");
        if (HasColumn(reader, "actor_names")) result["actorNames"] = Str(reader, "actor_names");

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
    /// 五个调用点（影片列表 / 演员详情 / 系列详情 / 片商详情 / 标签详情）都构造这一个对象，
    /// 免得参数越加越长、各调用点漏传一个还看不出来。
    /// </summary>
    public sealed class SourceFilter
    {
        public string? Subtitle { get; set; }
        public string? Watermark { get; set; }
        public string? Resolution { get; set; }
        public string? StudioId { get; set; }
        public string? TagId { get; set; }
        public bool? HasFile { get; set; }
    }

    /// <summary>
    /// 拼接片源两维 / 分辨率档 / 片商 / 标签 / 有没有文件这几项共用筛选。
    /// 三个字符串条件都过白名单，取值不在口径里一律当"没筛"——不能让前端传来的字符串进 SQL。
    /// 表别名固定为 v，与 Columns / ColumnsWithSeries 一致；返回的片段以 AND 开头。
    /// </summary>
    public static string AppendCommonFilters(
        ref string where,
        List<SqliteParameter> parameters,
        SourceFilter f)
    {
        if (Utils.SourceStates.IsSubtitle(f.Subtitle))
        {
            where += " AND v.subtitle_state = @subtitleState";
            parameters.Add(new SqliteParameter("@subtitleState", f.Subtitle));
        }

        if (Utils.SourceStates.IsWatermark(f.Watermark))
        {
            where += " AND v.watermark_state = @watermarkState";
            parameters.Add(new SqliteParameter("@watermarkState", f.Watermark));
        }

        if (f.Resolution is not null && Utils.SourceStates.Resolutions.TryGetValue(f.Resolution, out var clause))
        {
            where += $" AND ({clause})";
        }

        // 片商与标签走 EXISTS 而不是 JOIN：一部片挂两家片商时，JOIN 会把同一行翻倍
        if (!string.IsNullOrWhiteSpace(f.StudioId))
        {
            where += " AND EXISTS (SELECT 1 FROM video_studios x WHERE x.video_id = v.id AND x.studio_id = @studioId)";
            parameters.Add(new SqliteParameter("@studioId", f.StudioId));
        }

        if (!string.IsNullOrWhiteSpace(f.TagId))
        {
            where += " AND EXISTS (SELECT 1 FROM video_tags x WHERE x.video_id = v.id AND x.tag_id = @tagId)";
            parameters.Add(new SqliteParameter("@tagId", f.TagId));
        }

        if (f.HasFile == true)
        {
            where += " AND v.file_size > 0";
        }
        else if (f.HasFile == false)
        {
            where += " AND (v.file_size IS NULL OR v.file_size <= 0)";
        }

        return where;
    }

    private static int Int(SqliteDataReader reader, string column)
        => HasColumn(reader, column) && reader[column] != DBNull.Value ? Convert.ToInt32(reader[column]) : 0;

    private static string? Str(SqliteDataReader reader, string column)
        => HasColumn(reader, column) && reader[column] != DBNull.Value ? reader[column].ToString() : null;
}

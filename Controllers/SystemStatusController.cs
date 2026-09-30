using ckapi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace ckapi.Controllers;

/// <summary>
/// 运行状态与自检（设置里的「运行状态」分区）。
///
/// 这个库里最值钱的不是那 4800 行影片记录，而是它们之间的**关系不变量**：
/// 每部片恰好一条默认版本、一部片同一个类型只一条、点赞必须落在这部片的某个版本上、
/// 番号不重复、关联表不指向已经不存在的东西。这些被一次批量操作改坏之后界面不会报错，
/// 只是数字悄悄变了，往往过很久才从"某个列表看起来不对"里被发现。
/// 所以把它们写成一条命令就能跑完的断言集，而不是靠人想起来去抽查。
///
/// 这里**只做纯 SQL 断言**（整库量级毫秒到几十毫秒，进一次设置页跑一遍没问题）。
/// 需要读挂载卷的那些——file_path 死链、封面文件是否真在盘上、图片目录与库是否相符——
/// 一条都不放在这儿：全库 stat 是四千多次网络往返（SMB 上 0.1~0.5 秒一次），
/// 同步接口不能答应这种成本，它们属于"深度自检"任务，另说。
/// </summary>
[ApiController]
[Route("api/system")]
public class SystemStatusController : ControllerBase
{
    /// <summary>
    /// 一条不变量断言。Level=error 期望计数为 0；Level=notice 只是把规模报出来给人看，
    /// 不算失败（比如"两维还没标"的行数——那是还没扫，不是数据坏了）。
    /// </summary>
    private record Check(string Key, string Label, string Level, string CountSql, string? SampleSql = null, string? Hint = null);

    private static readonly Check[] Assertions =
    {
        new("file_none",
            "一部片没有任何版本行",
            "error",
            @"SELECT COUNT(*) FROM videos v WHERE NOT EXISTS (SELECT 1 FROM video_files f WHERE f.video_id = v.id)",
            @"SELECT IFNULL(group_concat(code, '、'), '') FROM (SELECT v.code FROM videos v WHERE NOT EXISTS (SELECT 1 FROM video_files f WHERE f.video_id = v.id) ORDER BY v.code LIMIT 5)",
            "v11 起文件层只存在于 video_files；这种片在列表里永远显示「没有文件」，扫描也捞不到它"),

        new("file_no_default",
            "有版本行但没有一条是默认",
            "error",
            @"SELECT COUNT(*) FROM videos v
              WHERE EXISTS (SELECT 1 FROM video_files f WHERE f.video_id = v.id)
                AND NOT EXISTS (SELECT 1 FROM video_files f WHERE f.video_id = v.id AND f.is_default = 1)",
            @"SELECT IFNULL(group_concat(code, '、'), '') FROM (SELECT v.code FROM videos v
              WHERE EXISTS (SELECT 1 FROM video_files f WHERE f.video_id = v.id)
                AND NOT EXISTS (SELECT 1 FROM video_files f WHERE f.video_id = v.id AND f.is_default = 1)
              ORDER BY v.code LIMIT 5)",
            "卡片的分辨率/大小/字幕水印都读默认那一版；没有默认版就没有口径"),

        new("file_multi_default",
            "一部片有多条默认版本",
            "error",
            @"SELECT COUNT(*) FROM (SELECT video_id FROM video_files GROUP BY video_id HAVING SUM(is_default) > 1)",
            @"SELECT IFNULL(group_concat(v.code, '、'), '') FROM (SELECT v2.code FROM (SELECT video_id FROM video_files GROUP BY video_id HAVING SUM(is_default) > 1) x
              JOIN videos v2 ON v2.id = x.video_id ORDER BY v2.code LIMIT 5)",
            "LEFT JOIN 会把它放大成多行，列表与统计都会重复计数"),

        new("type_duplicate",
            "同片同类型有多行（v12 约束违例）",
            "error",
            @"SELECT COUNT(*) FROM (SELECT video_id, type_id FROM video_files WHERE IFNULL(type_id, '') <> '' GROUP BY video_id, type_id HAVING COUNT(*) > 1)",
            @"SELECT IFNULL(group_concat(v.code, '、'), '') FROM (SELECT v2.code FROM (SELECT video_id FROM video_files WHERE IFNULL(type_id, '') <> '' GROUP BY video_id, type_id HAVING COUNT(*) > 1) x
              JOIN videos v2 ON v2.id = x.video_id ORDER BY v2.code LIMIT 5)",
            "一部片同一个版本类型只能有一条；违例说明唯一索引没兜住（可能被直接改过库）"),

        new("file_orphan",
            "版本行不属于任何影片",
            "error",
            @"SELECT COUNT(*) FROM video_files f WHERE NOT EXISTS (SELECT 1 FROM videos v WHERE v.id = f.video_id)",
            @"SELECT IFNULL(group_concat(f.code, '、'), '') FROM (SELECT f.code FROM video_files f WHERE NOT EXISTS (SELECT 1 FROM videos v WHERE v.id = f.video_id) ORDER BY f.code LIMIT 5)",
            "删影片时没清版本行留下的孤儿；它会被版本类型的计数算进去"),

        new("like_bad_file",
            "点赞的 file_id 不属于那部片",
            "error",
            @"SELECT COUNT(*) FROM video_likes l
              JOIN videos v ON v.id = l.video_id
              LEFT JOIN video_files f ON f.id = l.file_id
              WHERE l.target_type = 'video' AND IFNULL(l.file_id, '') <> ''
                AND (f.id IS NULL OR f.video_id <> v.id)",
            @"SELECT IFNULL(group_concat(x.id, ','), '') FROM (SELECT l.id FROM video_likes l
              JOIN videos v ON v.id = l.video_id
              LEFT JOIN video_files f ON f.id = l.file_id
              WHERE l.target_type = 'video' AND IFNULL(l.file_id, '') <> '' AND (f.id IS NULL OR f.video_id <> v.id) LIMIT 5) x",
            "点赞榜与日历都按版本读 label/类型名，串了片的点赞会显示成别的片的版本"),

        new("like_no_file",
            "影片点赞没带 file_id",
            "error",
            @"SELECT COUNT(*) FROM video_likes WHERE target_type = 'video' AND IFNULL(file_id, '') = ''",
            null,
            Hint: "v11 起每条点赞都应指到一个版本行；空的那批要么是老数据要么是绕过了接口"),

        new("code_duplicate",
            "番号重复",
            "error",
            @"SELECT COUNT(*) FROM (SELECT code FROM videos WHERE IFNULL(code, '') <> '' GROUP BY code HAVING COUNT(*) > 1)",
            @"SELECT IFNULL(group_concat(code, '、'), '') FROM (SELECT code FROM videos WHERE IFNULL(code, '') <> '' GROUP BY code HAVING COUNT(*) > 1 ORDER BY code LIMIT 5)",
            "番号是按文件名找回、级联改名、字幕匹配的钥匙，重复了这几件事都会互相打架"),

        new("series_dangling",
            "影片指向不存在的系列",
            "error",
            @"SELECT COUNT(*) FROM videos v WHERE IFNULL(v.seriesid, '') <> '' AND NOT EXISTS (SELECT 1 FROM video_series s WHERE s.id = v.seriesid)"),

        new("studio_dangling",
            "影片指向不存在的片商",
            "error",
            @"SELECT COUNT(*) FROM videos v WHERE IFNULL(v.studioid, '') <> '' AND NOT EXISTS (SELECT 1 FROM studios st WHERE st.id = v.studioid)"),

        new("type_dangling",
            "版本行指向不存在的类型",
            "error",
            @"SELECT COUNT(*) FROM video_files f WHERE IFNULL(f.type_id, '') <> '' AND NOT EXISTS (SELECT 1 FROM version_types t WHERE t.id = f.type_id)"),

        new("actor_link_dangling",
            "演员关联表悬空",
            "error",
            @"SELECT (SELECT COUNT(*) FROM video_actors va WHERE NOT EXISTS (SELECT 1 FROM videos v WHERE v.id = va.video_id))
                  + (SELECT COUNT(*) FROM video_actors va WHERE NOT EXISTS (SELECT 1 FROM actors a WHERE a.id = va.actor_id))",
            null,
            Hint: "删演员/删影片时没清关联表；演员页的影片数会对不上"),

        new("index_missing",
            "关键唯一索引不在了",
            "error",
            // 「每片恰好一条默认」和「同片同类型只一条」平时是靠这两个部分唯一索引挡住的，
            // 计数检查在索引存在时永远跑不出违例（试过：直接 UPDATE 会被 UNIQUE constraint 拒绝）。
            // 所以这里真正要断言的是**索引还在不在**——从旧备份恢复、或有人手改过库，索引就可能没了
            @"SELECT 2 - COUNT(DISTINCT name) FROM sqlite_master
              WHERE type = 'index' AND name IN ('idx_video_files_default', 'idx_video_files_movie_type')",
            @"SELECT IFNULL(group_concat(x.name, '、'), '') FROM (SELECT n.name FROM (SELECT 'idx_video_files_default' AS name UNION ALL SELECT 'idx_video_files_movie_type') n
              WHERE NOT EXISTS (SELECT 1 FROM sqlite_master m WHERE m.type = 'index' AND m.name = n.name)) x",
            "这两个索引是不变量的执行者；缺了的话下面几条计数检查也才第一次真正有意义"),

        new("size_missing",
            "有路径但没量到大小",
            "notice",
            @"SELECT COUNT(*) FROM video_files WHERE IFNULL(file_path, '') <> '' AND IFNULL(file_size, 0) = 0",
            null,
            Hint: "扫描还没轮到，或者文件确实不在盘上——这一条要结合「深度自检」才知道是哪种"),

        new("res_missing",
            "有文件但分辨率还没扫",
            "notice",
            @"SELECT COUNT(*) FROM video_files WHERE IFNULL(file_size, 0) > 0 AND IFNULL(res_h, 0) = 0",
            null,
            Hint: "扫描只读文件头（盒头），是网络盘上的小读；没扫的越多，筛选「分辨率」越不准"),

        new("source_unmarked",
            "两维（字幕/水印）都还没标",
            "notice",
            @"SELECT COUNT(*) FROM video_files f WHERE f.is_default = 1
              AND IFNULL(f.subtitle_state, 'unknown') = 'unknown'
              AND IFNULL(f.watermark_state, 'unknown') = 'unknown'",
            null,
            Hint: "「看过没」就是从这两维推的，全未标的新片会排在首页最前面（这是有意的）")
    };

    private readonly ILogger<SystemStatusController> _logger;
    private readonly Utils.SQLiteHelper _db;

    public SystemStatusController(ILogger<SystemStatusController> logger, Utils.SQLiteHelper db)
    {
        _logger = logger;
        _db = db;
    }

    /// <summary>
    /// 跑一遍全部不变量。纯读，不建事务；整库量级在几十毫秒内。
    /// </summary>
    [HttpGet("checks")]
    public IActionResult RunChecks()
    {
        try
        {
            using var conn = _db.GetConnection();
            conn.Open();

            var results = new List<object>();
            var failed = 0;

            foreach (var c in Assertions)
            {
                // 单项隔离：某条断言自己写坏了（列名改了、表被搬走）不该让整个面板打不开——
                // 一次真实的踩坑就是这里引了个不存在的别名，接口直接 500
                try
                {
                    var count = Convert.ToInt32(Scalar(conn, c.CountSql) ?? 0);
                    if (c.Level == "error" && count > 0) failed++;

                    object? samples = null;
                    if (count > 0 && c.SampleSql is not null)
                    {
                        var s = Scalar(conn, c.SampleSql)?.ToString();
                        if (!string.IsNullOrWhiteSpace(s)) samples = s;
                    }

                    results.Add(new
                    {
                        key = c.Key,
                        label = c.Label,
                        level = c.Level,
                        count,
                        pass = c.Level != "error" || count == 0,
                        samples,
                        hint = c.Hint
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "自检项 {Key} 执行失败", c.Key);
                    results.Add(new
                    {
                        key = c.Key,
                        label = c.Label,
                        level = c.Level,
                        count = -1,
                        pass = false,
                        samples = (object?)null,
                        hint = "这条检查本身跑挂了：" + ex.Message
                    });
                }
            }

            return Ok(new
            {
                success = true,
                data = new
                {
                    schemaVersion = Convert.ToInt32(Scalar(conn, "PRAGMA user_version") ?? 0),
                    targetVersion = DataService.SchemaTargetVersion,
                    journalMode = Scalar(conn, "PRAGMA journal_mode")?.ToString(),
                    dbPath = _db.GetDbPath(),
                    totals = new
                    {
                        videos = Convert.ToInt32(Scalar(conn, "SELECT COUNT(*) FROM videos") ?? 0),
                        files = Convert.ToInt32(Scalar(conn, "SELECT COUNT(*) FROM video_files") ?? 0),
                        actors = Convert.ToInt32(Scalar(conn, "SELECT COUNT(*) FROM actors") ?? 0),
                        likes = Convert.ToInt32(Scalar(conn, "SELECT COUNT(*) FROM video_likes") ?? 0),
                        comics = Convert.ToInt32(Scalar(conn, "SELECT COUNT(*) FROM comics") ?? 0)
                    },
                    ranAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    failed,
                    pass = failed == 0,
                    checks = results
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "系统自检失败");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>
    /// 备份概况：目录配了没有、最近一份是几点、多大、留了多少。
    /// 与自检分开是因为它要扫目录（只读几个文件的元信息，不碰媒体卷），
    /// 而自检是纯 SQL——两者失败原因完全不同，混在一起不好报错。
    /// </summary>
    [HttpGet("backups")]
    public IActionResult Backups()
    {
        try
        {
            return Ok(new { success = true, data = BackupService.Status(_db.GetDbPath(), _db.GetBackupPath()) });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取备份概况失败");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    private static object? Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = new SqliteCommand(sql, conn);
        return cmd.ExecuteScalar();
    }
}

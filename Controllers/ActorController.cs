using ckapi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using System.Text.Json.Serialization;

namespace ckapi.Controllers;

/// <summary>
/// 演员相关接口
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class ActorController : ControllerBase
{
    private readonly ILogger<ActorController> _logger;
    private readonly IConfiguration _config;
    private readonly Utils.SQLiteHelper _db;

    public ActorController(ILogger<ActorController> logger, IConfiguration config, Utils.SQLiteHelper db)
    {
        _logger = logger;
        _config = config;
        _db = db;
    }

    private SqliteConnection GetConnection()
    {
        return new SqliteConnection(_config.GetConnectionString("DefaultConnection"));
    }

    /// <summary>
    /// 获取演员列表
    /// </summary>
    [HttpGet]
    public IActionResult GetActors([FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] string? keyword = null, [FromQuery] string? country = null,
        [FromQuery] string? sortBy = null)
    {
        try
        {
            page = Utils.Paging.ClampPage(page);
            pageSize = Utils.Paging.ClampSize(pageSize);
            var offset = (page - 1) * pageSize;
            var whereClause = "WHERE 1=1";
            var parameters = new List<SqliteParameter>();

            // 排序
            var orderBy = sortBy?.ToLower() switch
            {
                "name" => "a.name ASC",
                "likecount" => "like_count DESC",
                "videocount" => "video_count DESC",
                _ => "like_count DESC"
            };
            // 并列行按 id 收尾，保证翻页结果稳定
            orderBy += ", a.id ASC";

            if (!string.IsNullOrEmpty(keyword))
            {
                // 曾用名也要命中：actor_aliases 一行一个，检索走 EXISTS 而不是字符串 LIKE
                whereClause += @" AND (a.name LIKE @keyword
                            OR EXISTS (SELECT 1 FROM actor_aliases aa
                                       WHERE aa.actor_id = a.id AND aa.alias LIKE @keyword))";
                parameters.Add(new SqliteParameter("@keyword", $"%{keyword}%"));
            }

            if (!string.IsNullOrEmpty(country))
            {
                whereClause += " AND a.country = @country";
                parameters.Add(new SqliteParameter("@country", country));
            }

            using var conn = GetConnection();
            conn.Open();

            // 总数
            var countSql = $"SELECT COUNT(*) FROM actors a {whereClause}";
            using (var countCmd = new SqliteCommand(countSql, conn))
            {
                foreach (var p in parameters) countCmd.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
                var total = Convert.ToInt32(countCmd.ExecuteScalar());

                // 列表 + video_count 子查询
                var sql = $@"
                    SELECT a.*, 
                        (SELECT COUNT(*) FROM video_actors va WHERE va.actor_id = a.id) as video_count,
                        (SELECT COUNT(*) FROM video_actors va2 
                         JOIN videos v ON va2.video_id = v.id 
                         JOIN video_likes vl ON v.id = vl.video_id AND vl.target_type = 'video'
                         WHERE va2.actor_id = a.id) as like_count,
                        (SELECT COUNT(*) FROM video_actors va3 
                         JOIN videos v2 ON va3.video_id = v2.id 
                         WHERE va3.actor_id = a.id AND (v2.file_size IS NULL OR v2.file_size = 0)) as unloaded_count,
                        (SELECT GROUP_CONCAT(alias, char(31)) FROM actor_aliases aa2 WHERE aa2.actor_id = a.id) as aliases,
                        -- 列表也要带 links：编辑框是从列表行打开的，缺了它一保存就把外链抹掉
                        (SELECT GROUP_CONCAT(kind || char(31) || url, char(30)) FROM actor_links al WHERE al.actor_id = a.id) as links,
                        -- 只有主图参与列表展示：脸只出现在演员列表这一页，别处仍不放头像
                        (SELECT ai2.file_name FROM actor_images ai2
                          WHERE ai2.actor_id = a.id AND ai2.is_primary = 1 LIMIT 1) as avatar
                    FROM actors a
                    {whereClause}
                    ORDER BY " + orderBy + @"
                    LIMIT @pageSize OFFSET @offset";

                using var cmd = new SqliteCommand(sql, conn);
                foreach (var p in parameters) cmd.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
                cmd.Parameters.Add(new SqliteParameter("@pageSize", pageSize));
                cmd.Parameters.Add(new SqliteParameter("@offset", offset));

                using var reader = cmd.ExecuteReader();
                var actors = new List<object>();
                while (reader.Read())
                {
                    actors.Add(new
                    {
                        id = reader["id"].ToString(),
                        name = reader["name"].ToString(),
                        aliases = SplitAliases(reader["aliases"]),
                        links = SplitLinks(reader["links"]),
                        avatar = reader["avatar"] is null or DBNull ? null : reader["avatar"].ToString(),
                        country = reader["country"] == DBNull.Value ? null : reader["country"].ToString(),
                        bio = reader["bio"] == DBNull.Value ? null : reader["bio"].ToString(),
                        videoCount = reader["video_count"] == DBNull.Value ? 0 : Convert.ToInt32(reader["video_count"]),
                        likeCount = reader["like_count"] == DBNull.Value ? 0 : Convert.ToInt32(reader["like_count"]),
                        unloadedCount = reader["unloaded_count"] == DBNull.Value ? 0 : Convert.ToInt32(reader["unloaded_count"])
                    });
                }

                return Ok(new { success = true, data = actors, total, page, pageSize });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取演员列表失败");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>
    /// 获取演员详情
    /// </summary>
    [HttpGet("{id}")]
    public IActionResult GetActor(string id)
    {
        try
        {
            using var conn = GetConnection();
            conn.Open();

            var sql = @"SELECT a.*, 
                        (SELECT COUNT(*) FROM video_likes vl 
                         JOIN video_actors va ON vl.video_id = va.video_id 
                         WHERE va.actor_id = a.id) as like_count,
                        (SELECT GROUP_CONCAT(alias, char(31)) FROM actor_aliases aa WHERE aa.actor_id = a.id) as aliases
                        FROM actors a WHERE a.id = @id";
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.Add(new SqliteParameter("@id", id));

            string? name, country, bio;
            List<string> aliases;
            int likeCount;
            using (var reader = cmd.ExecuteReader())
            {
                if (!reader.Read())
                    return NotFound(new { success = false, message = "演员不存在" });

                name = reader["name"].ToString();
                country = reader["country"] is null or DBNull ? null : reader["country"].ToString();
                bio = reader["bio"] is null or DBNull ? null : reader["bio"].ToString();
                aliases = SplitAliases(reader["aliases"]);
                likeCount = reader["like_count"] is null or DBNull ? 0 : Convert.ToInt32(reader["like_count"]);
            }

            // 外链与图片各要一次查询，所以前面的 reader 必须先关掉：
            // Microsoft.Data.Sqlite 不支持多个活动结果集，叠着发会把连接状态搞乱
            return Ok(new
            {
                success = true,
                data = new
                {
                    id,
                    name,
                    aliases,
                    country,
                    bio,
                    likeCount,
                    links = ReadLinks(conn, id),
                    images = ReadImages(conn, id)
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取演员详情失败");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>
    /// 添加演员
    /// </summary>
    [HttpPost]
    public IActionResult AddActor([FromBody] AddActorRequest request)
    {
        try
        {
            if (string.IsNullOrEmpty(request.Name))
                return Ok(new { success = false, message = "演员姓名不能为空" });

            var id = Guid.NewGuid().ToString("N").ToUpper();
            var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            using var conn = GetConnection();
            conn.Open();

            // 检查重名
            using (var checkCmd = new SqliteCommand("SELECT COUNT(*) FROM actors WHERE name = @name", conn))
            {
                checkCmd.Parameters.Add(new SqliteParameter("@name", request.Name));
                if (Convert.ToInt32(checkCmd.ExecuteScalar()) > 0)
                    return Ok(new { success = false, message = "演员已存在" });
            }

            var sql = @"INSERT INTO actors (id, name, country, bio, ctime) VALUES (@id, @name, @country, @bio, @addedAt)";
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.Add(new SqliteParameter("@id", id));
            cmd.Parameters.Add(new SqliteParameter("@name", request.Name));
            cmd.Parameters.Add(new SqliteParameter("@country", (object?)request.Country ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@bio", (object?)request.Bio ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@addedAt", now));
            cmd.ExecuteNonQuery();

            SaveAliases(conn, id, request.Name, request.Aliases);
            SaveLinks(conn, id, request.Links);

            return Ok(new { success = true, data = new { id, name = request.Name }, message = "添加成功" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "添加演员失败");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>
    /// 更新演员
    /// </summary>
    [HttpPut("{id}")]
    public IActionResult UpdateActor(string id, [FromBody] UpdateActorRequest request)
    {
        try
        {
            using var conn = GetConnection();
            conn.Open();

            var sql = @"UPDATE actors SET name = @name, country = @country, bio = @bio WHERE id = @id";
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.Add(new SqliteParameter("@id", id));
            cmd.Parameters.Add(new SqliteParameter("@name", request.Name ?? ""));
            cmd.Parameters.Add(new SqliteParameter("@country", (object?)request.Country ?? DBNull.Value));
            cmd.Parameters.Add(new SqliteParameter("@bio", (object?)request.Bio ?? DBNull.Value));

            if (cmd.ExecuteNonQuery() <= 0)
                return Ok(new { success = false, message = "演员不存在" });

            SaveAliases(conn, id, request.Name, request.Aliases);
            SaveLinks(conn, id, request.Links);
            return Ok(new { success = true, message = "更新成功" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新演员失败");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>
    /// 删除演员
    /// </summary>
    [HttpDelete("{id}")]
    public IActionResult DeleteActor(string id)
    {
        try
        {
            using var conn = GetConnection();
            conn.Open();

            // 删除关联
            using (var relCmd = new SqliteCommand("DELETE FROM video_actors WHERE actor_id = @actorId", conn))
            {
                relCmd.Parameters.Add(new SqliteParameter("@actorId", id));
                relCmd.ExecuteNonQuery();
            }

            // 连接串没开 FK 强制，级联不会自己触发，子表一律显式清
            foreach (var child in new[] { "actor_aliases", "actor_links", "actor_images" })
            {
                using var cmd = new SqliteCommand($"DELETE FROM [{child}] WHERE actor_id = @actorId", conn);
                cmd.Parameters.Add(new SqliteParameter("@actorId", id));
                cmd.ExecuteNonQuery();
            }

            // 删除演员
            using (var cmd = new SqliteCommand("DELETE FROM actors WHERE id = @id", conn))
            {
                cmd.Parameters.Add(new SqliteParameter("@id", id));
                if (cmd.ExecuteNonQuery() > 0)
                    return Ok(new { success = true, message = "删除成功" });
                else
                    return Ok(new { success = false, message = "演员不存在" });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除演员失败");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>
    /// 疑似重复演员，两类证据：其中一人的本名恰好挂在对方的曾用名里（强），
    /// 或两人共享某个曾用名（弱，按强→影片数排序）。
    /// 共享曾用名并不等于同一个人（实测里就有把第三个女优的名字同时挂到两人名下的脏数据），
    /// 所以这里只负责把候选摆出来，合不合由人在界面上判断。
    /// </summary>
    [HttpGet("duplicates")]
    public IActionResult GetDuplicates()
    {
        try
        {
            const string sql = @"
                WITH rel AS (
                    -- 强证据：其中一人的本名，恰好挂在另一人的曾用名里
                    SELECT min(a1.id, a2.id) AS id1, max(a1.id, a2.id) AS id2,
                           a1.name AS evidence, 1 AS strong
                    FROM actors a1
                    JOIN actor_aliases t2 ON t2.alias = a1.name
                    JOIN actors a2 ON a2.id = t2.actor_id
                    WHERE a1.id <> a2.id
                    UNION ALL
                    -- 弱证据：共享某个曾用名（也可能是第三个女优的名字被同时挂到两人名下）
                    SELECT min(t1.actor_id, t2.actor_id), max(t1.actor_id, t2.actor_id), t1.alias, 0
                    FROM actor_aliases t1
                    JOIN actor_aliases t2 ON t1.alias = t2.alias AND t1.actor_id < t2.actor_id
                ),
                pairs AS (SELECT id1, id2, evidence, max(strong) AS strong FROM rel GROUP BY id1, id2, evidence),
                -- SQLite 不允许 group_concat(DISTINCT x, sep)：带两个参数就不行，所以上一层先去重
                grouped AS (SELECT id1, id2, group_concat(evidence, char(31)) AS evidences, max(strong) AS strong
                            FROM pairs GROUP BY id1, id2)
                SELECT g.id1, g.id2, g.evidences, g.strong,
                       a1.name AS name1, a2.name AS name2,
                       a1.country AS country1, a2.country AS country2,
                       (SELECT COUNT(*) FROM video_actors v WHERE v.actor_id = g.id1) AS c1,
                       (SELECT COUNT(*) FROM video_actors v WHERE v.actor_id = g.id2) AS c2
                FROM grouped g
                JOIN actors a1 ON a1.id = g.id1
                JOIN actors a2 ON a2.id = g.id2
                ORDER BY g.strong DESC, (c1 + c2) DESC, name1";

            using var conn = GetConnection();
            conn.Open();

            var list = new List<object>();
            using var cmd = new SqliteCommand(sql, conn);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                list.Add(new
                {
                    id1 = reader.GetString(0),
                    id2 = reader.GetString(1),
                    name1 = reader["name1"].ToString(),
                    name2 = reader["name2"].ToString(),
                    country1 = reader["country1"] is null or DBNull ? null : reader["country1"].ToString(),
                    country2 = reader["country2"] is null or DBNull ? null : reader["country2"].ToString(),
                    videoCount1 = Convert.ToInt32(reader["c1"]),
                    videoCount2 = Convert.ToInt32(reader["c2"]),
                    strong = Convert.ToInt32(reader["strong"]) == 1,
                    sharedAliases = SplitAliases(reader["evidences"])
                });

            return Ok(new { success = true, data = list, total = list.Count });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "查询疑似重复演员失败");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>
    /// 把 from 并入 to：影片关系改挂、曾用名与外链合并，from 删除。
    ///
    /// 这是全站唯一一个不可撤销的写操作，所以动手前先做一次即时快照。
    /// 被合并方名下还有照片时直接拒绝 —— 图片按 &lt;艳图目录&gt;/&lt;演员ID&gt;/ 定位，
    /// 只把行改挂到目标名下会得到一批 404，磁盘目录得由人先挪。
    /// </summary>
    [HttpPost("merge")]
    public IActionResult MergeActors([FromBody] MergeActorsRequest request)
    {
        try
        {
            var from = request.From;
            var to = request.To;
            if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to))
                return Ok(new { success = false, message = "要合并的两个演员都要指定" });
            if (from == to)
                return Ok(new { success = false, message = "不能把自己和自己合并" });

            using var conn = GetConnection();
            conn.Open();

            string? fromName = null, toName = null;
            using (var q = new SqliteCommand("SELECT id, name FROM actors WHERE id IN (@from, @to)", conn))
            {
                q.Parameters.Add(new SqliteParameter("@from", from));
                q.Parameters.Add(new SqliteParameter("@to", to));
                using var reader = q.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.GetString(0) == from) fromName = reader.GetString(1);
                    else toName = reader.GetString(1);
                }
            }

            if (fromName is null || toName is null)
                return Ok(new { success = false, message = "演员不存在或已被删除" });

            int fromImages;
            using (var q = new SqliteCommand("SELECT COUNT(*) FROM actor_images WHERE actor_id = @id", conn))
            {
                q.Parameters.Add(new SqliteParameter("@id", from));
                fromImages = Convert.ToInt32(q.ExecuteScalar());
            }

            if (fromImages > 0)
                return Ok(new { success = false, message = $"「{fromName}」名下还有 {fromImages} 张照片，请先把照片目录挪到「{toName}」下再合并" });

            _db.BackupDatabase("合并演员前", tag: "pre-merge");

            int movedVideos = 0;
            int mergedAliases = 0;
            int keptLinks = 0;

            using var tx = conn.BeginTransaction();

            // 影片关系：先补到目标名下（同一部片两人都挂着时 OR IGNORE 自然跳过），再清掉来源的
            using (var mv = new SqliteCommand(
                       @"INSERT OR IGNORE INTO video_actors (video_id, actor_id)
                         SELECT video_id, @to FROM video_actors WHERE actor_id = @from;
                         DELETE FROM video_actors WHERE actor_id = @from;", conn, tx))
            {
                mv.Parameters.Add(new SqliteParameter("@from", from));
                mv.Parameters.Add(new SqliteParameter("@to", to));
                mv.ExecuteNonQuery();
                using var cnt = new SqliteCommand("SELECT COUNT(*) FROM video_actors WHERE actor_id = @to", conn, tx);
                cnt.Parameters.Add(new SqliteParameter("@to", to));
                movedVideos = Convert.ToInt32(cnt.ExecuteScalar());
            }

            // 曾用名整组搬过去，并把"她原来叫什么"也留成一条曾用名——艺名换过的人，旧名就是检索入口
            using (var al = new SqliteCommand(
                       @"INSERT OR IGNORE INTO actor_aliases (actor_id, alias)
                         SELECT @to, alias FROM actor_aliases WHERE actor_id = @from;", conn, tx))
            {
                al.Parameters.Add(new SqliteParameter("@from", from));
                al.Parameters.Add(new SqliteParameter("@to", to));
                mergedAliases = al.ExecuteNonQuery();
            }

            using (var own = new SqliteCommand(
                       "INSERT OR IGNORE INTO actor_aliases (actor_id, alias) VALUES (@to, @name)", conn, tx))
            {
                own.Parameters.Add(new SqliteParameter("@to", to));
                own.Parameters.Add(new SqliteParameter("@name", fromName));
                mergedAliases += own.ExecuteNonQuery();
            }

            // 外链走一遍统一规则（按地址去重、每人 ≤10 条），不在 SQL 里另起一套判重口径
            var links = new List<Utils.ActorLink>();
            using (var q = new SqliteCommand("SELECT kind, url FROM actor_links WHERE actor_id IN (@from, @to) ORDER BY actor_id = @to DESC", conn, tx))
            {
                q.Parameters.Add(new SqliteParameter("@from", from));
                q.Parameters.Add(new SqliteParameter("@to", to));
                using var reader = q.ExecuteReader();
                while (reader.Read())
                    links.Add(new Utils.ActorLink { Kind = reader.GetString(0), Url = reader.GetString(1) });
            }

            var merged = Utils.Links.Normalize(links);
            using (var del = new SqliteCommand("DELETE FROM actor_links WHERE actor_id IN (@from, @to)", conn, tx))
            {
                del.Parameters.Add(new SqliteParameter("@from", from));
                del.Parameters.Add(new SqliteParameter("@to", to));
                del.ExecuteNonQuery();
            }

            foreach (var link in merged)
            {
                using var ins = new SqliteCommand(
                    "INSERT INTO actor_links (id, actor_id, kind, url) VALUES (@id, @to, @kind, @url)", conn, tx);
                ins.Parameters.Add(new SqliteParameter("@id", Guid.NewGuid().ToString("N").ToUpper()));
                ins.Parameters.Add(new SqliteParameter("@to", to));
                ins.Parameters.Add(new SqliteParameter("@kind", link.Kind));
                ins.Parameters.Add(new SqliteParameter("@url", link.Url));
                keptLinks += ins.ExecuteNonQuery();
            }

            // 目标缺的字段用来源的补上，已有的不覆盖
            using (var fill = new SqliteCommand(
                       @"UPDATE actors SET
                           country = IFNULL(country, (SELECT country FROM actors WHERE id = @from)),
                           bio     = IFNULL(bio,     (SELECT bio     FROM actors WHERE id = @from))
                         WHERE id = @to", conn, tx))
            {
                fill.Parameters.Add(new SqliteParameter("@from", from));
                fill.Parameters.Add(new SqliteParameter("@to", to));
                fill.ExecuteNonQuery();
            }

            foreach (var child in new[] { "actor_aliases", "actor_links", "actor_images" })
            {
                using var del = new SqliteCommand($"DELETE FROM [{child}] WHERE actor_id = @from", conn, tx);
                del.Parameters.Add(new SqliteParameter("@from", from));
                del.ExecuteNonQuery();
            }

            using (var del = new SqliteCommand("DELETE FROM actors WHERE id = @from", conn, tx))
            {
                del.Parameters.Add(new SqliteParameter("@from", from));
                del.ExecuteNonQuery();
            }

            tx.Commit();

            return Ok(new {
                success = true,
                data = new { to, name = toName, videoCount = movedVideos, aliases = mergedAliases, links = keptLinks },
                message = $"已把「{fromName}」并入「{toName}」：现挂 {movedVideos} 部影片、{keptLinks} 条外链"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "合并演员失败");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>
    /// 地区可选列表（统一取设置里的规范值，与影片/系列一致）
    /// </summary>
    [HttpGet("countries")]
    public IActionResult GetCountries()
    {
        try
        {
            using var conn = GetConnection();
            conn.Open();
            var countries = Utils.Options.CommaList(conn, Utils.Options.Countries);
            return Ok(new { success = true, data = countries });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取地区列表失败");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>
    /// 获取演员的影片列表
    /// </summary>
    [HttpGet("{id}/videos")]
    public IActionResult GetActorVideos(string id, [FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        [FromQuery] int? mediaAttrFlags = null, [FromQuery] bool? hasFile = null)
    {
        try
        {
            page = Utils.Paging.ClampPage(page);
            pageSize = Utils.Paging.ClampSize(pageSize);
            var offset = (page - 1) * pageSize;
            using var conn = GetConnection();
            conn.Open();

            // 片源/下载状态筛选下沉到 SQL：原先前端在已分页的结果里再过滤一次，
            // 只能筛到当前页，页数不同结果就不同。
            var where = "WHERE va.actor_id = @actorId";
            var parameters = new List<SqliteParameter> { new("@actorId", id) };
            VideoCardQuery.AppendCommonFilters(ref where, parameters, mediaAttrFlags, hasFile);

            var countSql = $@"SELECT COUNT(*) FROM videos v INNER JOIN video_actors va ON v.id = va.video_id {where}";
            using (var countCmd = new SqliteCommand(countSql, conn))
            {
                foreach (var p in parameters) countCmd.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
                var total = Convert.ToInt32(countCmd.ExecuteScalar());

                var sql = $@"
                    SELECT {VideoCardQuery.ColumnsWithSeries}
                    FROM videos v
                    INNER JOIN video_actors va ON v.id = va.video_id
                    LEFT JOIN video_series s ON v.seriesid = s.id
                    {where}
                    ORDER BY v.code ASC, v.id ASC
                    LIMIT @pageSize OFFSET @offset";
                using var cmd = new SqliteCommand(sql, conn);
                foreach (var p in parameters) cmd.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
                cmd.Parameters.Add(new SqliteParameter("@pageSize", pageSize));
                cmd.Parameters.Add(new SqliteParameter("@offset", offset));

                using var reader = cmd.ExecuteReader();
                var videos = new List<object>();
                while (reader.Read())
                {
                    videos.Add(VideoCardQuery.Map(reader));
                }

                return Ok(new { success = true, data = videos, total, page, pageSize });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取演员影片失败");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }
    /// <summary>
    /// 把一位演员的图片目录同步进 actor_images。
    /// 只有界面上的「同步照片」会调它 —— 打开详情页不再扫盘，这就是这张表存在的全部理由。
    /// </summary>
    [HttpPost("{id}/images/sync")]
    public IActionResult SyncImages(string id)
    {
        try
        {
            using var conn = GetConnection();
            conn.Open();

            var posterDir = Utils.ImageIndex.PosterDir(conn);
            if (string.IsNullOrEmpty(posterDir))
                return Ok(new { success = false, message = "未配置艳图目录（系统设置 → 艳图目录）" });

            using (var exists = new SqliteCommand("SELECT COUNT(*) FROM actors WHERE id = @id", conn))
            {
                exists.Parameters.Add(new SqliteParameter("@id", id));
                if (Convert.ToInt32(exists.ExecuteScalar()) == 0)
                    return NotFound(new { success = false, message = "演员不存在" });
            }

            var stat = SyncDir(conn, id, Path.Combine(posterDir, id));
            return Ok(new { success = true, data = stat, message = StatMessage(stat) });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "同步演员图片失败");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>
    /// 一次同步全部演员：把艳图目录下的子目录逐个对一遍（目录名就是演员 ID）。
    /// 根目录不存在时直接报错返回、一个字都不写 —— 挂载卷暂时没挂上时，
    /// "按差集删除"会把整张表清空，而照片元数据一旦丢了只能重扫。
    /// </summary>
    [HttpPost("images/sync")]
    public IActionResult SyncAllImages()
    {
        try
        {
            using var conn = GetConnection();
            conn.Open();

            var posterDir = Utils.ImageIndex.PosterDir(conn);
            if (string.IsNullOrEmpty(posterDir))
                return Ok(new { success = false, message = "未配置艳图目录（系统设置 → 艳图目录）" });
            if (!Directory.Exists(posterDir))
                return Ok(new { success = false, message = $"艳图目录不存在：{posterDir}" });

            var actorIds = new HashSet<string>(StringComparer.Ordinal);
            using (var read = new SqliteCommand("SELECT id FROM actors", conn))
            using (var reader = read.ExecuteReader())
            {
                while (reader.Read()) actorIds.Add(reader.GetString(0));
            }

            var added = 0; var updated = 0; var removed = 0; var scanned = 0;
            var visited = new HashSet<string>(StringComparer.Ordinal);

            foreach (var dir in Directory.GetDirectories(posterDir).OrderBy(x => x, StringComparer.Ordinal))
            {
                // default/ 是艳图页自己的图池，不是哪位演员
                var id = Path.GetFileName(dir);
                if (!actorIds.Contains(id)) continue;

                visited.Add(id);
                var stat = SyncDir(conn, id, dir);
                added += stat.Added;
                updated += stat.Updated;
                removed += stat.Removed;
                scanned++;
            }

            // 整个目录被人删掉的演员：只有走到这一步才说明根目录确实可读，差集删除才是安全的
            var stale = new List<string>();
            using (var read = new SqliteCommand("SELECT DISTINCT actor_id FROM actor_images", conn))
            using (var reader = read.ExecuteReader())
            {
                while (reader.Read())
                {
                    var id = reader.GetString(0);
                    if (!visited.Contains(id)) stale.Add(id);
                }
            }

            foreach (var id in stale)
            {
                using var del = new SqliteCommand("DELETE FROM actor_images WHERE actor_id = @id", conn);
                del.Parameters.Add(new SqliteParameter("@id", id));
                removed += del.ExecuteNonQuery();
            }

            var data = new { actors = scanned, added, updated, removed, stale = stale.Count };
            return Ok(new { success = true, data, message = $"扫了 {scanned} 位演员：新增 {added}、更新 {updated}、移除 {removed}" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "批量同步演员图片失败");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>
    /// 指定某张图为演员的主图（列表页那张脸）。
    /// 先全清再置一：部分唯一索引不允许同时有两张，单条 UPDATE 会撞约束。
    /// </summary>
    [HttpPut("{id}/image/primary")]
    public IActionResult SetPrimaryImage(string id, [FromBody] PrimaryImageRequest request)
    {
        try
        {
            var fileName = Utils.SafePath.AsFileName(request.FileName);
            if (fileName is null)
                return Ok(new { success = false, message = "文件名不合法" });

            using var conn = GetConnection();
            conn.Open();

            using var tx = conn.BeginTransaction();
            using (var clear = new SqliteCommand("UPDATE actor_images SET is_primary = 0 WHERE actor_id = @id", conn, tx))
            {
                clear.Parameters.Add(new SqliteParameter("@id", id));
                clear.ExecuteNonQuery();
            }

            int hit;
            using (var set = new SqliteCommand(
                       "UPDATE actor_images SET is_primary = 1 WHERE actor_id = @id AND file_name = @file", conn, tx))
            {
                set.Parameters.Add(new SqliteParameter("@id", id));
                set.Parameters.Add(new SqliteParameter("@file", fileName));
                hit = set.ExecuteNonQuery();
            }

            // 影响行数为 0 说明这张图不在表里（没同步过或已删），回滚让主图保持原样
            if (hit == 0)
            {
                tx.Rollback();
                return Ok(new { success = false, message = "这张图片还没同步进库" });
            }

            tx.Commit();
            return Ok(new { success = true, message = "已设为头像" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "设置演员主图失败");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>
    /// 同步一位演员的图片目录，对齐动作本身在 Utils.ImageIndex（与艳图共用一份口径），
    /// 这里只补演员特有的一步：一张主图都没有时选一张出来。
    /// </summary>
    private (int Added, int Updated, int Removed, int Total, string? Primary) SyncDir(
        SqliteConnection conn, string actorId, string dir)
    {
        var stat = Utils.ImageIndex.Sync(conn, "actor_images", "actor_id", actorId, dir);

        string? primary = null;
        using (var q = new SqliteCommand(
                   "SELECT file_name FROM actor_images WHERE actor_id = @id AND is_primary = 1", conn))
        {
            q.Parameters.Add(new SqliteParameter("@id", actorId));
            primary = q.ExecuteScalar()?.ToString();
        }

        if (primary is null && stat.Total > 0)
        {
            // 每人只留一张主图（部分唯一索引兜底），这里只在一张都没有时补选
            var files = new List<string>();
            using (var q = new SqliteCommand(
                       "SELECT file_name FROM actor_images WHERE actor_id = @id ORDER BY file_name", conn))
            {
                q.Parameters.Add(new SqliteParameter("@id", actorId));
                using var reader = q.ExecuteReader();
                while (reader.Read()) files.Add(reader.GetString(0));
            }

            primary = PickPrimary(files);
            if (primary is not null)
            {
                using var set = new SqliteCommand(
                    "UPDATE actor_images SET is_primary = 1 WHERE actor_id = @id AND file_name = @file", conn);
                set.Parameters.Add(new SqliteParameter("@id", actorId));
                set.Parameters.Add(new SqliteParameter("@file", primary));
                set.ExecuteNonQuery();
            }
        }

        return (stat.Added, stat.Updated, stat.Removed, stat.Total, primary);
    }

    /// <summary>手工放图时最常见的几种"这就是头像"命名，都命中不了就按文件名取第一张</summary>
    private static readonly string[] PrimaryHints =
        { "默认", "头像", "default", "avatar", "cover", "main", "profile", "primary", "1", "01", "first" };

    private static string? PickPrimary(ICollection<string> files)
    {
        var ordered = files.OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (ordered.Count == 0) return null;

        foreach (var hint in PrimaryHints)
        {
            var hit = ordered.FirstOrDefault(f =>
                string.Equals(Path.GetFileNameWithoutExtension(f), hint, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }

        return ordered[0];
    }

    private static string StatMessage((int Added, int Updated, int Removed, int Total, string? Primary) stat)
        => $"新增 {stat.Added}、更新 {stat.Updated}、移除 {stat.Removed}，共 {stat.Total} 张";

    /// <summary>相册用：主图排最前，顺序稳定，翻页时才不会跳</summary>
    private static List<object> ReadImages(SqliteConnection conn, string actorId)
    {
        var list = new List<object>();
        using var cmd = new SqliteCommand(
            @"SELECT file_name, is_primary, IFNULL(width, 0), IFNULL(height, 0), size
              FROM actor_images WHERE actor_id = @id ORDER BY is_primary DESC, file_name", conn);
        cmd.Parameters.Add(new SqliteParameter("@id", actorId));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(new
            {
                fileName = reader.GetString(0),
                primary = reader.GetInt32(1) == 1,
                width = reader.GetInt32(2),
                height = reader.GetInt32(3),
                size = reader.GetInt64(4)
            });
        return list;
    }

    /// <summary>
    /// 演员图片原图。列表来自 actor_images（不再扫盘），字节仍从挂载卷直读。
    /// </summary>
    [HttpGet("{id}/poster/{fileName}")]
    public IActionResult GetPoster(string id, string fileName)
    {
        try
        {
            var safeFileName = Utils.SafePath.AsFileName(fileName);
            var safeId = Utils.SafePath.AsFileName(id);
            if (safeFileName is null || safeId is null || !Utils.SafePath.IsImageFile(safeFileName))
                return NotFound();

            using var conn = GetConnection();
            conn.Open();
            var posterDir = Utils.ImageIndex.PosterDir(conn);
            if (string.IsNullOrEmpty(posterDir)) return NotFound();

            if (!ImageKnown(conn, safeId, safeFileName)) return NotFound();

            var filePath = Path.Combine(posterDir, safeId, safeFileName);
            if (!Utils.SafePath.IsInside(filePath, posterDir)) return NotFound();

            var result = Utils.CachedFile.TryServe(this, filePath);
            if (result is not null) return result;

            return NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取海报图片失败");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>
    /// 缩略图：s=160 给列表页头像，m=400 给详情页相册。
    /// 只认表里有的文件名 —— 表就是白名单，没同步过的路径一律 404。
    /// </summary>
    [HttpGet("{id}/thumb/{size}/{fileName}")]
    public IActionResult GetThumb(string id, string size, string fileName)
    {
        try
        {
            var safeFileName = Utils.SafePath.AsFileName(fileName);
            var safeId = Utils.SafePath.AsFileName(id);
            var width = Utils.Thumbs.WidthOf(size);
            if (safeFileName is null || safeId is null || width == 0 || !Utils.SafePath.IsImageFile(safeFileName))
                return NotFound();

            using var conn = GetConnection();
            conn.Open();
            var posterDir = Utils.ImageIndex.PosterDir(conn);
            if (string.IsNullOrEmpty(posterDir)) return NotFound();
            if (!ImageKnown(conn, safeId, safeFileName)) return NotFound();

            var source = Path.Combine(posterDir, safeId, safeFileName);
            if (!Utils.SafePath.IsInside(source, posterDir)) return NotFound();

            var thumb = Utils.Thumbs.Ensure(source, Utils.Thumbs.CacheRoot(_config, _db.GetDbPath()),
                safeId, safeFileName, width, out var sw, out var sh);
            if (thumb is null)
                // 解码不了的（损坏或没编进来的格式）退回原图，页面至少还有东西可看
                return Utils.CachedFile.TryServe(this, source) ?? NotFound();

            if (sw > 0)
            {
                // 顺手回填宽高：前端靠它预留画框，不用每张都解码一次。只在缺失时写，避免每次命中都产生写操作
                using var back = new SqliteCommand(
                    "UPDATE actor_images SET width = @w, height = @h WHERE actor_id = @id AND file_name = @file AND width IS NULL",
                    conn);
                back.Parameters.Add(new SqliteParameter("@w", sw));
                back.Parameters.Add(new SqliteParameter("@h", sh));
                back.Parameters.Add(new SqliteParameter("@id", safeId));
                back.Parameters.Add(new SqliteParameter("@file", safeFileName));
                back.ExecuteNonQuery();
            }

            return Utils.CachedFile.TryServe(this, thumb.Value.Path, "image/webp") ?? NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "生成演员缩略图失败");
            return StatusCode(500, new { success = false, message = Utils.Api.InternalErrorMessage });
        }
    }

    /// <summary>这张图片是否已经同步进表（并且属于这位演员）</summary>
    private static bool ImageKnown(SqliteConnection conn, string actorId, string fileName)
    {
        using var cmd = new SqliteCommand(
            "SELECT 1 FROM actor_images WHERE actor_id = @id AND file_name = @file", conn);
        cmd.Parameters.Add(new SqliteParameter("@id", actorId));
        cmd.Parameters.Add(new SqliteParameter("@file", fileName));
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>列表与详情都用 GROUP_CONCAT 一次带出别名，避免每行再查一次</summary>
    private static List<string> SplitAliases(object? value)
    {
        var raw = value is null or DBNull ? null : value.ToString();
        if (string.IsNullOrEmpty(raw)) return new List<string>();
        // 分隔符是 char(31)：别名里可能出现逗号，不会出现单元分隔符
        return raw.Split((char)31, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    /// <summary>整组替换：先清后写，规则与迁移共用 Utils.Aliases</summary>
    private void SaveAliases(SqliteConnection conn, string actorId, string? name, List<string>? raw)
    {
        var aliases = Utils.Aliases.Normalize(raw, name);

        using var tx = conn.BeginTransaction();
        using (var del = new SqliteCommand("DELETE FROM actor_aliases WHERE actor_id = @id", conn, tx))
        {
            del.Parameters.Add(new SqliteParameter("@id", actorId));
            del.ExecuteNonQuery();
        }

        foreach (var alias in aliases)
        {
            using var ins = new SqliteCommand(
                "INSERT OR IGNORE INTO actor_aliases (actor_id, alias) VALUES (@id, @alias)", conn, tx);
            ins.Parameters.Add(new SqliteParameter("@id", actorId));
            ins.Parameters.Add(new SqliteParameter("@alias", alias));
            ins.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>列表里用 GROUP_CONCAT 一次带出：条目间 char(30)，kind 与 url 间 char(31)</summary>
    private static List<object> SplitLinks(object? value)
    {
        var raw = value is null or DBNull ? null : value.ToString();
        if (string.IsNullOrEmpty(raw)) return new List<object>();

        return raw.Split((char)30, StringSplitOptions.RemoveEmptyEntries)
            .Select((entry) => entry.Split((char)31, 2))
            .Where((parts) => parts.Length == 2 && parts[1].Length > 0)
            .Select((parts) => (object)new { kind = parts[0], url = parts[1] })
            .ToList();
    }

    /// <summary>详情单独查一次即可，不必走列表那套 GROUP_CONCAT 编码</summary>
    private static List<object> ReadLinks(SqliteConnection conn, string actorId)
    {
        var list = new List<object>();
        using var cmd = new SqliteCommand(
            "SELECT kind, url FROM actor_links WHERE actor_id = @id ORDER BY kind, url", conn);
        cmd.Parameters.Add(new SqliteParameter("@id", actorId));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(new { kind = reader.GetString(0), url = reader.GetString(1) });
        return list;
    }

    /// <summary>整组替换；非法 scheme、超长、重复的地址在这里被丢掉</summary>
    private void SaveLinks(SqliteConnection conn, string actorId, List<Utils.ActorLink>? raw)
    {
        var links = Utils.Links.Normalize(raw);

        using var tx = conn.BeginTransaction();
        using (var del = new SqliteCommand("DELETE FROM actor_links WHERE actor_id = @id", conn, tx))
        {
            del.Parameters.Add(new SqliteParameter("@id", actorId));
            del.ExecuteNonQuery();
        }

        foreach (var link in links)
        {
            using var ins = new SqliteCommand(
                "INSERT INTO actor_links (id, actor_id, kind, url) VALUES (@id, @actorId, @kind, @url)", conn, tx);
            ins.Parameters.Add(new SqliteParameter("@id", Guid.NewGuid().ToString("N").ToUpper()));
            ins.Parameters.Add(new SqliteParameter("@actorId", actorId));
            ins.Parameters.Add(new SqliteParameter("@kind", link.Kind));
            ins.Parameters.Add(new SqliteParameter("@url", link.Url));
            ins.ExecuteNonQuery();
        }

        tx.Commit();
    }
}

public class AddActorRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
    [JsonPropertyName("aliases")]
    public List<string>? Aliases { get; set; }
    [JsonPropertyName("links")]
    public List<Utils.ActorLink>? Links { get; set; }
    [JsonPropertyName("country")]
    public string? Country { get; set; }
    [JsonPropertyName("bio")]
    public string? Bio { get; set; }
}

/// <summary>把哪张图设为演员主图（列表页那张脸）</summary>
public class PrimaryImageRequest
{
    [JsonPropertyName("fileName")]
    public string? FileName { get; set; }
}

/// <summary>合并重复演员：from 并进 to，to 是活下来的那个名字</summary>
public class MergeActorsRequest
{
    [JsonPropertyName("from")]
    public string? From { get; set; }
    [JsonPropertyName("to")]
    public string? To { get; set; }
}

public class UpdateActorRequest
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
    [JsonPropertyName("aliases")]
    public List<string>? Aliases { get; set; }
    [JsonPropertyName("links")]
    public List<Utils.ActorLink>? Links { get; set; }
    [JsonPropertyName("country")]
    public string? Country { get; set; }
    [JsonPropertyName("bio")]
    public string? Bio { get; set; }
}

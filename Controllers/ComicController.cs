using Microsoft.AspNetCore.Mvc;
using Io = System.IO;
using Microsoft.Data.Sqlite;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.IO;

namespace ckapi.Controllers;

/// <summary>
/// 漫画控制器
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class ComicController : ControllerBase
{
    private readonly IConfiguration _config;
    private readonly ILogger<ComicController> _logger;

    public ComicController(IConfiguration config, ILogger<ComicController> logger)
    {
        _config = config;
        _logger = logger;
    }

    private SqliteConnection GetConnection()
    {
        return new SqliteConnection(_config.GetConnectionString("DefaultConnection"));
    }

    /// <summary>
    /// 获取漫画列表
    /// </summary>
    [HttpGet("list")]
    public IActionResult GetList(
        [FromQuery] int pageIndex = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? keyword = null)
    {
        try
        {
            var offset = (pageIndex - 1) * pageSize;
            var whereClause = "WHERE 1=1";
            var parameters = new List<SqliteParameter>();

            if (!string.IsNullOrEmpty(keyword))
            {
                whereClause += " AND (c.name LIKE @keyword OR c.author LIKE @keyword)";
                parameters.Add(new SqliteParameter("@keyword", "%" + keyword + "%"));
            }

            var countSql = "SELECT COUNT(*) FROM comics c " + whereClause;
            var total = Convert.ToInt32(ExecuteScalar(countSql, parameters.ToArray()));

            var sql = $@"
                SELECT c.*,
                       (SELECT COUNT(*) FROM comic_chapters cc WHERE cc.comic_id = c.id) as chapter_count
                FROM comics c
                {whereClause}
                ORDER BY c.ctime DESC
                LIMIT @pageSize OFFSET @offset";
            parameters.Add(new SqliteParameter("@pageSize", pageSize));
            parameters.Add(new SqliteParameter("@offset", offset));

            using var conn = GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddRange(parameters.ToArray());
            using var reader = cmd.ExecuteReader();

            var list = new List<object>();
            while (reader.Read())
            {
                list.Add(ReadComicRow(reader));
            }

            return Ok(new
            {
                success = true,
                data = new { list, total, page = pageIndex, pageSize }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetComicList failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 获取漫画详情
    /// </summary>
    [HttpGet("{id}")]
    public IActionResult GetById(string id)
    {
        try
        {
            using var conn = GetConnection();
            conn.Open();

            var sql = @"SELECT c.*,
           (SELECT COUNT(*) FROM comic_chapters cc WHERE cc.comic_id = c.id) as chapter_count
           FROM comics c WHERE c.id = @id";
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.Add(new SqliteParameter("@id", id));
            using var reader = cmd.ExecuteReader();

            if (!reader.Read())
                return NotFound(new { success = false, message = "漫画不存在" });

            var comic = ReadComicRow(reader);

            var chaptersSql = "SELECT * FROM comic_chapters WHERE comic_id = @comicId ORDER BY sort_order ASC, ctime ASC";
            using var chCmd = new SqliteCommand(chaptersSql, conn);
            chCmd.Parameters.Add(new SqliteParameter("@comicId", id));
            using var chReader = chCmd.ExecuteReader();

            var chapters = new List<object>();
            while (chReader.Read())
            {
                chapters.Add(new
                {
                    id = chReader["id"].ToString(),
                    comicId = chReader["comic_id"].ToString(),
                    title = chReader["title"].ToString(),
                    directory = chReader["directory"].ToString(),
                    sortOrder = Convert.ToInt32(chReader["sort_order"]),
                    imageCount = Convert.ToInt32(chReader["image_count"]),
                    ctime = chReader["ctime"].ToString()
                });
            }

            return Ok(new { success = true, data = new { comic, chapters } });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetComicById failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 添加漫画
    /// </summary>
    [HttpPost("add")]
    public IActionResult AddComic([FromBody] Models.AddComicRequest req)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return BadRequest(new { success = false, message = "名称不能为空" });

            var id = Guid.NewGuid().ToString("N").ToUpper();
            var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            var sql = @"
                INSERT INTO comics (id, name, author, description, url, cover_path, directory, ctime, utime)
                VALUES (@id, @name, @author, @description, @url, @coverPath, @directory, @ctime, @utime)";

            using var conn = GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.Add(new SqliteParameter("@id", id));
            cmd.Parameters.Add(new SqliteParameter("@name", req.Name ?? ""));
            cmd.Parameters.Add(new SqliteParameter("@author", req.Author ?? ""));
            cmd.Parameters.Add(new SqliteParameter("@description", req.Description ?? ""));
            cmd.Parameters.Add(new SqliteParameter("@url", req.Url ?? ""));
            cmd.Parameters.Add(new SqliteParameter("@coverPath", req.CoverPath ?? ""));
            cmd.Parameters.Add(new SqliteParameter("@directory", req.Directory ?? ""));
            cmd.Parameters.Add(new SqliteParameter("@ctime", now));
            cmd.Parameters.Add(new SqliteParameter("@utime", now));
            cmd.ExecuteNonQuery();

            return Ok(new { success = true, data = new { id }, message = "添加成功" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AddComic failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 更新漫画
    /// </summary>
    [HttpPut("{id}")]
    public IActionResult UpdateComic(string id, [FromBody] Models.UpdateComicRequest req)
    {
        try
        {
            using var conn = GetConnection();
            conn.Open();

            using var checkCmd = new SqliteCommand("SELECT COUNT(*) FROM comics WHERE id = @id", conn);
            checkCmd.Parameters.Add(new SqliteParameter("@id", id));
            if (Convert.ToInt32(checkCmd.ExecuteScalar()) == 0)
                return NotFound(new { success = false, message = "漫画不存在" });

            var sql = @"
                UPDATE comics SET
                    name = @name,
                    author = @author,
                    description = @description,
                    url = @url,
                    cover_path = @coverPath,
                    directory = @directory,
                    utime = @utime
                WHERE id = @id";

            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.Add(new SqliteParameter("@id", id));
            cmd.Parameters.Add(new SqliteParameter("@name", req.Name ?? ""));
            cmd.Parameters.Add(new SqliteParameter("@author", req.Author ?? ""));
            cmd.Parameters.Add(new SqliteParameter("@description", req.Description ?? ""));
            cmd.Parameters.Add(new SqliteParameter("@url", req.Url ?? ""));
            cmd.Parameters.Add(new SqliteParameter("@coverPath", req.CoverPath ?? ""));
            cmd.Parameters.Add(new SqliteParameter("@directory", req.Directory ?? ""));
            cmd.Parameters.Add(new SqliteParameter("@utime", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
            cmd.ExecuteNonQuery();

            return Ok(new { success = true, message = "更新成功" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UpdateComic failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 删除漫画
    /// </summary>
    [HttpDelete("{id}")]
    public IActionResult DeleteComic(string id)
    {
        try
        {
            using var conn = GetConnection();
            conn.Open();

            using var delChCmd = new SqliteCommand("DELETE FROM comic_chapters WHERE comic_id = @comicId", conn);
            delChCmd.Parameters.Add(new SqliteParameter("@comicId", id));
            delChCmd.ExecuteNonQuery();

            using var delCmd = new SqliteCommand("DELETE FROM comics WHERE id = @id", conn);
            delCmd.Parameters.Add(new SqliteParameter("@id", id));
            var rows = delCmd.ExecuteNonQuery();

            if (rows == 0)
                return NotFound(new { success = false, message = "漫画不存在" });

            return Ok(new { success = true, message = "删除成功" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DeleteComic failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 添加章节（扫描目录结构）
    /// </summary>
    [HttpPost("{comicId}/chapters")]
    public IActionResult AddChapter(string comicId, [FromBody] AddChapterRequest req)
    {
        try
        {
            using var conn = GetConnection();
            conn.Open();

            using var checkCmd = new SqliteCommand("SELECT COUNT(*) FROM comics WHERE id = @id", conn);
            checkCmd.Parameters.Add(new SqliteParameter("@id", comicId));
            if (Convert.ToInt32(checkCmd.ExecuteScalar()) == 0)
                return NotFound(new { success = false, message = "漫画不存在" });

            var id = Guid.NewGuid().ToString("N").ToUpper();
            var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            int imageCount = 0;
            var chapterDir = req.Directory ?? "";
            if (!string.IsNullOrEmpty(chapterDir) && Io.Directory.Exists(chapterDir))
            {
                var imageExts = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp" };
                var files = Io.Directory.GetFiles(chapterDir)
                    .Where(f => imageExts.Contains(Io.Path.GetExtension(f).ToLower()))
                    .Where(f => !Io.Path.GetFileName(f).StartsWith("._"))
                    .ToArray();
                imageCount = files.Length;
            }

            int maxSort = 0;
            using var sortCmd = new SqliteCommand("SELECT MAX(sort_order) FROM comic_chapters WHERE comic_id = @comicId", conn);
            sortCmd.Parameters.Add(new SqliteParameter("@comicId", comicId));
            var sortVal = sortCmd.ExecuteScalar();
            if (sortVal != null && sortVal != DBNull.Value)
                maxSort = Convert.ToInt32(sortVal);

            var sql = @"
                INSERT INTO comic_chapters (id, comic_id, title, directory, sort_order, image_count, ctime)
                VALUES (@id, @comicId, @title, @directory, @sortOrder, @imageCount, @ctime)";

            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.Add(new SqliteParameter("@id", id));
            cmd.Parameters.Add(new SqliteParameter("@comicId", comicId));
            cmd.Parameters.Add(new SqliteParameter("@title", req.Title ?? Io.Path.GetFileName(chapterDir)));
            cmd.Parameters.Add(new SqliteParameter("@directory", chapterDir));
            cmd.Parameters.Add(new SqliteParameter("@sortOrder", maxSort + 1));
            cmd.Parameters.Add(new SqliteParameter("@imageCount", imageCount));
            cmd.Parameters.Add(new SqliteParameter("@ctime", now));
            cmd.ExecuteNonQuery();

            return Ok(new { success = true, data = new { id, imageCount }, message = "添加成功" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AddChapter failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 获取章节图片列表
    /// </summary>
    [HttpGet("chapter/{chapterId}/images")]
    public IActionResult GetChapterImages(string chapterId)
    {
        try
        {
            using var conn = GetConnection();
            conn.Open();

            string? chapterDir = null;
            using (var chCmd = new SqliteCommand("SELECT directory FROM comic_chapters WHERE id = @id", conn))
            {
                chCmd.Parameters.Add(new SqliteParameter("@id", chapterId));
                var result = chCmd.ExecuteScalar();
                if (result == null || result == DBNull.Value)
                    return NotFound(new { success = false, message = "章节不存在" });
                chapterDir = result.ToString();
            }

            if (string.IsNullOrEmpty(chapterDir) || !Io.Directory.Exists(chapterDir))
                return Ok(new { success = true, data = new { images = new object[0] } });

            var imageExts = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp" };
            var files = Io.Directory.GetFiles(chapterDir)
                .Where(f => imageExts.Contains(Io.Path.GetExtension(f).ToLower()))
                .Where(f => !Io.Path.GetFileName(f).StartsWith("._"))
                .OrderBy(f => f, new NaturalStringComparer())
                .ToArray();

            var images = files.Select((f, idx) => new
            {
                fileName = Io.Path.GetFileName(f),
                index = idx,
                isDecrypted = Io.File.Exists(Io.Path.Combine(chapterDir, "_decrypted", Io.Path.GetFileNameWithoutExtension(f) + ".jpg")),
                size = new Io.FileInfo(f).Length
            }).ToArray();

            return Ok(new { success = true, data = new { images } });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetChapterImages failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }


    /// <summary>
    /// 获取漫画封面图代理
    /// </summary>
    [HttpGet("image/cover/{**path}")]
    public IActionResult GetCoverImage(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path))
                return NotFound(new { success = false, message = "路径为空" });

            if (!System.IO.File.Exists(path))
                return NotFound(new { success = false, message = "封面文件不存在" });

            var bytes = System.IO.File.ReadAllBytes(path);
            var ext = System.IO.Path.GetExtension(path).ToLower();
            var contentType = ext switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".bmp" => "image/bmp",
                _ => "application/octet-stream"
            };

            return PhysicalFile(path, contentType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetCoverImage failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 删除章节
    /// </summary>
    [HttpDelete("chapter/{id}")]
    public IActionResult DeleteChapter(string id)
    {
        try
        {
            using var conn = GetConnection();
            conn.Open();

            using var cmd = new SqliteCommand("DELETE FROM comic_chapters WHERE id = @id", conn);
            cmd.Parameters.Add(new SqliteParameter("@id", id));
            var rows = cmd.ExecuteNonQuery();

            if (rows == 0)
                return NotFound(new { success = false, message = "章节不存在" });

            return Ok(new { success = true, message = "删除成功" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DeleteChapter failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 修改章节
    /// </summary>
    [HttpPut("chapter/{id}")]
    public IActionResult UpdateChapter(string id, [FromBody] Models.UpdateChapterRequest req)
    {
        try
        {
            using var conn = GetConnection();
            conn.Open();

            // 查询原 chapter
            string? originalDir = null;
            using (var getCmd = new SqliteCommand("SELECT directory FROM comic_chapters WHERE id = @id", conn))
            {
                getCmd.Parameters.Add(new SqliteParameter("@id", id));
                var result = getCmd.ExecuteScalar();
                if (result == null || result == DBNull.Value)
                    return NotFound(new { success = false, message = "章节不存在" });
                originalDir = result.ToString();
            }

            var setClauses = new List<string>();
            var updateParams = new List<SqliteParameter> { new SqliteParameter("@id", id) };

            if (req.Title != null)
            {
                setClauses.Add("title = @title");
                updateParams.Add(new SqliteParameter("@title", req.Title));
            }

            if (req.Directory != null)
            {
                setClauses.Add("directory = @directory");
                updateParams.Add(new SqliteParameter("@directory", req.Directory));
            }

            if (req.SortOrder.HasValue)
            {
                setClauses.Add("sort_order = @sort_order");
                updateParams.Add(new SqliteParameter("@sort_order", req.SortOrder.Value));
            }

            setClauses.Add("utime = @utime");
            updateParams.Add(new SqliteParameter("@utime", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));

            if (setClauses.Count == 1)
                return BadRequest(new { success = false, message = "没有需要更新的字段" });

            var sql = $"UPDATE comic_chapters SET {string.Join(", ", setClauses)} WHERE id = @id";
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddRange(updateParams.ToArray());
            var rows = cmd.ExecuteNonQuery();

            // 若目录变更，重新统计图片数
            if (req.Directory != null && req.Directory != originalDir)
            {
                int imageCount = 0;
                if (Directory.Exists(req.Directory))
                    imageCount = Directory.GetFiles(req.Directory)
                        .Count(f => !f.Contains("_decrypted") && new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp" }.Contains(Path.GetExtension(f).ToLowerInvariant()));
                using var updCmd = new SqliteCommand(
                    "UPDATE comic_chapters SET image_count = @image_count WHERE id = @id", conn);
                updCmd.Parameters.Add(new SqliteParameter("@image_count", imageCount));
                updCmd.Parameters.Add(new SqliteParameter("@id", id));
                updCmd.ExecuteNonQuery();
            }

            return Ok(new { success = true, message = "更新成功" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UpdateChapter failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }


    /// <summary>
    /// 测试接口（调试用）
    /// </summary>
    [HttpGet("test")]
    public IActionResult Test()
    {
        try
        {
            using var conn = GetConnection();
            conn.Open();
            using var cmd = new SqliteCommand("SELECT COUNT(*) FROM comics", conn);
            var count = cmd.ExecuteScalar();
            return Ok(new { success = true, message = "OK", count });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { success = false, error = ex.Message, type = ex.GetType().Name });
        }
    }

    /// <summary>
    /// 还原单张图片（删除解密文件，回退到原图）
    /// </summary>
    [HttpPost("restore/image")]
    public IActionResult RestoreImage([FromBody] Models.DecryptImageRequest req)
    {
        try
        {
            if (string.IsNullOrEmpty(req.ChapterId) || string.IsNullOrEmpty(req.ImageName))
                return BadRequest(new { success = false, message = "章节ID和图片名不能为空" });

            using var conn = GetConnection();
            conn.Open();

            string? chapterDir = null;
            using (var chCmd = new SqliteCommand("SELECT directory FROM comic_chapters WHERE id = @id", conn))
            {
                chCmd.Parameters.Add(new SqliteParameter("@id", req.ChapterId));
                var result = chCmd.ExecuteScalar();
                if (result == null || result == DBNull.Value)
                    return NotFound(new { success = false, message = "章节不存在" });
                chapterDir = result.ToString();
            }

            var baseName = Io.Path.GetFileNameWithoutExtension(req.ImageName);
            var decryptedDir = Io.Path.Combine(chapterDir!, "_decrypted");
            var decryptedPath = Io.Path.Combine(decryptedDir, baseName + ".jpg");

            if (Io.File.Exists(decryptedPath))
            {
                Io.File.Delete(decryptedPath);
                return Ok(new { success = true, message = "还原成功" });
            }
            return Ok(new { success = true, message = "无解密文件，无需还原" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RestoreImage failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 还原章节内所有图片（清空 _decrypted 目录）
    /// </summary>
    [HttpPost("restore/batch")]
    public IActionResult RestoreBatch([FromBody] Models.DecryptTaskRequest req)
    {
        try
        {
            if (string.IsNullOrEmpty(req.ChapterId))
                return BadRequest(new { success = false, message = "章节ID不能为空" });

            using var conn = GetConnection();
            conn.Open();

            string? chapterDir = null;
            using (var chCmd = new SqliteCommand("SELECT directory FROM comic_chapters WHERE id = @id", conn))
            {
                chCmd.Parameters.Add(new SqliteParameter("@id", req.ChapterId));
                var result = chCmd.ExecuteScalar();
                if (result == null || result == DBNull.Value)
                    return NotFound(new { success = false, message = "章节不存在" });
                chapterDir = result.ToString();
            }

            var decryptedDir = Io.Path.Combine(chapterDir!, "_decrypted");
            var deleted = 0;
            if (Io.Directory.Exists(decryptedDir))
            {
                foreach (var f in Io.Directory.GetFiles(decryptedDir, "*.jpg"))
                {
                    Io.File.Delete(f);
                    deleted++;
                }
            }
            return Ok(new { success = true, data = new { deleted }, message = $"已还原 {deleted} 张图片" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RestoreBatch failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

        /// <summary>
    /// 解密单张图片
    /// </summary>
    [HttpPost("decrypt/image")]
    public IActionResult DecryptImage([FromBody] Models.DecryptImageRequest req)
    {
        try
        {
            if (string.IsNullOrEmpty(req.ChapterId) || string.IsNullOrEmpty(req.ImageName))
                return BadRequest(new { success = false, message = "章节ID和图片名不能为空" });

            var config = req.Config ?? new Models.ComicDecryptConfig { Rows = 3, Order = new List<int> { 2, 0, 1 } };

            using var conn = GetConnection();
            conn.Open();

            string? chapterDir = null;
            using (var chCmd = new SqliteCommand("SELECT directory FROM comic_chapters WHERE id = @id", conn))
            {
                chCmd.Parameters.Add(new SqliteParameter("@id", req.ChapterId));
                var result = chCmd.ExecuteScalar();
                if (result == null || result == DBNull.Value)
                    return NotFound(new { success = false, message = "章节不存在" });
                chapterDir = result.ToString();
            }

            var srcPath = Io.Path.Combine(chapterDir!, req.ImageName);
            if (!Io.File.Exists(srcPath))
                return NotFound(new { success = false, message = "图片文件不存在: " + req.ImageName });

            var baseName = Io.Path.GetFileNameWithoutExtension(req.ImageName);
            var decryptedDir = Io.Path.Combine(chapterDir!, "_decrypted");
            var outPath = Io.Path.Combine(decryptedDir, baseName + ".jpg");

            if (Io.File.Exists(outPath) && !req.Overwrite)
                return Ok(new { success = true, data = new { outputPath = outPath, skipped = true }, message = "文件已存在，跳过" });

            Io.Directory.CreateDirectory(decryptedDir);
            DecryptImageRows(srcPath, outPath, config);

            return Ok(new { success = true, data = new { outputPath = outPath }, message = "解密成功" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DecryptImage failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 批量解密章节内所有图片
    /// </summary>
    [HttpPost("decrypt/batch")]
    public IActionResult DecryptBatch([FromBody] Models.DecryptTaskRequest req)
    {
        try
        {
            if (string.IsNullOrEmpty(req.ChapterId))
                return BadRequest(new { success = false, message = "章节ID不能为空" });

            var config = req.Config ?? new Models.ComicDecryptConfig { Rows = 3, Order = new List<int> { 2, 0, 1 } };
            var results = new List<object>();

            using var conn = GetConnection();
            conn.Open();

            string? chapterDir = null;
            using (var chCmd = new SqliteCommand("SELECT directory FROM comic_chapters WHERE id = @id", conn))
            {
                chCmd.Parameters.Add(new SqliteParameter("@id", req.ChapterId));
                var result = chCmd.ExecuteScalar();
                if (result == null || result == DBNull.Value)
                    return NotFound(new { success = false, message = "章节不存在" });
                chapterDir = result.ToString();
            }

            var imageExts = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp" };
            var files = Io.Directory.GetFiles(chapterDir!)
                .Where(f => imageExts.Contains(Io.Path.GetExtension(f).ToLower()))
                .Where(f => !Io.Path.GetFileName(f).StartsWith("._"))
                .Where(f => !f.Contains("_decrypted"))
                .OrderBy(f => f, new NaturalStringComparer())
                .ToArray();

            if (req.ImageNames != null && req.ImageNames.Count > 0)
                files = files.Where(f => req.ImageNames.Contains(Io.Path.GetFileName(f))).ToArray();

            foreach (var srcPath in files)
            {
                var fileName = Io.Path.GetFileName(srcPath);
                var baseName = Io.Path.GetFileNameWithoutExtension(srcPath);
                var decryptedDir = Io.Path.Combine(chapterDir!, "_decrypted");
                var outPath = Io.Path.Combine(decryptedDir, baseName + ".jpg");

                try
                {
                    if (Io.File.Exists(outPath) && !req.Overwrite)
                    {
                        results.Add(new { fileName, outputPath = outPath, success = true, skipped = true });
                        continue;
                    }

                    Io.Directory.CreateDirectory(decryptedDir);
                    DecryptImageRows(srcPath, outPath, config);
                    results.Add(new { fileName, outputPath = outPath, success = true, skipped = false });
                }
                catch (Exception ex)
                {
                    results.Add(new { fileName, outputPath = outPath, success = false, error = ex.Message });
                }
            }

            return Ok(new { success = true, data = new { results, total = files.Length } });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DecryptBatch failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// 获取漫画图片代理（优先返回解密后的图片）
    /// </summary>
    [HttpGet("image/{chapterId}/{fileName}")]
    public IActionResult GetImage(string chapterId, string fileName)
    {
        try
        {
            using var conn = GetConnection();
            conn.Open();

            string? chapterDir = null;
            using (var chCmd = new SqliteCommand("SELECT directory FROM comic_chapters WHERE id = @id", conn))
            {
                chCmd.Parameters.Add(new SqliteParameter("@id", chapterId));
                var result = chCmd.ExecuteScalar();
                if (result == null || result == DBNull.Value)
                    return NotFound(new { success = false, message = "章节不存在" });
                chapterDir = result.ToString();
            }

            var decryptedDir = Io.Path.Combine(chapterDir!, "_decrypted");
            var baseName = Io.Path.GetFileNameWithoutExtension(fileName);
            string? imagePath = null;

            var decryptedPath = Io.Path.Combine(decryptedDir, baseName + ".jpg");
            if (Io.File.Exists(decryptedPath))
                imagePath = decryptedPath;
            else
            {
                var originalPath = Io.Path.Combine(chapterDir!, fileName);
                if (Io.File.Exists(originalPath))
                    imagePath = originalPath;
            }

            if (imagePath == null || !Io.File.Exists(imagePath))
                return NotFound(new { success = false, message = "图片不存在" });

            var bytes = Io.File.ReadAllBytes(imagePath);
            var ext = Io.Path.GetExtension(imagePath).ToLower();
            var contentType = ext switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".bmp" => "image/bmp",
                _ => "application/octet-stream"
            };

            return PhysicalFile(imagePath, contentType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetImage failed");
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    // ========== 核心解密算法 ==========

    /// <summary>
    /// 解密图片：将打乱的行重新按正确顺序拼接为完整图片并保存为 JPG
    /// 加密过程：原图按 Rows 等分行后按 Order 数组打乱
    /// 解密过程：根据 Order 逆序还原
    /// Order[i]=j 表示：加密图第 i 行来自原图第 j 行
    /// 还原时：reversed[j]=i，即输出第 j 行来自加密图第 i 行
    /// </summary>
    private void DecryptImageRows(string srcPath, string outPath, Models.ComicDecryptConfig config)
    {
        int rows = config.Rows > 0 ? config.Rows : 3;
        if (config.Order == null || config.Order.Count < rows)
            rows = 1;

        // 构建逆序映射
        var reversedOrder = new int[rows];
        for (int i = 0; i < rows; i++) reversedOrder[i] = -1;
        for (int i = 0; i < Math.Min(config.Order?.Count ?? 0, rows); i++)
        {
            int targetRow = config.Order![i];
            if (targetRow >= 0 && targetRow < rows)
                reversedOrder[targetRow] = i;
        }
        int next = 0;
        for (int i = 0; i < rows; i++)
            if (reversedOrder[i] == -1)
                reversedOrder[i] = next++;

        using var img = Image.Load<Rgba32>(srcPath);
        int fullHeight = img.Height;
        int rowHeight = fullHeight / rows;
        int remainder = fullHeight % rows;

        using var output = new Image<Rgba32>(img.Width, fullHeight);

        // 逐行处理
        for (int outRow = 0; outRow < rows; outRow++)
        {
            // 找出输出第 outRow 行应从加密图的哪一行取值
            int srcSlice = reversedOrder[outRow];

            // 计算源行起始Y
            int srcY = 0;
            for (int k = 0; k < srcSlice; k++)
                srcY += rowHeight + (k < remainder ? 1 : 0);
            int sliceH = rowHeight + (srcSlice < remainder ? 1 : 0);

            // 计算目标行起始Y
            int dstY = 0;
            for (int k = 0; k < outRow; k++)
                dstY += rowHeight + (k < remainder ? 1 : 0);

            // 从原图裁剪一行，绘制到输出图
            using var slice = img.Clone(ctx => ctx
                .Crop(new Rectangle(0, srcY, img.Width, sliceH))
                .Resize(img.Width, sliceH));
            output.Mutate(ctx => ctx.DrawImage(slice, new Point(0, dstY), 1f));
        }

        output.SaveAsJpeg(outPath);
    }

    // ========== 辅助方法 ==========

    private object ReadComicRow(SqliteDataReader reader)
    {
        return new
        {
            id = reader["id"].ToString(),
            name = reader["name"].ToString(),
            author = reader["author"] == DBNull.Value ? null : reader["author"].ToString(),
            description = reader["description"] == DBNull.Value ? null : reader["description"].ToString(),
            url = reader["url"] == DBNull.Value ? null : reader["url"].ToString(),
            coverPath = reader["cover_path"] == DBNull.Value ? null : reader["cover_path"].ToString(),
            directory = reader["directory"] == DBNull.Value ? null : reader["directory"].ToString(),
            chapterCount = Convert.ToInt32(reader["chapter_count"]),
            ctime = reader["ctime"].ToString(),
            utime = reader["utime"] == DBNull.Value ? null : reader["utime"].ToString()
        };
    }

    private object ExecuteScalar(string sql, SqliteParameter[] parameters)
    {
        using var conn = GetConnection();
        conn.Open();
        using var cmd = new SqliteCommand(sql, conn);
        if (parameters != null)
            cmd.Parameters.AddRange(parameters);
        return cmd.ExecuteScalar() ?? DBNull.Value;
    }
}

/// <summary>
/// 添加章节请求
/// </summary>
public class AddChapterRequest
{
    public string? Title { get; set; }
    public string? Directory { get; set; }
}

/// <summary>
/// 自然排序比较器（用于图片按文件名数字排序）
/// </summary>
public class NaturalStringComparer : IComparer<string>
{
    public int Compare(string? x, string? y)
    {
        if (x == null && y == null) return 0;
        if (x == null) return -1;
        if (y == null) return 1;

        int ix = 0, iy = 0;
        while (ix < x.Length && iy < y.Length)
        {
            if (char.IsDigit(x[ix]) && char.IsDigit(y[iy]))
            {
                long nx = 0, ny = 0;
                while (ix < x.Length && char.IsDigit(x[ix])) { nx = nx * 10 + (x[ix] - '0'); ix++; }
                while (iy < y.Length && char.IsDigit(y[iy])) { ny = ny * 10 + (y[iy] - '0'); iy++; }
                if (nx != ny) return nx.CompareTo(ny);
            }
            else
            {
                int cmp = x[ix].CompareTo(y[iy]);
                if (cmp != 0) return cmp;
                ix++; iy++;
            }
        }
        return x.Length.CompareTo(y.Length);
    }
}
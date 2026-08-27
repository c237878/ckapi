using Microsoft.Data.Sqlite;

namespace ckapi.Services;

/// <summary>
/// 数据服务接口
/// </summary>
public interface IDataService
{
    /// <summary>
    /// 初始化数据库和表结构
    /// </summary>
    void Initialize();
}

/// <summary>
/// 数据服务实现 - 负责项目启动时的数据库初始化
/// </summary>
public class DataService : IDataService
{
    private readonly ILogger<DataService> _logger;
    private readonly Utils.SQLiteHelper _db;
    private readonly IConfiguration _config;

    public DataService(ILogger<DataService> logger, Utils.SQLiteHelper db, IConfiguration config)
    {
        _logger = logger;
        _db = db;
        _config = config;
    }

    /// <summary>
    /// 初始化数据库和表结构
    /// </summary>
    public void Initialize()
    {
        _logger.LogInformation("开始初始化数据库...");

        try
        {
            CreateVideoTable();
            CreateVideoSeriesTable();
            CreateActorTable();
            CreateVideoActorTable();
            CreateSystemSettingsTable();
            CreateFriendLinksTable();
            CreateScanDirectoryTable();
            CreateVideoTypeTable();
            CreateComicTables();

            _logger.LogInformation("数据库初始化完成，数据库路径: {DbPath}", _db.GetDbPath());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "数据库初始化失败");
            throw;
        }
    }

    /// <summary>
    /// 创建影片表
    /// </summary>
    private void CreateVideoTable()
    {
        const string tableName = "videos";
        const string fieldStr = @"
            id          TEXT    NOT NULL    PRIMARY KEY,
            name        TEXT    NOT NULL,
            code        TEXT,
            category    TEXT    NOT NULL,
            country     TEXT,
            file_path   TEXT,
            file_size   INTEGER,
            cover_path  TEXT,
            ctime       TEXT,
            seriesid    TEXT,
            media_attr_flags INTEGER DEFAULT 0,
            sort_order  INTEGER DEFAULT 0
        ";

        if (!_db.TableExists(tableName))
        {
            _db.CreateTable(tableName, fieldStr);
            _logger.LogInformation("表 [{TableName}] 创建成功", tableName);
        }
        else
        {
            _logger.LogInformation("表 [{TableName}] 已存在，跳过创建", tableName);
            // 添加迁移逻辑
            MigrateVideoTable();
        }
    }

    private void MigrateVideoTable()
    {
        try
        {
            var columns = _db.ExecuteDataTable("PRAGMA table_info(videos)");
            var columnNames = new List<string>();
            foreach (System.Data.DataRow row in columns.Rows)
            {
                columnNames.Add(row["name"].ToString()?.ToLower() ?? "");
            }

            // 添加 name 列（旧表可能还是 title）
            if (!columnNames.Contains("name") && columnNames.Contains("title"))
            {
                _db.ExecuteNonQuery("ALTER TABLE videos RENAME COLUMN title TO name");
                _logger.LogInformation("重命名 title 列为 name");
            }

            // 添加 code 列
            if (!columnNames.Contains("code"))
            {
                _db.ExecuteNonQuery("ALTER TABLE videos ADD COLUMN code TEXT");
                _logger.LogInformation("添加 code 列到 videos 表");
            }

            // 添加 seriesid 列
            if (!columnNames.Contains("seriesid"))
            {
                _db.ExecuteNonQuery("ALTER TABLE videos ADD COLUMN seriesid TEXT");
                _logger.LogInformation("添加 seriesid 列到 videos 表");
            }

            // 添加 media_attr_flags 列
            if (!columnNames.Contains("media_attr_flags"))
            {
                _db.ExecuteNonQuery("ALTER TABLE videos ADD COLUMN media_attr_flags INTEGER DEFAULT 0");
                _logger.LogInformation("添加 media_attr_flags 列到 videos 表");
            }

            // 添加 sort_order 列
            if (!columnNames.Contains("sort_order"))
            {
                _db.ExecuteNonQuery("ALTER TABLE videos ADD COLUMN sort_order INTEGER DEFAULT 0");
                _logger.LogInformation("添加 sort_order 列到 videos 表");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "迁移videos表失败");
        }
    }

    /// <summary>
    /// 迁移actors表结构
    /// </summary>
    private void MigrateActorTable()
    {
        try
        {
            var columns = _db.ExecuteDataTable("PRAGMA table_info(actors)");
            var columnNames = new List<string>();
            foreach (System.Data.DataRow row in columns.Rows)
            {
                columnNames.Add(row["name"].ToString()?.ToLower() ?? "");
            }

            // 添加 alias 列
            if (!columnNames.Contains("alias"))
            {
                _db.ExecuteNonQuery("ALTER TABLE actors ADD COLUMN alias TEXT");
                _logger.LogInformation("添加 alias 列到 actors 表");
            }

            // 添加 country 列
            if (!columnNames.Contains("country"))
            {
                _db.ExecuteNonQuery("ALTER TABLE actors ADD COLUMN country TEXT");
                _logger.LogInformation("添加 country 列到 actors 表");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "迁移actors表失败");
        }
    }

    /// <summary>
    /// 创建文件目录表
    /// </summary>
    private void CreateScanDirectoryTable()
    {
        const string tableName = "scan_directories";
        const string fieldStr = @"
            id          TEXT    NOT NULL    PRIMARY KEY,
            path        TEXT    NOT NULL,
            category    TEXT    DEFAULT '',
            recursive   INTEGER DEFAULT 1,
            ctime       TEXT    NOT NULL,
            utime       TEXT    NOT NULL
        ";

        if (!_db.TableExists(tableName))
        {
            _db.CreateTable(tableName, fieldStr);
            _logger.LogInformation("表 [{TableName}] 创建成功", tableName);
        }
        else
        {
            _logger.LogInformation("表 [{TableName}] 已存在，跳过创建", tableName);
        }
    }

    /// <summary>
    /// 创建视频类型表
    /// </summary>
    private void CreateVideoTypeTable()
    {
        const string tableName = "video_types";
        const string fieldStr = @"
            id          TEXT    NOT NULL    PRIMARY KEY,
            name        TEXT    NOT NULL UNIQUE,
            extensions  TEXT    NOT NULL,
            sort_order  INTEGER DEFAULT 0,
            ctime       TEXT    NOT NULL,
            utime       TEXT    NOT NULL
        ";

        if (!_db.TableExists(tableName))
        {
            _db.CreateTable(tableName, fieldStr);
            _logger.LogInformation("表 [{TableName}] 创建成功", tableName);
            // 初始化默认类型
            InitDefaultVideoTypes();
        }
        else
        {
            _logger.LogInformation("表 [{TableName}] 已存在，跳过创建", tableName);
        }
    }

    private void InitDefaultVideoTypes()
    {
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var defaults = new (string name, string ext)[]
        {
            ("mp4", ".mp4"),
            ("mkv", ".mkv"),
            ("avi", ".avi"),
            ("mov", ".mov")
        };
        foreach (var (name, ext) in defaults)
        {
            try
            {
                _db.ExecuteNonQuery(@"
                    INSERT OR IGNORE INTO video_types (id, name, extensions, sort_order, ctime, utime)
                    VALUES (@id, @name, @ext, @sort, @ctime, @utime)",
                    new SqliteParameter("@id", Guid.NewGuid().ToString("N").ToUpper()),
                    new SqliteParameter("@name", name),
                    new SqliteParameter("@ext", ext),
                    new SqliteParameter("@sort_order", 0),
                    new SqliteParameter("@ctime", now),
                    new SqliteParameter("@utime", now));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "初始化视频类型 {name} 失败", name);
            }
        }
    }

    /// <summary>
    /// 创建影视系列表
    /// </summary>
    private void CreateVideoSeriesTable()
    {
        const string tableName = "video_series";
        const string fieldStr = @"
            id          TEXT    NOT NULL    PRIMARY KEY,
            name        TEXT    NOT NULL,
            alias       TEXT,
            link        TEXT,
            country     TEXT,
            ctime       TEXT    NOT NULL,
            utime       TEXT    NOT NULL
        ";

        if (!_db.TableExists(tableName))
        {
            _db.CreateTable(tableName, fieldStr);
            _logger.LogInformation("表 [{TableName}] 创建成功", tableName);
        }
        else
        {
            _logger.LogInformation("表 [{TableName}] 已存在，跳过创建", tableName);
        }
    }

    /// <summary>
    /// 创建演员表
    /// </summary>
    private void CreateActorTable()
    {
        const string tableName = "actors";
        const string fieldStr = @"
            id          TEXT    NOT NULL    PRIMARY KEY,
            name        TEXT    NOT NULL UNIQUE,
            alias       TEXT,
            country     TEXT,
            avatar_path TEXT,
            bio         TEXT,
            ctime       TEXT    NOT NULL
        ";

        if (!_db.TableExists(tableName))
        {
            _db.CreateTable(tableName, fieldStr);
            _logger.LogInformation("表 [{TableName}] 创建成功", tableName);
        }
        else
        {
            _logger.LogInformation("表 [{TableName}] 已存在，跳过创建", tableName);
            MigrateActorTable();
        }
    }

    /// <summary>
    /// 创建影片-演员关联表
    /// </summary>
    private void CreateVideoActorTable()
    {
        const string tableName = "video_actors";
        const string fieldStr = @"
            id          TEXT    NOT NULL    PRIMARY KEY,
            video_id    TEXT    NOT NULL,
            actor_id    TEXT    NOT NULL,
            ctime       TEXT    NOT NULL
        ";

        if (!_db.TableExists(tableName))
        {
            _db.CreateTable(tableName, fieldStr);
            _logger.LogInformation("表 [{TableName}] 创建成功", tableName);
        }
        else
        {
            _logger.LogInformation("表 [{TableName}] 已存在，跳过创建", tableName);
        }
    }

    /// <summary>
    /// 创建系统设置表
    /// </summary>
    private void CreateSystemSettingsTable()
    {
        const string tableName = "system_settings";
        const string fieldStr = @"
            id          TEXT    NOT NULL    PRIMARY KEY,
            name        TEXT    NOT NULL,
            content     TEXT,
            ctime       TEXT    NOT NULL,
            utime       TEXT    NOT NULL
        ";

        if (!_db.TableExists(tableName))
        {
            _db.CreateTable(tableName, fieldStr);
            _logger.LogInformation("表 [{TableName}] 创建成功", tableName);
        }
        else
        {
            _logger.LogInformation("表 [{TableName}] 已存在，跳过创建", tableName);
        }
    }

    /// <summary>
    /// 创建友情链接表
    /// </summary>
    private void CreateFriendLinksTable()
    {
        const string tableName = "friend_links";
        const string fieldStr = @"
            id          TEXT    NOT NULL    PRIMARY KEY,
            name        TEXT    NOT NULL,
            link        TEXT    NOT NULL,
            logo        TEXT,
            description TEXT,
            sortorder   INTEGER DEFAULT 0,
            ctime       TEXT    NOT NULL,
            utime       TEXT    NOT NULL
        ";

        if (!_db.TableExists(tableName))
        {
            _db.CreateTable(tableName, fieldStr);
            _logger.LogInformation("表 [{TableName}] 创建成功", tableName);
        }
        else
        {
            _logger.LogInformation("表 [{TableName}] 已存在，跳过创建", tableName);
        }
    }



    /// <summary>
    /// 创建漫画相关表
    /// </summary>
    private void CreateComicTables()
    {
        // 漫画表
        const string comicsTable = "comics";
        const string comicsFields = @"
            id          TEXT    NOT NULL    PRIMARY KEY,
            name        TEXT    NOT NULL,
            author      TEXT    DEFAULT '',
            description TEXT    DEFAULT '',
            url         TEXT    DEFAULT '',
            cover_path  TEXT    DEFAULT '',
            directory   TEXT    DEFAULT '',
            ctime       TEXT    NOT NULL,
            utime       TEXT    NOT NULL
        ";
        if (!_db.TableExists(comicsTable))
        {
            _db.CreateTable(comicsTable, comicsFields);
            _logger.LogInformation("表 [{TableName}] 创建成功", comicsTable);
        }
        else
        {
            _logger.LogInformation("表 [{TableName}] 已存在，跳过创建", comicsTable);
        }

        // 漫画章节表
        const string chaptersTable = "comic_chapters";
        const string chaptersFields = @"
            id          TEXT    NOT NULL    PRIMARY KEY,
            comic_id    TEXT    NOT NULL,
            title       TEXT    NOT NULL,
            directory   TEXT    DEFAULT '',
            sort_order  INTEGER DEFAULT 0,
            image_count INTEGER DEFAULT 0,
            ctime       TEXT    NOT NULL,
            utime       TEXT
        ";
        if (!_db.TableExists(chaptersTable))
        {
            _db.CreateTable(chaptersTable, chaptersFields);
            _logger.LogInformation("表 [{TableName}] 创建成功", chaptersTable);
        }
        else
        {
            // 表已存在，补充缺失的 utime 列（向后兼容）
            try
            {
                using var conn = new SqliteConnection(_config.GetConnectionString("DefaultConnection"));
                conn.Open();
                using var checkCmd = new SqliteCommand("PRAGMA table_info(comic_chapters)", conn);
                using var reader = checkCmd.ExecuteReader();
                var columns = new HashSet<string>();
                while (reader.Read()) columns.Add(reader["name"].ToString()!);
                reader.Close();
                if (!columns.Contains("utime"))
                {
                    using var alterCmd = new SqliteCommand("ALTER TABLE comic_chapters ADD COLUMN utime TEXT", conn);
                    alterCmd.ExecuteNonQuery();
                    _logger.LogInformation("表 [comic_chapters] 已补充 utime 列");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "补充 comic_chapters.utime 列失败（可能已存在）");
            }
        }
    }
}
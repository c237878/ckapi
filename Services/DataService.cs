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

    public DataService(ILogger<DataService> logger, Utils.SQLiteHelper db)
    {
        _logger = logger;
        _db = db;
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
            CreateLikeRecordTable();
            CreateSystemSettingTable();
            CreateFriendLinkTable();
            CreateScanDirectoryTable();
            CreateVideoTypeTable();

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
            title       TEXT    NOT NULL,
            code        TEXT,
            year        INTEGER,
            category    TEXT    NOT NULL,
            country     TEXT,
            file_path   TEXT    UNIQUE NOT NULL,
            file_size   INTEGER,
            cover_path  TEXT,
            has_cover   INTEGER DEFAULT 0,
            added_at    TEXT    NOT NULL,
            note        TEXT,
            seriesid    TEXT
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
    /// 创建扫描目录表
    /// </summary>
    private void CreateScanDirectoryTable()
    {
        const string tableName = "scan_directories";
        const string fieldStr = @"
            id          TEXT    NOT NULL    PRIMARY KEY,
            path        TEXT    NOT NULL,
            video_types TEXT    NOT NULL,
            recursive   INTEGER DEFAULT 1,
            is_enabled  INTEGER DEFAULT 1,
            created_at  TEXT    NOT NULL,
            updated_at  TEXT    NOT NULL
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
            created_at  TEXT    NOT NULL,
            updated_at  TEXT    NOT NULL
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
                    INSERT OR IGNORE INTO video_types (id, name, extensions, sort_order, created_at, updated_at)
                    VALUES (@id, @name, @ext, @sort, @ctime, @utime)",
                    new SqliteParameter("@id", Guid.NewGuid().ToString()),
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
        const string tableName = "VideoSeries";
        const string fieldStr = @"
            id          TEXT    NOT NULL    PRIMARY KEY,
            name        TEXT    NOT NULL,
            alias       TEXT,
            link        TEXT,
            country     TEXT,
            created_at  TEXT    NOT NULL,
            updated_at  TEXT    NOT NULL
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
            added_at    TEXT    NOT NULL
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
            created_at  TEXT    NOT NULL
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
    /// 创建点赞记录表
    /// </summary>
    private void CreateLikeRecordTable()
    {
        const string tableName = "LikeRecord";
        const string fieldStr = @"
            id          TEXT    NOT NULL    PRIMARY KEY,
            videoid     TEXT    NOT NULL,
            liketime    TEXT    NOT NULL,
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
    /// 创建系统设置表
    /// </summary>
    private void CreateSystemSettingTable()
    {
        const string tableName = "SystemSetting";
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
    private void CreateFriendLinkTable()
    {
        const string tableName = "FriendLink";
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


}

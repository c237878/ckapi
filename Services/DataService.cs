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
            CreateVideosTable();
            CreateActorsTable();
            CreateVideoActorsTable();
            CreateVideoLikesTable();
            CreateScanTasksTable();
            CreateHighlightsTable();
            CreateLikeRecordsTable();
            CreateSystemSettingsTable();
            CreateFriendLinksTable();
            CreateVideoSeriesTable();

            _logger.LogInformation("数据库初始化完成，数据库路径: {DbPath}", _db.GetDbPath());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "数据库初始化失败");
            throw;
        }
    }

    /// <summary>
    /// 创建影片表（videos）
    /// </summary>
    private void CreateVideosTable()
    {
        const string tableName = "videos";
        const string fieldStr = @"
            id                  TEXT    NOT NULL    PRIMARY KEY,
            name                TEXT,
            category            TEXT,
            file_path           TEXT,
            file_size           INTEGER,
            cover_path          TEXT,
            code                TEXT,
            country             TEXT,
            seriesid            TEXT,
            added_at            TEXT,
            ctime               TEXT,
            utime               TEXT,
            media_attr_flags    INTEGER DEFAULT 0,
            sort_order          INTEGER DEFAULT 0
        ";

        if (!_db.TableExists(tableName))
        {
            _db.CreateTable(tableName, fieldStr);
            _logger.LogInformation("表 [{TableName}] 创建成功", tableName);
        }
        else
        {
            _logger.LogInformation("表 [{TableName}] 已存在，跳过创建");
            MigrateVideosTable();
        }
    }

    private void MigrateVideosTable()
    {
        // 原始 ckplayer.db 的 videos 表已存在且有数据，无需迁移
    }

    /// <summary>
    /// 创建演员表（actors）
    /// </summary>
    private void CreateActorsTable()
    {
        const string tableName = "actors";
        const string fieldStr = @"
            id          TEXT    NOT NULL    PRIMARY KEY,
            name        TEXT    NOT NULL,
            avatar_path TEXT,
            bio         TEXT,
            ctime       TEXT,
            alias       TEXT,
            country     TEXT
        ";

        if (!_db.TableExists(tableName))
        {
            _db.CreateTable(tableName, fieldStr);
            _logger.LogInformation("表 [{TableName}] 创建成功", tableName);
        }
        else
        {
            _logger.LogInformation("表 [{TableName}] 已存在，跳过创建");
        }
    }

    /// <summary>
    /// 创建影片-演员关联表（video_actors）
    /// </summary>
    private void CreateVideoActorsTable()
    {
        const string tableName = "video_actors";
        const string fieldStr = @"
            video_id    TEXT    NOT NULL,
            actor_id    TEXT    NOT NULL,
            PRIMARY KEY (video_id, actor_id)
        ";

        if (!_db.TableExists(tableName))
        {
            _db.CreateTable(tableName, fieldStr);
            _logger.LogInformation("表 [{TableName}] 创建成功", tableName);
        }
        else
        {
            _logger.LogInformation("表 [{TableName}] 已存在，跳过创建");
        }
    }

    /// <summary>
    /// 创建点赞表（video_likes）
    /// </summary>
    private void CreateVideoLikesTable()
    {
        const string tableName = "video_likes";
        const string fieldStr = @"
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            video_id    TEXT    NOT NULL,
            liked_at    TEXT
        ";

        if (!_db.TableExists(tableName))
        {
            _db.CreateTable(tableName, fieldStr);
            _logger.LogInformation("表 [{TableName}] 创建成功", tableName);
        }
        else
        {
            _logger.LogInformation("表 [{TableName}] 已存在，跳过创建");
        }
    }

    /// <summary>
    /// 创建扫描任务表（scan_tasks）
    /// </summary>
    private void CreateScanTasksTable()
    {
        const string tableName = "scan_tasks";
        const string fieldStr = @"
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            name            TEXT,
            target_path     TEXT,
            file_extensions TEXT,
            recursive       INTEGER DEFAULT 1,
            status          TEXT,
            started_at      TEXT,
            completed_at    TEXT,
            files_found     INTEGER DEFAULT 0,
            files_added     INTEGER DEFAULT 0,
            files_updated   INTEGER DEFAULT 0,
            files_skipped   INTEGER DEFAULT 0,
            errors          TEXT
        ";

        if (!_db.TableExists(tableName))
        {
            _db.CreateTable(tableName, fieldStr);
            _logger.LogInformation("表 [{TableName}] 创建成功", tableName);
        }
        else
        {
            _logger.LogInformation("表 [{TableName}] 已存在，跳过创建");
        }
    }

    /// <summary>
    /// 创建高光表（highlights）
    /// </summary>
    private void CreateHighlightsTable()
    {
        const string tableName = "highlights";
        const string fieldStr = @"
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            video_id    TEXT,
            title       TEXT,
            image       TEXT,
            ctime       TEXT,
            utime       TEXT
        ";

        if (!_db.TableExists(tableName))
        {
            _db.CreateTable(tableName, fieldStr);
            _logger.LogInformation("表 [{TableName}] 创建成功", tableName);
        }
        else
        {
            _logger.LogInformation("表 [{TableName}] 已存在，跳过创建");
        }
    }

    /// <summary>
    /// 创建点赞记录表（like_records）
    /// </summary>
    private void CreateLikeRecordsTable()
    {
        // LikeController 使用 like_records 表
        // 表结构和原始 video_likes 不同，用于收藏功能
        const string tableName = "like_records";
        const string fieldStr = @"
            id          TEXT    NOT NULL    PRIMARY KEY,
            video_id    TEXT    NOT NULL,
            like_time   TEXT    NOT NULL,
            user_token  TEXT,
            ctime       TEXT,
            utime       TEXT
        ";

        if (!_db.TableExists(tableName))
        {
            _db.CreateTable(tableName, fieldStr);
            _logger.LogInformation("表 [{TableName}] 创建成功", tableName);
        }
        else
        {
            _logger.LogInformation("表 [{TableName}] 已存在，跳过创建");
        }
    }

    /// <summary>
    /// 创建系统设置表（system_settings）
    /// </summary>
    private void CreateSystemSettingsTable()
    {
        const string tableName = "system_settings";
        const string fieldStr = @"
            id          TEXT    NOT NULL    PRIMARY KEY,
            name        TEXT    NOT NULL,
            content     TEXT,
            ctime       TEXT,
            utime       TEXT
        ";

        if (!_db.TableExists(tableName))
        {
            _db.CreateTable(tableName, fieldStr);
            _logger.LogInformation("表 [{TableName}] 创建成功", tableName);
        }
        else
        {
            _logger.LogInformation("表 [{TableName}] 已存在，跳过创建");
        }
    }

    /// <summary>
    /// 创建友链表（friend_links）
    /// </summary>
    private void CreateFriendLinksTable()
    {
        const string tableName = "friend_links";
        const string fieldStr = @"
            id          TEXT    NOT NULL    PRIMARY KEY,
            name        TEXT,
            link        TEXT,
            logo        TEXT,
            description TEXT,
            sortorder   INTEGER DEFAULT 0,
            ctime       TEXT,
            utime       TEXT
        ";

        if (!_db.TableExists(tableName))
        {
            _db.CreateTable(tableName, fieldStr);
            _logger.LogInformation("表 [{TableName}] 创建成功", tableName);
        }
        else
        {
            _logger.LogInformation("表 [{TableName}] 已存在，跳过创建");
        }
    }

    /// <summary>
    /// 创建系列表（video_series）
    /// </summary>
    private void CreateVideoSeriesTable()
    {
        const string tableName = "video_series";
        const string fieldStr = @"
            id          TEXT    NOT NULL    PRIMARY KEY,
            name        TEXT,
            alias       TEXT,
            link        TEXT,
            country     TEXT,
            ctime       TEXT,
            utime       TEXT
        ";

        if (!_db.TableExists(tableName))
        {
            _db.CreateTable(tableName, fieldStr);
            _logger.LogInformation("表 [{TableName}] 创建成功", tableName);
        }
        else
        {
            _logger.LogInformation("表 [{TableName}] 已存在，跳过创建");
        }
    }
}

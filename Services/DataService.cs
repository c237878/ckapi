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
            CreateHighlightTable();
            CreateLikeRecordTable();
            CreateSystemSettingTable();
            CreateFriendLinkTable();

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
        const string tableName = "Video";
        const string fieldStr = @"
            id          TEXT    NOT NULL    PRIMARY KEY,
            code        TEXT,
            name        TEXT    NOT NULL,
            country     TEXT,
            coverurl    TEXT,
            videourl    TEXT,
            videosize   INTEGER,
            quality     TEXT,
            seriesid    TEXT,
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
            // 添加迁移逻辑
            MigrateVideoTable();
        }
    }

    /// <summary>
    /// 迁移Video表结构
    /// </summary>
    private void MigrateVideoTable()
    {
        try
        {
            var columns = _db.ExecuteDataTable("PRAGMA table_info(Video)");
            bool hasCoverUrl = false;
            bool hasVideoUrl = false;
            bool hasHostUrl = false;

            foreach (System.Data.DataRow row in columns.Rows)
            {
                var colName = row["name"].ToString();
                if (colName == "coverurl") hasCoverUrl = true;
                if (colName == "videourl") hasVideoUrl = true;
                if (colName == "hosturl") hasHostUrl = true;
            }

            // 添加coverurl列
            if (!hasCoverUrl)
            {
                _db.ExecuteNonQuery("ALTER TABLE Video ADD COLUMN coverurl TEXT");
                _logger.LogInformation("添加 coverurl 列到 Video 表");
            }

            // 添加videourl列
            if (!hasVideoUrl)
            {
                _db.ExecuteNonQuery("ALTER TABLE Video ADD COLUMN videourl TEXT");
                _logger.LogInformation("添加 videourl 列到 Video 表");
            }

            // 如果有旧的hosturl列，迁移数据
            if (hasHostUrl && !hasVideoUrl)
            {
                try
                {
                    _db.ExecuteNonQuery("UPDATE Video SET videourl = hosturl WHERE videourl IS NULL AND hosturl IS NOT NULL");
                    _logger.LogInformation("迁移 hosturl 数据到 videourl");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "迁移 hosturl 数据失败");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "迁移Video表失败");
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
        const string tableName = "Actor";
        const string fieldStr = @"
            id          TEXT    NOT NULL    PRIMARY KEY,
            name        TEXT    NOT NULL,
            alias       TEXT,
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
    /// 创建影片-演员关联表
    /// </summary>
    private void CreateVideoActorTable()
    {
        const string tableName = "VideoActor";
        const string fieldStr = @"
            id          TEXT    NOT NULL    PRIMARY KEY,
            videoid     TEXT    NOT NULL,
            actorid     TEXT    NOT NULL,
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
    /// 创建精彩集锦表
    /// </summary>
    private void CreateHighlightTable()
    {
        const string tableName = "Highlight";
        const string fieldStr = @"
            id          TEXT    NOT NULL    PRIMARY KEY,
            image       TEXT,
            actorid     TEXT,
            videoid     TEXT,
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

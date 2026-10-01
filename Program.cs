using System.IO.Compression;
using System.Reflection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.FileProviders;
using ckapi.Services;
using Microsoft.Extensions.Configuration;

var builder = WebApplication.CreateBuilder(args);

// 配置 Kestrel 支持大文件上传（无限制）
var serverPort = builder.Configuration.GetValue<int?>("ServerPort") ?? 5033;
builder.WebHost.ConfigureKestrel(options => {
    options.Limits.MaxRequestBodySize = null; // 无限制
    options.ListenAnyIP(serverPort);
});

// 解除 multipart 上传大小限制
builder.Services.Configure<FormOptions>(options => {
    options.MultipartBodyLengthLimit = long.MaxValue;
});

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddMemoryCache();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "影视网站 API", Version = "v1", Description = "影视网站后端接口文档" });
    // 读取所有控制器的 XML 注释文档
    var xmlFilename = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFilename);
    if (File.Exists(xmlPath))
        c.IncludeXmlComments(xmlPath);
});

// 注册数据库服务
builder.Services.AddSingleton<ckapi.Utils.SQLiteHelper>(sp =>
    new ckapi.Utils.SQLiteHelper(sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<ILogger<ckapi.Utils.SQLiteHelper>>()));
builder.Services.AddScoped<IDataService, DataService>();
// av-wiki 抓取：Scraper 无状态、Job 持有进程内进度，都只需一个实例
builder.Services.AddSingleton<ckapi.Services.ActorScraper>();
// 老师图鉴（laoshi.ink）：只补生日/别名/头像，与 av-wiki 共用同一套门槛与礼貌策略
builder.Services.AddSingleton<ckapi.Services.LaoshiScraper>();
builder.Services.AddSingleton<ckapi.Services.IActorSource>(sp => sp.GetRequiredService<ckapi.Services.ActorScraper>());
builder.Services.AddSingleton<ckapi.Services.IActorSource>(sp => sp.GetRequiredService<ckapi.Services.LaoshiScraper>());
builder.Services.AddSingleton<ckapi.Services.ScrapeJob>();
// 片源扫描（分辨率 + 字幕证据）：同上
builder.Services.AddSingleton<ckapi.Services.SourceScanner>();
builder.Services.AddSingleton<ckapi.Services.SourceScanJob>();
// 抓取通道：配置与礼貌策略（限速/配额/熔断）都在 scrape_channels 表里，界面可改
builder.Services.AddSingleton<ckapi.Services.ScrapeChannelService>();
// 规则通道的批量补数据：进度是进程内状态，所以必须是单例（面板轮询的要和跑任务的是同一个实例）
builder.Services.AddSingleton<ckapi.Services.ChannelBatch>();
// MCP：给本机大模型的工具接口，密钥与打标口径都存在 system_settings
builder.Services.AddSingleton<ckapi.Services.McpService>();
// 每日常规备份 + 轮转：进程长期不重启也得每天有份快照（详见 BackupService 注释）
// 面板要读要写的是"那个正在计时的实例"，所以先注册单例再用工厂挂成 hosted service
builder.Services.AddSingleton<ckapi.Services.BackupService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ckapi.Services.BackupService>());
// 每日自动增量扫描 + 每日盘上核对：同样跟着进程走，不靠外部 cron（详见 ScanScheduleService 注释）
// 先注册单例再用工厂挂成 hosted service：面板要读的是"那个正在计时的实例"的结果，
// 只写 AddHostedService 的话控制器按具体类型根本解析不到（没有对应的描述符）
builder.Services.AddSingleton<ckapi.Services.ScanScheduleService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ckapi.Services.ScanScheduleService>());
// 破坏性接口的管理口令门禁（设置-安全里开关；没设口令时不拦）
builder.Services.AddScoped<ckapi.Utils.AdminTokenFilter>();

// 配置CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", builder =>
    {
        builder.AllowAnyOrigin()
               .AllowAnyMethod()
               .AllowAnyHeader();
    });
});

// 响应压缩：弱网（手机热点、远程）下最贵的是字节数，而 JS/CSS/JSON 正好是可压的那几类。
// 实测列表页那一页 JSON 16.7KB → 约 3KB，vendor.js 102KB → 40KB。
// 图片与视频不在这儿：JPEG/MP4 再压一遍只是白花 CPU（Brotli 默认 MimeTypes 里也没有它们）。
builder.Services.AddResponseCompression(options =>
{
    // 我们是纯 HTTP（5033），这行只是不留坑：将来挂上 TLS 反代时压缩不会静默失效
    options.EnableForHttps = true;
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(new[]
    {
        "text/javascript", "application/javascript", "application/ecmascript", "application/manifest+json"
    });
});
builder.Services.Configure<BrotliCompressionProviderOptions>(
    o => o.Level = CompressionLevel.Fastest);
builder.Services.Configure<GzipCompressionProviderOptions>(
    o => o.Level = CompressionLevel.Fastest);

var app = builder.Build();

// 初始化数据库
using (var scope = app.Services.CreateScope())
{
    var dataService = scope.ServiceProvider.GetRequiredService<IDataService>();
    dataService.Initialize();
}

// Configure the HTTP request pipeline.
var isDev = app.Environment.IsDevelopment() ||
             app.Environment.EnvironmentName.Equals("dev", StringComparison.OrdinalIgnoreCase);
if (isDev)
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("AllowAll");
// 压缩要在静态文件与控制器之前挂上，否则 JS/CSS 与接口 JSON 都错过它
app.UseResponseCompression();
// 注意：服务只监听 HTTP（5033），不要启用 UseHttpsRedirection，否则会把请求 307 到无人监听的 HTTPS 端口
app.UseRouting();

// 前端产物托管。为什么要它：弱网下首屏最贵的不是查询，而是请求数——
// vite 开发服务（3001）一个模块一个请求，打开影片列表要发 59 个、其中 49 个是未打包未压缩的 JS 共 416KB；
// 打包后的 dist 只有 12 个请求、约 175KB，再经上面那层压缩剩 55KB。
// 3001 那个开发服务照旧留着改代码用，这里只是给"访问"多一个端口。
var dist = builder.Configuration.GetValue<string>("Frontend:Dist");
var serveDist = !string.IsNullOrWhiteSpace(dist)
                && Directory.Exists(dist)
                && File.Exists(Path.Combine(dist, "index.html"));
if (serveDist)
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(dist!),
        OnPrepareResponse = ctx =>
        {
            // assets/* 的文件名里带内容哈希，改一行代码就换个名字，所以可以放心一年不过期；
            // index.html 恰恰相反，它每次都可能是新的，缓存住就等于"改了没生效"
            ctx.Context.Response.Headers.CacheControl =
                ctx.Context.Request.Path.StartsWithSegments("/assets")
                    ? "public,max-age=31536000,immutable"
                    : "no-cache";
        }
    });
}

app.MapControllers();

if (serveDist)
{
    // SPA 兜底：/videos、/video/xxx 这些前端路由在磁盘上没有对应文件，不兜底就是"刷新一下打不开"。
    // /api 不参与兜底——接口路径写错应该老老实实回 404，回一份 HTML 只会让前端把 404 当成功解析
    app.MapFallback(async context =>
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(new { success = false, message = "接口不存在" });
            return;
        }
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-cache";
        await context.Response.SendFileAsync(Path.Combine(dist!, "index.html"));
    });

    app.Logger.LogInformation("前端产物已挂载：{Dist}（改完 ckweb 记得 npm run build）", dist);
}

app.Run();

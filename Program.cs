using System.Reflection;
using ckapi.Services;
using Microsoft.Extensions.Configuration;

var builder = WebApplication.CreateBuilder(args);

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
app.UseHttpsRedirection();
app.UseRouting();
app.MapControllers();

app.Urls.Add("http://0.0.0.0:5033");

app.Run();

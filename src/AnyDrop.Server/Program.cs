using AnyDrop.Server;
using AnyDrop.Server.Logging;
using Serilog.Extensions.Logging;

if (args.Length > 0 && (string.Equals(args[0], "--set-password", StringComparison.Ordinal) ||
                        string.Equals(args[0], "--set-password-stdin", StringComparison.Ordinal)))
    return await Cli.SetPasswordAsync(args);

var builder = WebApplication.CreateSlimBuilder(args);
builder.Configuration.AddJsonFile(
    Path.Combine(AppContext.BaseDirectory, "anydrop.json"), optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables("ANYDROP__");

var config = ConfigLoader.Load(builder.Configuration);
Directory.CreateDirectory(config.DataDir);

// 文件日志：Serilog 只作为一个 ILoggerProvider 挂进来，**不替换 ILoggerFactory**。
// 这一点是承重的：换成 Services.AddSerilog / Host.UseSerilog 会把 MS 的级别过滤器整个绕过，
// 于是 logging:logLevel 与 ANYDROP__Logging__LogLevel__* 全部失效 —— 那正是"日志里出现下载 id"
// 那道安全护栏，不能丢。
//
// 两个坑都不能踩：
//   1) 不能用 Logging.AddSerilog(...)。实测（隔离探针 + 真二进制）它加进来的 provider
//      **不受 MS 级别过滤器约束** —— Microsoft.AspNetCore.* 的 Debug/Information 会照样落盘。
//   2) 不能用 Logging.AddProvider(实例)。实测那样注册的 provider **永远不会被释放**
//      （LoggerFactory 不释放外部实例、DI 也不认它），于是 SerilogLoggerProvider 的
//      dispose:true 是空转 —— buffered=true 时优雅停止会丢掉整个缓冲区。
// 所以走 DI 单例：容器负责释放 ⇒ dispose:true 真正生效 ⇒ 退出时 flush。
var fileLogger = LogFileSetup.TryCreate(config, out var logProblem);
if (logProblem is not null)
    Console.Error.WriteLine($"警告：日志文件不可用（{logProblem}），本次仅输出到控制台。");
if (fileLogger is not null)
    builder.Services.AddSingleton<ILoggerProvider>(_ => new SerilogLoggerProvider(fileLogger, dispose: true));

builder.WebHost.UseUrls(config.Server.Urls);
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = config.Server.MaxUploadBytes;
    options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(5);
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);
});

builder.Services.AddSingleton(config);
builder.Services.AddSingleton<KeyRing>();
builder.Services.AddSingleton<SqliteIndex>();
builder.Services.AddSingleton<BlobStore>();
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<RateLimiter>();
builder.Services.AddSingleton<IpAccessor>();
builder.Services.AddSingleton<WebAssetStore>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<BlobService>();
builder.Services.AddSingleton<AdminAuth>();
builder.Services.AddHostedService<GcService>();

var app = builder.Build();

// 结构版本不匹配（旧库）时给一条可操作的提示，而不是一坨堆栈。
try
{
    await app.Services.GetRequiredService<SqliteIndex>().InitializeAsync();
}
catch (InvalidOperationException ex)
{
    app.Logger.LogCritical("{Message}", ex.Message);
    // 这条路径不会走到 app.Run()，宿主也就不会被释放 —— 显式收尾，别丢尾部日志。
    // （正常退出路径由容器释放 provider，见上面的 DI 注册。）
    fileLogger?.Dispose();
    return 1;
}
_ = app.Services.GetRequiredService<KeyRing>();
var assets = app.Services.GetRequiredService<WebAssetStore>();

if (config.PathBase.Length > 0) app.UsePathBase(config.PathBase);
app.UseSecurityHeaders();
app.UseApiErrorHandling();
// 必须在 UsePathBase 之后显式路由：否则 WebApplication 会把 UseRouting 自动插到管道最前面，
// 带 /drop 前缀的请求会先被匹配（落进兜底端点）才轮到剥前缀。
app.UseRouting();

app.MapBlobEndpoints();
app.MapAdminEndpoints();
app.MapHealthEndpoints();
app.MapPageEndpoints();

app.Logger.LogInformation(
    "AnyDrop 启动：urls={Urls} pathBase='{PathBase}' publicBaseUrl={PublicBaseUrl} dataDir={DataDir} 内嵌前端={HasUi}",
    config.Server.Urls, config.PathBase, config.Server.PublicBaseUrl, config.DataDir, assets.HasUi);

app.Run();
return 0;

public partial class Program
{
    public static DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
}

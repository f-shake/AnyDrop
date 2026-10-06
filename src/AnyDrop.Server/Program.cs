using AnyDrop.Server;

if (args.Length > 0 && (string.Equals(args[0], "--set-password", StringComparison.Ordinal) ||
                        string.Equals(args[0], "--set-password-stdin", StringComparison.Ordinal)))
    return await Cli.SetPasswordAsync(args);

var builder = WebApplication.CreateSlimBuilder(args);
builder.Configuration.AddJsonFile(
    Path.Combine(AppContext.BaseDirectory, "anydrop.json"), optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables("ANYDROP__");

var config = ConfigLoader.Load(builder.Configuration);
Directory.CreateDirectory(config.DataDir);

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

await app.Services.GetRequiredService<SqliteIndex>().InitializeAsync();
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

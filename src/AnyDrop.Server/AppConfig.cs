using System.Globalization;

namespace AnyDrop.Server;

public sealed class ServerOptions
{
    public string PathBase { get; set; } = "";
    public string PublicBaseUrl { get; set; } = "http://127.0.0.1:8790";
    public string Urls { get; set; } = "http://127.0.0.1:8790";
    public long MaxUploadBytes { get; set; } = 256L * 1024 * 1024;
    public int MaxConcurrentUploads { get; set; } = 4;
    public bool CookieSecure { get; set; } = true;
}

public sealed class StorageOptions
{
    public string DataDir { get; set; } = "data";
    public long MinFreeBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    public int MaxUsedPercent { get; set; } = 90;
}

public sealed class CryptoOptions
{
    public int ChunkSizeBytes { get; set; } = 1024 * 1024;
    public string MasterKeyFile { get; set; } = "";
}

public sealed class RetentionOptions
{
    public int DefaultTtlDays { get; set; } = 30;
    public int GcIntervalMinutes { get; set; } = 10;
}

public sealed class SecurityOptions
{
    public int MaxLoginFailures { get; set; } = 10;
    public int LockoutMinutes { get; set; } = 15;
    public int RateLimitPerMinute { get; set; } = 120;

    /// <summary>与 IP/用户名无关的全局登录限流，用来把 PBKDF2 的并发成本钉死。</summary>
    public int LoginGlobalPerMinute { get; set; } = 30;
    public bool TrustProxy { get; set; } = true;
}

public sealed class AdminOptions
{
    public string Username { get; set; } = "admin";
}

/// <summary>
/// 文件日志的**运维参数**。级别不在这一类里：级别仍然只由 <c>logging:logLevel</c>（MS 约定）
/// 与 <c>ANYDROP__Logging__LogLevel__*</c> 决定，Serilog 侧不做二次过滤。
/// </summary>
public sealed class LogFileOptions
{
    /// <summary>滚动周期的合法取值（与 Serilog 的 RollingInterval 同名，不区分大小写）。</summary>
    public static readonly string[] RollingIntervalNames = ["Infinite", "Year", "Month", "Day", "Hour", "Minute"];

    public bool Enabled { get; set; } = true;

    /// <summary>日志目录；相对路径按程序所在目录解析。</summary>
    public string Directory { get; set; } = "logs";

    /// <summary>
    /// 文件名前缀。实际文件名由 <see cref="RollingInterval"/> 决定：
    /// <c>Day</c> → <c>{前缀}{yyyyMMdd}.log</c>，<c>Year/Month/Hour/Minute</c> 用对应长度的日期后缀，
    /// <c>Infinite</c> → <c>{前缀}.log</c>（**没有**日期后缀，所以不会换文件；配合不限大小就是无限增长）。
    /// </summary>
    public string FileNamePrefix { get; set; } = "server-";

    public string RollingInterval { get; set; } = "Day";

    /// <summary>保留文件数上限（**含**正在写的那个）。0 表示不限。</summary>
    public int RetainedFileCountLimit { get; set; } = 14;

    /// <summary>单文件字节上限。0 表示不限。</summary>
    public long FileSizeLimitBytes { get; set; } = 32L * 1024 * 1024;

    /// <summary>到达大小上限时换新文件。false 时该文件到下一个滚动点前不再写入（会静默丢日志）。</summary>
    public bool RollOnFileSizeLimit { get; set; } = true;

    public bool Buffered { get; set; }

    /// <summary>定期 flush 到磁盘的间隔秒数。0 表示不启用。</summary>
    public int FlushToDiskIntervalSeconds { get; set; }
}

public sealed class AppConfig
{
    public ServerOptions Server { get; init; } = new();
    public StorageOptions Storage { get; init; } = new();
    public CryptoOptions Crypto { get; init; } = new();
    public RetentionOptions Retention { get; init; } = new();
    public SecurityOptions Security { get; init; } = new();
    public AdminOptions Admin { get; init; } = new();
    public LogFileOptions LogFile { get; init; } = new();

    /// <summary>绝对路径的数据目录。</summary>
    public string DataDir { get; init; } = "";

    /// <summary>绝对路径的日志目录（仅当 <see cref="LogFileOptions.Enabled"/> 为真时会去创建）。</summary>
    public string LogDirectory { get; init; } = "";
    public string BlobsDir => Path.Combine(DataDir, "blobs");
    public string TempDir => Path.Combine(DataDir, "tmp");
    public string DbPath => Path.Combine(DataDir, "anydrop.db");
    public string MasterKeyPath { get; init; } = "";

    /// <summary>规范化的路径前缀：空串或 "/xxx"（无尾斜杠）。</summary>
    public string PathBase { get; init; } = "";
    public string CookiePath => PathBase.Length == 0 ? "/" : PathBase;

    public string PublicUrl(string relative) =>
        $"{Server.PublicBaseUrl.TrimEnd('/')}/{relative.TrimStart('/')}";
}

public static class ConfigLoader
{
    public static AppConfig Load(IConfiguration c)
    {
        var server = new ServerOptions
        {
            PathBase = Str(c, "server:pathBase", ""),
            PublicBaseUrl = Str(c, "server:publicBaseUrl", "http://127.0.0.1:8790"),
            Urls = Str(c, "server:urls", "http://127.0.0.1:8790"),
            MaxUploadBytes = Long(c, "server:maxUploadBytes", 256L * 1024 * 1024),
            MaxConcurrentUploads = Int(c, "server:maxConcurrentUploads", 4),
            CookieSecure = Bool(c, "server:cookieSecure", true),
        };
        var storage = new StorageOptions
        {
            DataDir = Str(c, "storage:dataDir", "data"),
            MinFreeBytes = Long(c, "storage:minFreeBytes", 2L * 1024 * 1024 * 1024),
            MaxUsedPercent = Int(c, "storage:maxUsedPercent", 90),
        };
        var crypto = new CryptoOptions
        {
            ChunkSizeBytes = Int(c, "crypto:chunkSizeBytes", 1024 * 1024),
            MasterKeyFile = Str(c, "crypto:masterKeyFile", ""),
        };
        var retention = new RetentionOptions
        {
            DefaultTtlDays = Int(c, "retention:defaultTtlDays", 30),
            GcIntervalMinutes = Int(c, "retention:gcIntervalMinutes", 10),
        };
        var security = new SecurityOptions
        {
            MaxLoginFailures = Int(c, "security:maxLoginFailures", 10),
            LockoutMinutes = Int(c, "security:lockoutMinutes", 15),
            RateLimitPerMinute = Int(c, "security:rateLimitPerMinute", 120),
            LoginGlobalPerMinute = Int(c, "security:loginGlobalPerMinute", 30),
            TrustProxy = Bool(c, "security:trustProxy", true),
        };
        var admin = new AdminOptions { Username = Str(c, "admin:username", "admin") };
        var logFile = new LogFileOptions
        {
            Enabled = Bool(c, "logging:file:enabled", true),
            Directory = Str(c, "logging:file:directory", "logs"),
            FileNamePrefix = Str(c, "logging:file:fileNamePrefix", "server-"),
            RollingInterval = Str(c, "logging:file:rollingInterval", "Day"),
            RetainedFileCountLimit = Int(c, "logging:file:retainedFileCountLimit", 14),
            FileSizeLimitBytes = Long(c, "logging:file:fileSizeLimitBytes", 32L * 1024 * 1024),
            RollOnFileSizeLimit = Bool(c, "logging:file:rollOnFileSizeLimit", true),
            Buffered = Bool(c, "logging:file:buffered", false),
            FlushToDiskIntervalSeconds = Int(c, "logging:file:flushToDiskIntervalSeconds", 0),
        };

        // 本批新增的这几个键走"严格取值"：写了却解析不了就报错，而不是像 Bool/Int/Long 那样静默回落默认值。
        // 既有的共享辅助方法保持原语义不动（改它们会影响所有既有配置的兼容性），但新增键上
        // "enabled: 0 / buffered: no / fileSizeLimitBytes: 32MB" 静默变成 true/false/默认值是不能接受的
        // —— 文档明确承诺"配置写错会在启动时报错，不会静默"。
        foreach (var key in new[] { "logging:file:enabled", "logging:file:rollOnFileSizeLimit", "logging:file:buffered" })
            RequireParsable(c, key, v => bool.TryParse(v, out _));
        foreach (var key in new[] { "logging:file:retainedFileCountLimit", "logging:file:flushToDiskIntervalSeconds" })
            RequireParsable(c, key, v => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out _));
        RequireParsable(c, "logging:file:fileSizeLimitBytes",
            v => long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out _));

        var pathBase = NormalizePathBase(server.PathBase);
        server.PathBase = pathBase;

        if (server.MaxUploadBytes <= 0)
            throw new InvalidOperationException("server:maxUploadBytes 必须大于 0");
        if (server.MaxConcurrentUploads <= 0)
            throw new InvalidOperationException("server:maxConcurrentUploads 必须大于 0");
        if (crypto.ChunkSizeBytes < 4096 || crypto.ChunkSizeBytes > 16 * 1024 * 1024)
            throw new InvalidOperationException("crypto:chunkSizeBytes 必须在 4096 到 16777216 之间");
        if (storage.MaxUsedPercent is < 1 or > 100)
            throw new InvalidOperationException("storage:maxUsedPercent 必须在 1 到 100 之间");
        if (storage.MinFreeBytes < 0)
            throw new InvalidOperationException("storage:minFreeBytes 不能为负");
        if (retention.DefaultTtlDays is < 1 or > 3650)
            throw new InvalidOperationException("retention:defaultTtlDays 必须在 1 到 3650 之间");
        if (retention.GcIntervalMinutes is < 1 or > 1440)
            throw new InvalidOperationException("retention:gcIntervalMinutes 必须在 1 到 1440 之间");
        if (security.MaxLoginFailures is < 1 or > 1000)
            throw new InvalidOperationException("security:maxLoginFailures 必须在 1 到 1000 之间");
        if (security.LockoutMinutes is < 1 or > 1440)
            throw new InvalidOperationException("security:lockoutMinutes 必须在 1 到 1440 之间");
        if (security.LoginGlobalPerMinute < 1)
            throw new InvalidOperationException("security:loginGlobalPerMinute 必须大于 0");

        var interval = Array.Find(LogFileOptions.RollingIntervalNames,
            n => string.Equals(n, logFile.RollingInterval, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"logging:file:rollingInterval 必须是 {string.Join(" / ", LogFileOptions.RollingIntervalNames)} 之一");
        logFile.RollingInterval = interval;
        // directory / fileNamePrefix 的"不能为空"**不需要**校验：Str() 对 null/空白一律回落默认值，
        // 这两个字段走到这里不可能为空（写 "" 等于没写）。空串回落默认值的行为由测试钉住。
        if (logFile.FileNamePrefix.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || logFile.FileNamePrefix.Contains('/')
            || logFile.FileNamePrefix.Contains('\\')
            || logFile.FileNamePrefix.Contains("..", StringComparison.Ordinal))
            throw new InvalidOperationException("logging:file:fileNamePrefix 不能包含路径分隔符或非法文件名字符");
        if (logFile.RetainedFileCountLimit < 0)
            throw new InvalidOperationException("logging:file:retainedFileCountLimit 不能为负（0 表示不限）");
        if (logFile.FileSizeLimitBytes < 0)
            throw new InvalidOperationException("logging:file:fileSizeLimitBytes 不能为负（0 表示不限）");
        if (logFile.FlushToDiskIntervalSeconds < 0)
            throw new InvalidOperationException("logging:file:flushToDiskIntervalSeconds 不能为负（0 表示不启用）");

        var dataDir = ResolvePath(storage.DataDir);
        var masterKeyPath = crypto.MasterKeyFile.Length == 0
            ? Path.Combine(dataDir, "master.key")
            : ResolvePath(crypto.MasterKeyFile);

        return new AppConfig
        {
            Server = server,
            Storage = storage,
            Crypto = crypto,
            Retention = retention,
            Security = security,
            Admin = admin,
            LogFile = logFile,
            DataDir = dataDir,
            LogDirectory = ResolvePath(logFile.Directory),
            MasterKeyPath = masterKeyPath,
            PathBase = pathBase,
        };
    }

    /// <summary>空串或 "/xxx"（无尾斜杠、无重复斜杠）。</summary>
    public static string NormalizePathBase(string? raw)
    {
        var v = (raw ?? "").Trim();
        if (v.Length == 0 || v == "/") return "";
        if (!v.StartsWith('/')) v = "/" + v;
        while (v.EndsWith('/')) v = v[..^1];
        while (v.Contains("//")) v = v.Replace("//", "/", StringComparison.Ordinal);
        if (v.Contains("..", StringComparison.Ordinal))
            throw new InvalidOperationException("server:pathBase 不能包含 '..'");
        return v;
    }

    /// <summary>相对路径按程序所在目录解析，避免受启动时工作目录影响。</summary>
    public static string ResolvePath(string path) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path));

    private static string Str(IConfiguration c, string key, string fallback)
    {
        var v = c[key];
        return string.IsNullOrWhiteSpace(v) ? fallback : v.Trim();
    }

    private static int Int(IConfiguration c, string key, int fallback) =>
        c[key] is { Length: > 0 } v && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    private static long Long(IConfiguration c, string key, long fallback) =>
        c[key] is { Length: > 0 } v && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    private static bool Bool(IConfiguration c, string key, bool fallback) =>
        c[key] is { Length: > 0 } v && bool.TryParse(v, out var parsed) ? parsed : fallback;

    /// <summary>
    /// 配置里**写了这个键**却解析不了就报错，而不是像 <see cref="Bool"/>/<see cref="Int"/>/<see cref="Long"/>
    /// 那样静默回落默认值。只用于本批新增的 <c>logging:file:*</c> —— 既有的共享辅助方法保持原语义不动
    /// （改它们等于改变所有既有配置的兼容性）。
    /// </summary>
    private static void RequireParsable(IConfiguration c, string key, Func<string, bool> tryParse)
    {
        var v = c[key];
        if (!string.IsNullOrWhiteSpace(v) && !tryParse(v))
            throw new InvalidOperationException(
                $"{key} 的值无法解析：'{v}'（这里不会静默回落默认值，请改成合法值）");
    }
}

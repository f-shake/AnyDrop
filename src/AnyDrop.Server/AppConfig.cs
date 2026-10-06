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

public sealed class AppConfig
{
    public ServerOptions Server { get; init; } = new();
    public StorageOptions Storage { get; init; } = new();
    public CryptoOptions Crypto { get; init; } = new();
    public RetentionOptions Retention { get; init; } = new();
    public SecurityOptions Security { get; init; } = new();
    public AdminOptions Admin { get; init; } = new();

    /// <summary>绝对路径的数据目录。</summary>
    public string DataDir { get; init; } = "";
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
            DataDir = dataDir,
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
}

using System.Net.Http.Json;
using AnyDrop.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace AnyDrop.Tests;

/// <summary>每个测试用独立的临时数据目录与配置；GC 后台服务在测试里被移除，由用例手动触发。</summary>
public sealed class TestApp : WebApplicationFactory<Program>
{
    public const string AdminPassword = "0123456789abcdef";

    private readonly Dictionary<string, string?> _settings = new(StringComparer.OrdinalIgnoreCase);

    public string Root { get; }
    public string DataDir => Path.Combine(Root, "data");

    public TestApp(params (string Key, string? Value)[] settings)
    {
        Root = TestPaths.NewRoot("app");
        foreach (var (key, value) in settings) _settings[key] = value;
    }

    public TestApp With(string key, string? value)
    {
        _settings[key] = value;
        return this;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("storage:dataDir", DataDir);
        builder.UseSetting("server:urls", "http://127.0.0.1:0");
        builder.UseSetting("server:cookieSecure", "false");
        builder.UseSetting("storage:minFreeBytes", "0");
        builder.UseSetting("storage:maxUsedPercent", "100");
        foreach (var (key, value) in _settings) builder.UseSetting(key, value);
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            // 去掉后台 GC（测试里手动触发），但保留可直接解析的 GcService 实例。
            services.RemoveAll<IHostedService>();
            services.AddSingleton<GcService>();
        });
    }

    public HttpClient NewClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = true,
        HandleCookies = true,
    });

    public T GetService<T>() where T : notnull => Services.GetRequiredService<T>();

    public AppConfig Config => GetService<AppConfig>();

    public string BlobPath(string id) => Path.Combine(DataDir, "blobs", id[..2], id);

    public async Task SetAdminPasswordAsync(string password = AdminPassword)
    {
        var index = GetService<SqliteIndex>();
        var (hash, salt, iterations) = SecretHasher.HashPassword(password);
        await index.SetAdminPasswordAsync(hash, salt, iterations);
    }

    /// <summary>通过 TokenService 建一把上传密钥，返回记录与明文 key（只在这一刻存在）。</summary>
    public async Task<(TokenRecord Token, string Key)> CreateTokenAsync(
        long? quotaBytes = null,
        long? maxFileBytes = null,
        int? ttlDays = null)
    {
        var tokens = GetService<TokenService>();
        var request = new CreateTokenRequest(
            $"test-{Guid.NewGuid():N}"[..16], ttlDays, quotaBytes, maxFileBytes);
        var result = await tokens.CreateAsync(request, CancellationToken.None);
        if (!result.Ok) throw new InvalidOperationException($"建 token 失败：{result.Failure!.Value.Message}");
        var key = result.Value!.Key;
        var record = await GetService<SqliteIndex>().GetTokenAsync(result.Value.Token.Id);
        return (record!, key);
    }

    public Task<GcReport> RunGcAsync() => GetService<GcService>().RunOnceAsync(CancellationToken.None);

    /// <summary>直接把 token / blob 的过期时间改到过去，用来测过期路径。</summary>
    public Task ExpireTokenAsync(string tokenId) => SetExpiryAsync("tokens", tokenId);

    public Task ExpireBlobAsync(string blobId) => SetExpiryAsync("blobs", blobId);

    /// <summary>直接数 idempotency 行数：用例需要证明某条路径确实没有写幂等表。</summary>
    public async Task<long> CountIdempotencyAsync()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={Path.Combine(DataDir, "anydrop.db")}");
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM idempotency";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private async Task SetExpiryAsync(string table, string id)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={Path.Combine(DataDir, "anydrop.db")}");
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"UPDATE {table} SET expires_at = $at WHERE id = $id";
        cmd.Parameters.AddWithValue("$at", Time.Iso(DateTimeOffset.UtcNow.AddMinutes(-5)));
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    public static StringContent Json(object value) =>
        new(System.Text.Json.JsonSerializer.Serialize(value), System.Text.Encoding.UTF8, "application/json");

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清不掉不影响测试结论
        }
    }
}

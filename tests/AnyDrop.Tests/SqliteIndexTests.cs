using AnyDrop.Server;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AnyDrop.Tests;

public sealed class SqliteIndexTests : IDisposable
{
    private readonly string _root = TestPaths.NewRoot("index");

    private AppConfig Config => new()
    {
        Server = new ServerOptions(),
        Storage = new StorageOptions { DataDir = Path.Combine(_root, "data") },
        Crypto = new CryptoOptions(),
        Retention = new RetentionOptions(),
        Security = new SecurityOptions(),
        Admin = new AdminOptions(),
        DataDir = Path.Combine(_root, "data"),
        MasterKeyPath = Path.Combine(_root, "data", "master.key"),
        PathBase = "",
    };

    private async Task<SqliteIndex> NewIndexAsync()
    {
        var index = new SqliteIndex(Config, NullLogger<SqliteIndex>.Instance);
        await index.InitializeAsync();
        return index;
    }

    private static TokenRecord NewToken(string ns = "default", bool canUpload = true, bool canRead = false, bool canDelete = false) => new()
    {
        Id = Ids.NewTokenId(),
        Name = "t",
        KeyHash = SecretHasher.Sha256Hex("ad_" + Guid.NewGuid().ToString("N")),
        KeyPrefix = "ad_ABCDEF",
        Namespace = ns,
        CanUpload = canUpload,
        CanRead = canRead,
        CanDelete = canDelete,
        MaxFileBytes = 1024,
        QuotaBytes = 8192,
        CreatedAt = Time.NowIso(),
        ExpiresAt = Time.AddDaysIso(30),
    };

    private static BlobRecord NewBlob(string tokenId, string ns = "default", long size = 100, string? expiresAt = null) => new()
    {
        Id = Ids.NewBlobId(),
        Name = "file.txt",
        Size = size,
        Sha256 = new string('a', 64),
        ContentType = "text/plain",
        CipherPath = $"blobs/xx/{Guid.NewGuid():N}",
        KeyVersion = 1,
        FileNonce = [1, 2, 3, 4],
        ChunkSize = 1024,
        WrappedDek = new byte[48],
        DekNonce = new byte[12],
        TokenId = tokenId,
        Namespace = ns,
        CreatedAt = Time.NowIso(),
        ExpiresAt = expiresAt ?? Time.AddDaysIso(30),
    };

    [Fact]
    public async Task 初始化可重复执行()
    {
        var index = await NewIndexAsync();
        await index.InitializeAsync();
        Assert.Equal(0, await index.CountBlobsAsync());
    }

    [Fact]
    public async Task token的增查改与用量重算()
    {
        var index = await NewIndexAsync();
        var token = NewToken();
        await index.InsertTokenAsync(token);

        var loaded = await index.GetTokenAsync(token.Id);
        Assert.NotNull(loaded);
        Assert.Equal(token.Namespace, loaded!.Namespace);
        Assert.True(loaded.CanUpload);
        Assert.False(loaded.CanRead);

        Assert.NotNull(await index.FindTokenByHashAsync(token.KeyHash));
        Assert.Null(await index.FindTokenByHashAsync("deadbeef"));

        await index.TouchTokenAsync(token.Id, Time.NowIso());
        Assert.NotNull((await index.GetTokenAsync(token.Id))!.LastUsedAt);

        await index.AddTokenUsageAsync(token.Id, 500);
        Assert.Equal(500, (await index.GetTokenAsync(token.Id))!.UsedBytes);
        await index.AddTokenUsageAsync(token.Id, -900);
        Assert.Equal(0L, (await index.GetTokenAsync(token.Id))!.UsedBytes);

        await index.InsertBlobAsync(NewBlob(token.Id, size: 300));
        await index.InsertBlobAsync(NewBlob(token.Id, size: 700));
        await index.RecomputeTokenUsageAsync(token.Id);
        Assert.Equal(1000, (await index.GetTokenAsync(token.Id))!.UsedBytes);

        Assert.True(await index.RevokeTokenAsync(token.Id, Time.NowIso()));
        Assert.True((await index.GetTokenAsync(token.Id))!.IsRevoked);
        Assert.False(await index.RevokeTokenAsync(token.Id, Time.NowIso()), "重复撤销应返回 false");
    }

    [Fact]
    public async Task blob列表分页与搜索()
    {
        var index = await NewIndexAsync();
        var token = NewToken();
        await index.InsertTokenAsync(token);
        for (var i = 0; i < 5; i++)
        {
            var blob = NewBlob(token.Id);
            blob.Name = i % 2 == 0 ? $"report-{i}.md" : $"image-{i}.png";
            await index.InsertBlobAsync(blob);
        }

        var (total, items) = await index.ListBlobsAsync(null, 1, 2);
        Assert.Equal(5, total);
        Assert.Equal(2, items.Count);

        var (page2Total, page2) = await index.ListBlobsAsync(null, 3, 2);
        Assert.Equal(5, page2Total);
        Assert.Single(page2);

        var (filteredTotal, filtered) = await index.ListBlobsAsync("report", 1, 50);
        Assert.Equal(3, filteredTotal);
        Assert.All(filtered, b => Assert.Contains("report", b.Name));
    }

    [Fact]
    public async Task blob的过期列表排除pinned()
    {
        var index = await NewIndexAsync();
        var token = NewToken();
        await index.InsertTokenAsync(token);
        var past = Time.Iso(DateTimeOffset.UtcNow.AddHours(-1));
        var expired = NewBlob(token.Id, expiresAt: past);
        var pinned = NewBlob(token.Id, expiresAt: past);
        pinned.Pinned = true;
        var alive = NewBlob(token.Id, expiresAt: Time.AddDaysIso(1));
        await index.InsertBlobAsync(expired);
        await index.InsertBlobAsync(pinned);
        await index.InsertBlobAsync(alive);

        var list = await index.ListExpiredAsync(Time.NowIso(), 100);
        Assert.Single(list);
        Assert.Equal(expired.Id, list[0].Id);
    }

    [Fact]
    public async Task blob的删除过期改期与下载计数()
    {
        var index = await NewIndexAsync();
        var token = NewToken();
        await index.InsertTokenAsync(token);
        var blob = NewBlob(token.Id);
        await index.InsertBlobAsync(blob);

        Assert.NotNull(await index.GetBlobAsync(blob.Id));
        Assert.Null(await index.GetBlobAsync("不存在的id"));

        await index.IncrementDownloadCountAsync(blob.Id);
        await index.IncrementDownloadCountAsync(blob.Id);
        Assert.Equal(2, (await index.GetBlobAsync(blob.Id))!.DownloadCount);

        Assert.True(await index.UpdateExpiryAsync(blob.Id, Time.AddDaysIso(1)));
        Assert.True(await index.SetPinnedAsync(blob.Id, true));
        Assert.True((await index.GetBlobAsync(blob.Id))!.Pinned);

        Assert.True(await index.DeleteBlobAsync(blob.Id));
        Assert.False(await index.DeleteBlobAsync(blob.Id));
    }

    [Fact]
    public async Task 幂等键重复写入不覆盖已有映射()
    {
        var index = await NewIndexAsync();
        var token = NewToken();
        await index.InsertTokenAsync(token);
        Assert.Null(await index.GetIdempotentBlobIdAsync(token.Id, "k1"));

        await index.InsertIdempotencyAsync(token.Id, "k1", "blob-a");
        await index.InsertIdempotencyAsync(token.Id, "k1", "blob-b");

        Assert.Equal("blob-a", await index.GetIdempotentBlobIdAsync(token.Id, "k1"));
        Assert.Null(await index.GetIdempotentBlobIdAsync(token.Id, "k2"));
    }

    [Fact]
    public async Task 审计写入与读取()
    {
        var index = await NewIndexAsync();
        await index.InsertAuditAsync("blob.upload", "t1", "b1", "default", "1.2.3.4", 123, "name.txt");
        await index.InsertAuditAsync("blob.download", "t1", "b1", "default", "1.2.3.4", 123, null);

        var items = await index.ListAuditAsync(10);
        Assert.Equal(2, items.Count);
        Assert.Equal("blob.download", items[0].Action);
        Assert.Equal(123, items[0].Bytes);
        Assert.Null(items[0].Detail);
    }

    [Fact]
    public async Task 管理员密码的写入与更新()
    {
        var index = await NewIndexAsync();
        Assert.Null(await index.GetAdminAsync());

        await index.SetAdminPasswordAsync("hash1", "salt1", 1000);
        var first = await index.GetAdminAsync();
        Assert.Equal("hash1", first!.Value.Hash);

        await index.SetAdminPasswordAsync("hash2", "salt2", 2000);
        var second = await index.GetAdminAsync();
        Assert.Equal("hash2", second!.Value.Hash);
        Assert.Equal(2000, second.Value.Iterations);
    }

    [Fact]
    public async Task 过期删除会跳过已pin的行()
    {
        var index = await NewIndexAsync();
        var token = NewToken();
        await index.InsertTokenAsync(token);
        var past = Time.Iso(DateTimeOffset.UtcNow.AddHours(-1));
        var pinned = NewBlob(token.Id, expiresAt: past);
        pinned.Pinned = true;
        var expired = NewBlob(token.Id, expiresAt: past);
        await index.InsertBlobAsync(pinned);
        await index.InsertBlobAsync(expired);

        Assert.False(await index.DeleteExpiredBlobAsync(pinned.Id));
        Assert.True(await index.DeleteExpiredBlobAsync(expired.Id));
        Assert.NotNull(await index.GetBlobAsync(pinned.Id));
        Assert.Null(await index.GetBlobAsync(expired.Id));
    }

    [Fact]
    public async Task 配额预占是原子的不会超额()
    {
        var index = await NewIndexAsync();
        var token = NewToken();
        token.QuotaBytes = 100;
        await index.InsertTokenAsync(token);

        Assert.True(await index.TryReserveQuotaAsync(token.Id, 60));
        Assert.False(await index.TryReserveQuotaAsync(token.Id, 60));
        Assert.True(await index.TryReserveQuotaAsync(token.Id, 40));
        Assert.False(await index.TryReserveQuotaAsync(token.Id, 1));
        Assert.Equal(100, (await index.GetTokenAsync(token.Id))!.UsedBytes);

        await index.AddTokenUsageAsync(token.Id, -70);
        Assert.True(await index.TryReserveQuotaAsync(token.Id, 70));
    }

    [Fact]
    public async Task 幂等行随blob删除而清理_悬空行也会被清掉()
    {
        var index = await NewIndexAsync();
        var token = NewToken();
        await index.InsertTokenAsync(token);
        var blob = NewBlob(token.Id);
        await index.InsertBlobAsync(blob, "key-1");
        await index.InsertIdempotencyAsync(token.Id, "dangling", "missing-blob");

        Assert.Equal(blob.Id, await index.GetIdempotentBlobIdAsync(token.Id, "key-1"));
        Assert.Equal(1, await index.PurgeDanglingIdempotencyAsync());
        Assert.Null(await index.GetIdempotentBlobIdAsync(token.Id, "dangling"));

        Assert.True(await index.DeleteBlobAsync(blob.Id));
        Assert.Null(await index.GetIdempotentBlobIdAsync(token.Id, "key-1"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 忽略
        }
    }
}

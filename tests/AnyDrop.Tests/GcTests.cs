using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using AnyDrop.Server;
using Xunit;

namespace AnyDrop.Tests;

public sealed class GcTests
{
    [Fact]
    public async Task 过期文件被回收且用量回退()
    {
        using var app = new TestApp();
        var (token, key) = await app.CreateTokenAsync();
        var client = app.NewClient();
        var upload = await TestHttp.UploadOkAsync(client, key, RandomNumberGenerator.GetBytes(2000));
        var index = app.GetService<SqliteIndex>();
        Assert.Equal(2000, (await index.GetTokenAsync(token.Id))!.UsedBytes);

        await app.ExpireBlobAsync(upload.Id);
        var report = await app.RunGcAsync();

        Assert.Equal(1, report.ExpiredBlobs);
        Assert.Null(await index.GetBlobAsync(upload.Id));
        Assert.False(File.Exists(app.BlobPath(upload.Id)));
        Assert.Equal(0, (await index.GetTokenAsync(token.Id))!.UsedBytes);
        Assert.Contains(await index.ListAuditAsync(10), a => a.Action == "blob.expire" && a.BlobId == upload.Id);
    }

    [Fact]
    public async Task 未过期文件不会被回收()
    {
        using var app = new TestApp();
        var (_, key) = await app.CreateTokenAsync();
        var client = app.NewClient();
        var upload = await TestHttp.UploadOkAsync(client, key, Encoding.UTF8.GetBytes("keep me"));

        var report = await app.RunGcAsync();

        Assert.Equal(0, report.ExpiredBlobs);
        Assert.NotNull(await app.GetService<SqliteIndex>().GetBlobAsync(upload.Id));
        Assert.True(File.Exists(app.BlobPath(upload.Id)));
    }

    [Fact]
    public async Task pinned文件即使过期也不回收()
    {
        using var app = new TestApp();
        var (_, key) = await app.CreateTokenAsync();
        var client = app.NewClient();
        var upload = await TestHttp.UploadOkAsync(client, key, Encoding.UTF8.GetBytes("pin me"));
        var index = app.GetService<SqliteIndex>();
        await index.SetPinnedAsync(upload.Id, true);
        await app.ExpireBlobAsync(upload.Id);

        var report = await app.RunGcAsync();

        Assert.Equal(0, report.ExpiredBlobs);
        Assert.NotNull(await index.GetBlobAsync(upload.Id));
        Assert.True(File.Exists(app.BlobPath(upload.Id)));
    }

    [Fact]
    public async Task 孤儿密文按宽限期回收而新文件保留()
    {
        using var app = new TestApp();
        var (_, key) = await app.CreateTokenAsync();
        var client = app.NewClient();
        var kept = await TestHttp.UploadOkAsync(client, key, Encoding.UTF8.GetBytes("kept"));

        var blobsDir = Path.Combine(app.DataDir, "blobs");
        var oldOrphan = Path.Combine(blobsDir, "zz", "zzoldorphanfile");
        var freshOrphan = Path.Combine(blobsDir, "zz", "zzfreshorphanfile");
        Directory.CreateDirectory(Path.GetDirectoryName(oldOrphan)!);
        await File.WriteAllTextAsync(oldOrphan, "junk");
        await File.WriteAllTextAsync(freshOrphan, "junk");
        File.SetLastWriteTimeUtc(oldOrphan, DateTime.UtcNow.AddHours(-5));

        var report = await app.RunGcAsync();

        Assert.Equal(1, report.OrphanFiles);
        Assert.False(File.Exists(oldOrphan));
        Assert.True(File.Exists(freshOrphan));
        Assert.True(File.Exists(app.BlobPath(kept.Id)));
    }

    [Fact]
    public async Task 过期的临时分片被清理()
    {
        using var app = new TestApp();
        var tempDir = Path.Combine(app.DataDir, "tmp");
        Directory.CreateDirectory(tempDir);
        var stale = Path.Combine(tempDir, "stale.part");
        var fresh = Path.Combine(tempDir, "fresh.part");
        await File.WriteAllTextAsync(stale, "x");
        await File.WriteAllTextAsync(fresh, "y");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-3));

        var report = await app.RunGcAsync();

        Assert.Equal(1, report.StaleParts);
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public async Task 孤儿回收前会再向数据库复核引用()
    {
        using var app = new TestApp();
        var (_, key) = await app.CreateTokenAsync();
        var client = app.NewClient();
        var upload = await TestHttp.UploadOkAsync(client, key, Encoding.UTF8.GetBytes("referenced"));

        // 模拟「耗时超过宽限期的上传」：文件 mtime 很旧，但数据库里确实有它这一行。
        // 单靠 GC 开始时的快照会把它当孤儿；两阶段复核必须保住它。
        File.SetLastWriteTimeUtc(app.BlobPath(upload.Id), DateTime.UtcNow.AddHours(-2));
        var store = app.GetService<BlobStore>();
        var candidates = store.FindOrphanCandidates([], TimeSpan.FromHours(1));
        Assert.Contains(BlobStore.RelativePath(upload.Id), candidates);

        var stillReferenced = await app.GetService<SqliteIndex>().FilterExistingPathsAsync(candidates);
        candidates.RemoveAll(stillReferenced.Contains);
        var removed = store.DeleteFiles(candidates);

        Assert.Equal(0, removed);
        Assert.True(File.Exists(app.BlobPath(upload.Id)));
    }

    [Fact]
    public async Task 健康检查返回磁盘与文件数()
    {
        using var app = new TestApp();
        var (_, key) = await app.CreateTokenAsync();
        var client = app.NewClient();
        await TestHttp.UploadOkAsync(client, key, Encoding.UTF8.GetBytes("healthz"));

        var health = await client.GetFromJsonAsync<HealthResponse>("/healthz");

        Assert.Equal("ok", health!.Status);
        Assert.Equal(1, health.BlobCount);
        Assert.True(health.DiskFreeBytes > 0);
        Assert.True(health.UptimeSeconds >= 0);
    }
}

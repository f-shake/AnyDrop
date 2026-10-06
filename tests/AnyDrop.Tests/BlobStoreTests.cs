using System.Text;
using AnyDrop.Server;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AnyDrop.Tests;

public sealed class BlobStoreTests : IDisposable
{
    private readonly string _root = TestPaths.NewRoot("store");

    private AppConfig Config => new()
    {
        Server = new ServerOptions(),
        Storage = new StorageOptions { DataDir = Path.Combine(_root, "data") },
        Crypto = new CryptoOptions { ChunkSizeBytes = 1024 },
        Retention = new RetentionOptions(),
        Security = new SecurityOptions(),
        Admin = new AdminOptions(),
        DataDir = Path.Combine(_root, "data"),
        MasterKeyPath = Path.Combine(_root, "data", "master.key"),
        PathBase = "",
    };

    private BlobStore NewStore() => new(Config, NullLogger<BlobStore>.Instance);

    [Fact]
    public void 相对路径按前两位分桶()
    {
        var id = Ids.NewBlobId();
        Assert.Equal($"blobs/{id[..2]}/{id}", BlobStore.RelativePath(id));
    }

    [Fact]
    public async Task 临时文件提交后落到最终路径且内容一致()
    {
        var store = NewStore();
        var id = Ids.NewBlobId();
        var (tempPath, stream) = store.CreateTemp();
        await using (stream)
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes("ciphertext"));
        }

        Assert.True(File.Exists(tempPath));
        Assert.False(store.Exists(BlobStore.RelativePath(id)));

        var relative = store.Commit(tempPath, id);

        Assert.Equal(BlobStore.RelativePath(id), relative);
        Assert.True(store.Exists(relative));
        Assert.False(File.Exists(tempPath));
        await using var read = store.OpenRead(relative);
        using var reader = new StreamReader(read);
        Assert.Equal("ciphertext", await reader.ReadToEndAsync());
    }

    [Fact]
    public void 删除不存在的文件返回false()
    {
        var store = NewStore();
        Assert.False(store.Delete(BlobStore.RelativePath(Ids.NewBlobId())));
    }

    [Fact]
    public void 清理过期分片但保留新鲜分片()
    {
        var store = NewStore();
        var tempDir = Path.Combine(Config.DataDir, "tmp");
        var old = Path.Combine(tempDir, "old.part");
        var fresh = Path.Combine(tempDir, "fresh.part");
        File.WriteAllText(old, "x");
        File.WriteAllText(fresh, "y");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-3));

        var removed = store.CleanStaleParts(TimeSpan.FromHours(1));

        Assert.Equal(1, removed);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void 孤儿文件按宽限期回收_新文件不动()
    {
        var store = NewStore();
        var knownId = Ids.NewBlobId();
        var (temp, stream) = store.CreateTemp();
        stream.Dispose();
        var knownRelative = store.Commit(temp, knownId);

        var orphanOldId = Ids.NewBlobId();
        var (tempOld, streamOld) = store.CreateTemp();
        streamOld.Dispose();
        var orphanOld = store.Commit(tempOld, orphanOldId);
        File.SetLastWriteTimeUtc(store.Resolve(orphanOld), DateTime.UtcNow.AddHours(-5));

        var orphanFreshId = Ids.NewBlobId();
        var (tempFresh, streamFresh) = store.CreateTemp();
        streamFresh.Dispose();
        var orphanFresh = store.Commit(tempFresh, orphanFreshId);

        // 两阶段：先挑候选，再由调用方向数据库复核后再删
        var candidates = store.FindOrphanCandidates([knownRelative], TimeSpan.FromHours(1));
        Assert.Equal([orphanOld], candidates);

        var removed = store.DeleteFiles(candidates);

        Assert.Equal(1, removed);
        Assert.True(store.Exists(knownRelative));
        Assert.False(store.Exists(orphanOld));
        Assert.True(store.Exists(orphanFresh));
    }

    [Fact]
    public void 提交不再覆盖同名文件()
    {
        var store = NewStore();
        var id = Ids.NewBlobId();
        var (temp, stream) = store.CreateTemp();
        stream.Dispose();
        var relative = store.Commit(temp, id);
        File.WriteAllText(store.Resolve(relative), "original");

        var (temp2, stream2) = store.CreateTemp();
        stream2.Dispose();
        Assert.Throws<IOException>(() => store.Commit(temp2, id));

        Assert.Equal("original", File.ReadAllText(store.Resolve(relative)));
        Assert.True(File.Exists(temp2), "提交失败时临时文件必须保留，交给调用方重试或清理");
        store.TryDeleteTemp(temp2);
    }

    [Fact]
    public void 回退后可以用新的id重新提交()
    {
        var store = NewStore();
        var (temp, stream) = store.CreateTemp();
        stream.Dispose();
        var firstId = Ids.NewBlobId();
        var firstRelative = store.Commit(temp, firstId);

        // 模拟「元数据写入失败」：把文件退回临时路径
        Assert.True(store.Rollback(firstRelative, temp));
        Assert.False(store.Exists(firstRelative));
        Assert.True(File.Exists(temp));

        var secondRelative = store.Commit(temp, Ids.NewBlobId());

        Assert.NotEqual(firstRelative, secondRelative);
        Assert.True(store.Exists(secondRelative));
    }

    [Fact]
    public void 回退失败时删除密文而不是留下孤儿()
    {
        var store = NewStore();
        var (temp, stream) = store.CreateTemp();
        stream.Dispose();
        var relative = store.Commit(temp, Ids.NewBlobId());
        File.WriteAllText(temp, "占用临时路径，让回退失败");

        Assert.False(store.Rollback(relative, temp));
        Assert.False(store.Exists(relative));
    }

    [Fact]
    public void 清理过期分片会跳过进行中的上传()
    {
        var store = NewStore();
        var (activePath, activeStream) = store.CreateTemp();
        using (activeStream)
        {
            File.SetLastWriteTimeUtc(activePath, DateTime.UtcNow.AddHours(-5));
            var abandoned = Path.Combine(Config.DataDir, "tmp", "abandoned.part");
            File.WriteAllText(abandoned, "x");
            File.SetLastWriteTimeUtc(abandoned, DateTime.UtcNow.AddHours(-5));

            var removed = store.CleanStaleParts(TimeSpan.FromHours(1));

            Assert.Equal(1, removed);
            Assert.False(File.Exists(abandoned));
            Assert.True(File.Exists(activePath), "正在写入的分片不能被清理掉");
        }
        store.TryDeleteTemp(activePath);
    }

    [Fact]
    public void 磁盘状态可用且水位判断可关闭()
    {
        var store = NewStore();
        var (free, used) = store.DiskStatus();
        Assert.True(free > 0);
        Assert.InRange(used, 0, 100);
        Assert.False(store.IsDiskLow(minFreeBytes: 0, maxUsedPercent: 100));
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

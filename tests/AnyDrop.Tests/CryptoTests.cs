using System.Security.Cryptography;
using System.Text;
using AnyDrop.Server;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AnyDrop.Tests;

public sealed class CryptoTests : IDisposable
{
    private readonly string _root = TestPaths.NewRoot("crypto");

    private KeyRing NewKeyRing(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        var config = new AppConfig { DataDir = dir, MasterKeyPath = Path.Combine(dir, "master.key") };
        return new KeyRing(config, NullLogger<KeyRing>.Instance);
    }

    private static async Task<byte[]> EncryptAsync(byte[] plaintext, KeyRing ring, int chunkSize)
    {
        using var input = new MemoryStream(plaintext);
        using var output = new MemoryStream();
        await BlobFormat.EncryptStreamAsync(input, output, ring, chunkSize, maxBytes: long.MaxValue, CancellationToken.None);
        return output.ToArray();
    }

    [Fact]
    public void 主密钥首次生成_再次加载得到同一把密钥()
    {
        var first = NewKeyRing("same");
        var second = NewKeyRing("same");
        var dek = new byte[32];
        var nonce = new byte[12];
        var aad = new byte[] { 1, 2, 3 };
        var wrapped = first.WrapDek(dek, nonce, aad);

        Assert.Equal(48, wrapped.Length);
        Assert.Equal(dek, second.UnwrapDek(wrapped, nonce, aad));
    }

    [Fact]
    public void 主密钥长度不对时拒绝启动()
    {
        var dir = Path.Combine(_root, "badkey");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "master.key");
        File.WriteAllBytes(path, new byte[16]);
        var config = new AppConfig { DataDir = dir, MasterKeyPath = path };

        Assert.Throws<InvalidOperationException>(() => new KeyRing(config, NullLogger<KeyRing>.Instance));
    }

    [Fact]
    public void 错误的附加认证数据无法解包DEK()
    {
        var ring = NewKeyRing("aad");
        var wrapped = ring.WrapDek(new byte[32], new byte[12], "aad-1"u8);

        Assert.ThrowsAny<CryptographicException>(() => ring.UnwrapDek(wrapped, new byte[12], "aad-2"u8));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1023)]
    [InlineData(1024)]
    [InlineData(1025)]
    [InlineData(4096)]
    [InlineData(5000)]
    public async Task 加密解密往返_字节完全一致(int size)
    {
        var ring = NewKeyRing($"roundtrip-{size}");
        var plaintext = RandomNumberGenerator.GetBytes(size);
        var ciphertext = await EncryptAsync(plaintext, ring, 1024);

        Assert.Equal(BlobFormat.TotalFileSize(size, 1024), ciphertext.Length);

        using var stream = new MemoryStream(ciphertext);
        using var file = BlobFile.Open(stream, ring);
        Assert.Equal(size, file.Size);

        using var output = new MemoryStream();
        await file.CopyRangeAsync(0, size, output, CancellationToken.None);
        Assert.Equal(plaintext, output.ToArray());
    }

    [Fact]
    public async Task 磁盘上不出现明文()
    {
        var ring = NewKeyRing("noplaintext");
        var secret = Encoding.UTF8.GetBytes("TOP-SECRET-MARKER-9f7a1c");
        var ciphertext = await EncryptAsync(secret, ring, 4096);

        Assert.DoesNotContain("TOP-SECRET-MARKER-9f7a1c", Encoding.UTF8.GetString(ciphertext));
    }

    [Fact]
    public async Task 翻转任意一个字节都会导致解密失败()
    {
        var ring = NewKeyRing("tamper");
        var ciphertext = await EncryptAsync(RandomNumberGenerator.GetBytes(3000), ring, 1024);

        for (var offset = BlobFormat.HeaderSize; offset < ciphertext.Length; offset += 97)
        {
            var tampered = (byte[])ciphertext.Clone();
            tampered[offset] ^= 0x01;
            using var stream = new MemoryStream(tampered);
            using var file = BlobFile.Open(stream, ring);
            await Assert.ThrowsAnyAsync<CryptographicException>(async () =>
                await file.CopyRangeAsync(0, file.Size, Stream.Null, CancellationToken.None));
        }
    }

    [Fact]
    public async Task 换一把主密钥后无法解密()
    {
        var writer = NewKeyRing("kek-a");
        var reader = NewKeyRing("kek-b");
        var ciphertext = await EncryptAsync(RandomNumberGenerator.GetBytes(64), writer, 1024);

        using var stream = new MemoryStream(ciphertext);
        Assert.ThrowsAny<CryptographicException>(() => BlobFile.Open(stream, reader));
    }

    [Fact]
    public async Task 多块文件的每块nonce都不同()
    {
        var ring = NewKeyRing("nonce");
        var ciphertext = await EncryptAsync(RandomNumberGenerator.GetBytes(4096), ring, 1024);
        using var stream = new MemoryStream(ciphertext);
        using var file = BlobFile.Open(stream, ring);
        Assert.Equal(4, file.ChunkCount);

        var nonces = Enumerable.Range(0, 4)
            .Select(i => Convert.ToHexString(BlobFormat.ChunkNonce(file.FileNonce, i)))
            .ToList();
        Assert.Equal(4, nonces.Distinct().Count());
        Assert.Equal(24, nonces[0].Length);
    }

    [Fact]
    public async Task 把一块密文挪到另一块位置会导致认证失败()
    {
        var ring = NewKeyRing("chunk-swap");
        var plaintext = RandomNumberGenerator.GetBytes(3000);
        var ciphertext = await EncryptAsync(plaintext, ring, 1024);

        // 交换第 0 块与第 1 块的密文（含各自 tag），AAD 里的块号应当让解密切断
        const int blockSize = 1024 + BlobFormat.TagSize;
        var swapped = (byte[])ciphertext.Clone();
        Array.Copy(ciphertext, BlobFormat.HeaderSize, swapped, BlobFormat.HeaderSize + blockSize, blockSize);
        Array.Copy(ciphertext, BlobFormat.HeaderSize + blockSize, swapped, BlobFormat.HeaderSize, blockSize);

        using var stream = new MemoryStream(swapped);
        using var file = BlobFile.Open(stream, ring);
        await Assert.ThrowsAnyAsync<CryptographicException>(async () =>
            await file.CopyRangeAsync(0, file.Size, Stream.Null, CancellationToken.None));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(5, 1)]
    [InlineData(1023, 2)]
    [InlineData(1024, 1)]
    [InlineData(1500, 700)]
    [InlineData(2999, 1)]
    public async Task 任意区间解密与明文切片一致(int start, int length)
    {
        var ring = NewKeyRing($"range-{start}-{length}");
        var plaintext = RandomNumberGenerator.GetBytes(3000);
        var ciphertext = await EncryptAsync(plaintext, ring, 1024);

        using var stream = new MemoryStream(ciphertext);
        using var file = BlobFile.Open(stream, ring);
        using var output = new MemoryStream();
        await file.CopyRangeAsync(start, length, output, CancellationToken.None);

        Assert.Equal(plaintext.Skip(start).Take(length).ToArray(), output.ToArray());
    }

    [Fact]
    public async Task 超出范围的区间被拒绝()
    {
        var ring = NewKeyRing("range-oob");
        var ciphertext = await EncryptAsync(RandomNumberGenerator.GetBytes(100), ring, 1024);
        using var stream = new MemoryStream(ciphertext);
        using var file = BlobFile.Open(stream, ring);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            file.CopyRangeAsync(50, 100, Stream.Null, CancellationToken.None));
    }

    [Fact]
    public async Task 请求体超过上限时抛PayloadTooLarge()
    {
        var ring = NewKeyRing("too-large");
        using var input = new MemoryStream(RandomNumberGenerator.GetBytes(2048));
        using var output = new MemoryStream();

        await Assert.ThrowsAsync<PayloadTooLargeException>(() =>
            BlobFormat.EncryptStreamAsync(input, output, ring, 1024, maxBytes: 1000, CancellationToken.None));
    }

    [Fact]
    public void 密码哈希校验与常数时间比较()
    {
        var (hash, salt, iterations) = SecretHasher.HashPassword("correct horse battery staple");
        Assert.True(SecretHasher.VerifyPassword("correct horse battery staple", hash, salt, iterations));
        Assert.False(SecretHasher.VerifyPassword("wrong password", hash, salt, iterations));
        Assert.False(SecretHasher.VerifyPassword("correct horse battery staple", hash, "not-base64", iterations));
    }

    [Fact]
    public void 十六进制比较对长度不同的输入返回false()
    {
        Assert.False(SecretHasher.FixedTimeEqualsHex("abcd", "abcde"));
        Assert.False(SecretHasher.FixedTimeEqualsHex(null, "abcd"));
        Assert.True(SecretHasher.FixedTimeEqualsHex("ab12", "ab12"));
    }

    [Fact]
    public void 生成的id长度与字母表符合约定()
    {
        var ids = Enumerable.Range(0, 200).Select(_ => Ids.NewBlobId()).ToList();
        Assert.All(ids, id => Assert.True(Ids.IsBlobId(id), id));
        Assert.Equal(ids.Count, ids.Distinct().Count());

        var tokenIds = Enumerable.Range(0, 100).Select(_ => Ids.NewTokenId()).ToList();
        Assert.All(tokenIds, id => Assert.True(Ids.IsTokenId(id), id));

        var key = Ids.NewTokenKey();
        Assert.StartsWith("ad_", key);
        Assert.Equal(3 + 39, key.Length);
        Assert.False(Ids.IsBlobId("i".PadRight(26, 'i')), "Crockford 字母表不含 I");
    }

    [Fact]
    public void 过期判断对空值与坏格式的处理()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.False(Time.IsExpired(null, now));
        Assert.True(Time.IsExpired("not-a-date", now));
        Assert.True(Time.IsExpired(Time.Iso(now.AddSeconds(-1)), now));
        Assert.False(Time.IsExpired(Time.Iso(now.AddHours(1)), now));
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

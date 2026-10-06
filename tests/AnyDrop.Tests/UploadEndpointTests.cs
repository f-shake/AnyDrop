using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using AnyDrop.Server;
using Xunit;

namespace AnyDrop.Tests;

public sealed class UploadEndpointTests
{
    private static string Sha256(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    [Fact]
    public async Task 上传成功返回完整信息且磁盘上只有密文()
    {
        using var app = new TestApp();
        var (_, key) = await app.CreateTokenAsync();
        var client = app.NewClient();
        var body = Encoding.UTF8.GetBytes("hello anydrop 你好");

        var upload = await TestHttp.UploadOkAsync(client, key, body, "报告.txt");

        Assert.Equal(body.Length, upload.Size);
        Assert.Equal(Sha256(body), upload.Sha256);
        Assert.True(Ids.IsBlobId(upload.Id));
        Assert.EndsWith($"/v1/blobs/{upload.Id}", upload.Url);
        Assert.True(Time.TryParse(upload.ExpiresAt, out _));
        Assert.True(File.Exists(app.BlobPath(upload.Id)));

        var onDisk = await File.ReadAllBytesAsync(app.BlobPath(upload.Id));
        Assert.Equal(BlobFormat.TotalFileSize(body.Length, app.Config.Crypto.ChunkSizeBytes), onDisk.Length);
        Assert.DoesNotContain("hello anydrop", Encoding.UTF8.GetString(onDisk));
    }

    [Fact]
    public async Task 上传后没有临时分片残留()
    {
        using var app = new TestApp();
        var (_, key) = await app.CreateTokenAsync();
        var client = app.NewClient();

        await TestHttp.UploadOkAsync(client, key, RandomNumberGenerator.GetBytes(5000));

        var tempDir = Path.Combine(app.DataDir, "tmp");
        Assert.Empty(Directory.GetFiles(tempDir, "*.part"));
    }

    [Fact]
    public async Task 缺少密钥返回401()
    {
        using var app = new TestApp();
        var client = app.NewClient();

        var response = await client.SendAsync(TestHttp.Upload(null, Encoding.UTF8.GetBytes("x")));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(ErrorCodes.InvalidToken, await TestHttp.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task 超过token上限返回413()
    {
        using var app = new TestApp();
        var (_, key) = await app.CreateTokenAsync(maxFileBytes: 100);
        var client = app.NewClient();

        var response = await client.SendAsync(TestHttp.Upload(key, new byte[200]));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(ErrorCodes.FileTooLarge, await TestHttp.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task 超过服务端上限返回413()
    {
        using var app = new TestApp(("server:maxUploadBytes", "1000"));
        var (_, key) = await app.CreateTokenAsync();
        var client = app.NewClient();

        var response = await client.SendAsync(TestHttp.Upload(key, new byte[2000]));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(ErrorCodes.FileTooLarge, await TestHttp.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task 超过配额返回507()
    {
        using var app = new TestApp();
        var (_, key) = await app.CreateTokenAsync(quotaBytes: 100);
        var client = app.NewClient();

        var response = await client.SendAsync(TestHttp.Upload(key, new byte[200]));

        Assert.Equal(HttpStatusCode.InsufficientStorage, response.StatusCode);
        Assert.Equal(ErrorCodes.QuotaExceeded, await TestHttp.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task 磁盘水位越线返回507()
    {
        using var app = new TestApp(("storage:minFreeBytes", "999999999999999"));
        var (_, key) = await app.CreateTokenAsync();
        var client = app.NewClient();

        var response = await client.SendAsync(TestHttp.Upload(key, Encoding.UTF8.GetBytes("x")));

        Assert.Equal(HttpStatusCode.InsufficientStorage, response.StatusCode);
        Assert.Equal(ErrorCodes.DiskLow, await TestHttp.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task 幂等键重放返回同一个id且不重复占盘()
    {
        using var app = new TestApp();
        var (token, key) = await app.CreateTokenAsync();
        var client = app.NewClient();
        var body = Encoding.UTF8.GetBytes("idempotent body");

        var first = await TestHttp.UploadOkAsync(client, key, body, idempotencyKey: "key-1");
        var second = await TestHttp.UploadOkAsync(client, key, body, idempotencyKey: "key-1");

        Assert.Equal(first.Id, second.Id);
        var blobsDir = Path.Combine(app.DataDir, "blobs");
        Assert.Single(Directory.GetFiles(blobsDir, "*", SearchOption.AllDirectories));
        Assert.Equal(body.Length, (await app.GetService<SqliteIndex>().GetTokenAsync(token.Id))!.UsedBytes);
    }

    [Fact]
    public async Task 幂等键配上不同内容返回409()
    {
        using var app = new TestApp();
        var (_, key) = await app.CreateTokenAsync();
        var client = app.NewClient();

        await TestHttp.UploadOkAsync(client, key, Encoding.UTF8.GetBytes("first"), idempotencyKey: "k");
        var response = await client.SendAsync(
            TestHttp.Upload(key, Encoding.UTF8.GetBytes("second"), idempotencyKey: "k"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ErrorCodes.IdempotencyConflict, await TestHttp.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task 文件名的路径穿越与控制字符被清洗()
    {
        using var app = new TestApp();
        var (_, key) = await app.CreateTokenAsync();
        var client = app.NewClient();

        var upload = await TestHttp.UploadOkAsync(client, key, Encoding.UTF8.GetBytes("x"), "../../etc/pa:sswd");
        var blob = await app.GetService<SqliteIndex>().GetBlobAsync(upload.Id);

        Assert.Equal("etcpasswd", blob!.Name);
    }

    [Fact]
    public async Task 用name查询参数传UTF8文件名()
    {
        using var app = new TestApp();
        var (_, key) = await app.CreateTokenAsync();
        var client = app.NewClient();

        var response = await client.SendAsync(
            TestHttp.Upload(key, Encoding.UTF8.GetBytes("x"), url: "/v1/blobs?name=%E6%8A%A5%E5%91%8A.md", fileName: null));
        var upload = await response.Content.ReadFromJsonAsync<UploadResponse>();
        var blob = await app.GetService<SqliteIndex>().GetBlobAsync(upload!.Id);

        Assert.Equal("报告.md", blob!.Name);
    }

    [Fact]
    public async Task 超长文件名被截断到200字符()
    {
        using var app = new TestApp();
        var (_, key) = await app.CreateTokenAsync();
        var client = app.NewClient();

        var upload = await TestHttp.UploadOkAsync(client, key, Encoding.UTF8.GetBytes("x"), new string('n', 300));
        var blob = await app.GetService<SqliteIndex>().GetBlobAsync(upload.Id);

        Assert.Equal(200, blob!.Name!.Length);
    }

    [Fact]
    public async Task 空文件可以上传且大小为0()
    {
        using var app = new TestApp();
        var (_, key) = await app.CreateTokenAsync();
        var client = app.NewClient();

        var upload = await TestHttp.UploadOkAsync(client, key, []);

        Assert.Equal(0, upload.Size);
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", upload.Sha256);
    }

    [Fact]
    public async Task 没有ContentLength返回411()
    {
        using var app = new TestApp();
        var (_, key) = await app.CreateTokenAsync();
        var client = app.NewClient();

        var response = await client.SendAsync(
            TestHttp.Upload(key, Encoding.UTF8.GetBytes("chunked body"), sendContentLength: false));

        Assert.Equal(HttpStatusCode.LengthRequired, response.StatusCode);
        Assert.Equal(ErrorCodes.LengthRequired, await TestHttp.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task 上传限流生效()
    {
        using var app = new TestApp(("security:rateLimitPerMinute", "2"));
        var (_, key) = await app.CreateTokenAsync();
        var client = app.NewClient();

        await TestHttp.UploadOkAsync(client, key, Encoding.UTF8.GetBytes("1"));
        await TestHttp.UploadOkAsync(client, key, Encoding.UTF8.GetBytes("2"));
        var third = await client.SendAsync(TestHttp.Upload(key, Encoding.UTF8.GetBytes("3")));

        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.Equal(ErrorCodes.RateLimited, await TestHttp.ErrorCodeAsync(third));
    }

    [Fact]
    public async Task 幂等键在文件过期后仍可重新上传()
    {
        using var app = new TestApp();
        var (_, key) = await app.CreateTokenAsync();
        var client = app.NewClient();
        var body = Encoding.UTF8.GetBytes("retry-after-expiry");

        var first = await TestHttp.UploadOkAsync(client, key, body, idempotencyKey: "retry-1");
        await app.ExpireBlobAsync(first.Id);

        // 旧实现这里会永久返回 409（幂等行指向的文件早已不在），AI 重试就卡死了
        var second = await TestHttp.UploadOkAsync(client, key, body, idempotencyKey: "retry-1");

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(first.Sha256, second.Sha256);
    }

    [Fact]
    public async Task 幂等键在文件被删除后仍可重新上传()
    {
        using var app = new TestApp();
        var (_, writeKey) = await app.CreateTokenAsync();
        var client = app.NewClient();
        var body = Encoding.UTF8.GetBytes("retry-after-delete");

        var first = await TestHttp.UploadOkAsync(client, writeKey, body, idempotencyKey: "retry-2");
        // 删除接口已是管理端专属（见 AdminTests），本用例只关心「幂等键指向的 blob 不在了」这个状态
        Assert.True(await app.GetService<SqliteIndex>().DeleteBlobAsync(first.Id));

        var second = await TestHttp.UploadOkAsync(client, writeKey, body, idempotencyKey: "retry-2");

        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task 并发上传不会突破配额()
    {
        using var app = new TestApp();
        var (token, key) = await app.CreateTokenAsync(quotaBytes: 5001);
        var client = app.NewClient();
        var body = new byte[5000];

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => client.SendAsync(TestHttp.Upload(key, body))));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(3, responses.Count(r => r.StatusCode == HttpStatusCode.InsufficientStorage));
        Assert.Equal(5000, (await app.GetService<SqliteIndex>().GetTokenAsync(token.Id))!.UsedBytes);
    }

    [Theory]
    [InlineData("caf\u00e9.md", "caf\u00e9.md")]
    [InlineData("\u00e6\u008a\u00a5\u00e5\u0091\u008a.md", "报告.md")]
    public async Task 文件名头按Latin1与UTF8两种发法都能正确还原(string headerValue, string expected)
    {
        using var app = new TestApp();
        var (_, key) = await app.CreateTokenAsync();
        var client = app.NewClient();

        var upload = await TestHttp.UploadOkAsync(client, key, Encoding.UTF8.GetBytes("x"), headerValue);
        var blob = await app.GetService<SqliteIndex>().GetBlobAsync(upload.Id);

        Assert.Equal(expected, blob!.Name);
        Assert.DoesNotContain('\uFFFD', blob.Name!);
    }

    [Fact]
    public async Task 上传写入审计与最后使用时间()
    {
        using var app = new TestApp();
        var (token, key) = await app.CreateTokenAsync();
        var client = app.NewClient();

        var upload = await TestHttp.UploadOkAsync(client, key, Encoding.UTF8.GetBytes("audited"));

        var audit = await app.GetService<SqliteIndex>().ListAuditAsync(10);
        Assert.Contains(audit, a => a.Action == "blob.upload" && a.BlobId == upload.Id);
        Assert.NotNull((await app.GetService<SqliteIndex>().GetTokenAsync(token.Id))!.LastUsedAt);
    }
}

using System.Net;
using System.Security.Cryptography;
using System.Text;
using AnyDrop.Server;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace AnyDrop.Tests;

public sealed class DownloadRangeTests
{
    private static async Task<(TestApp App, HttpClient Client, string WriteKey, string ReadKey, byte[] Body, UploadResponse Upload)>
        SeedAsync(byte[]? payload = null)
    {
        var app = new TestApp();
        var (_, writeKey) = await app.CreateTokenAsync();
        var (_, readKey) = await app.CreateReadTokenAsync();
        var client = app.NewClient();
        var body = payload ?? RandomNumberGenerator.GetBytes(3000);
        var upload = await TestHttp.UploadOkAsync(client, writeKey, body, "data.bin");
        return (app, client, writeKey, readKey, body, upload);
    }

    [Fact]
    public async Task 完整下载内容与响应头正确()
    {
        var (app, client, _, readKey, body, upload) = await SeedAsync();
        using var _app = app;

        var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"/v1/blobs/{upload.Id}").WithBearer(readKey));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(body, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(upload.Sha256, response.Headers.GetValues("X-File-Sha256").Single());
        Assert.Equal("bytes", response.Headers.GetValues("Accept-Ranges").Single());
        Assert.Contains("data.bin", response.Content.Headers.GetValues("Content-Disposition").Single());
        Assert.Equal(body.Length, response.Content.Headers.ContentLength);
        var cacheControl = response.Headers.CacheControl!.ToString();
        Assert.Contains("no-store", cacheControl);
        Assert.Contains("private", cacheControl);
        Assert.Equal(1, (await app.GetService<SqliteIndex>().GetBlobAsync(upload.Id))!.DownloadCount);
    }

    [Fact]
    public async Task HEAD只返回元数据()
    {
        var (app, client, _, readKey, body, upload) = await SeedAsync();
        using var _app = app;

        var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Head, $"/v1/blobs/{upload.Id}").WithBearer(readKey));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(body.Length, response.Content.Headers.ContentLength);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(0, (await app.GetService<SqliteIndex>().GetBlobAsync(upload.Id))!.DownloadCount);
    }

    [Theory]
    [InlineData("bytes=0-9", 0, 10)]
    [InlineData("bytes=2990-", 2990, 10)]
    [InlineData("bytes=-10", 2990, 10)]
    [InlineData("bytes=1000-1999", 1000, 1000)]
    [InlineData("bytes=0-0", 0, 1)]
    [InlineData("bytes=2999-2999", 2999, 1)]
    [InlineData("bytes=0-99999", 0, 3000)]
    public async Task 区间下载返回正确的切片(string header, int start, int length)
    {
        var (app, client, _, readKey, body, upload) = await SeedAsync();
        using var _app = app;

        var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/blobs/{upload.Id}").WithBearer(readKey);
        request.Headers.TryAddWithoutValidation("Range", header);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(body.Skip(start).Take(length).ToArray(), await response.Content.ReadAsByteArrayAsync());
        Assert.Equal($"bytes {start}-{start + length - 1}/{body.Length}",
            response.Content.Headers.GetValues("Content-Range").Single());
    }

    [Theory]
    [InlineData("bytes=3000-")]
    [InlineData("bytes=5000-6000")]
    [InlineData("bytes=abc")]
    [InlineData("bytes=0-1,5-6")]
    [InlineData("bytes=-0")]
    [InlineData("items=0-5")]
    [InlineData("bytes=10-5")]
    public async Task 非法区间返回416(string header)
    {
        var (app, client, _, readKey, body, upload) = await SeedAsync();
        using var _app = app;

        var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/blobs/{upload.Id}").WithBearer(readKey);
        request.Headers.TryAddWithoutValidation("Range", header);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
        Assert.Equal($"bytes */{body.Length}", response.Content.Headers.GetValues("Content-Range").Single());
        Assert.Equal(ErrorCodes.RangeNotSatisfiable, await TestHttp.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task 无凭证下载返回401()
    {
        var (app, client, _, _, _, upload) = await SeedAsync();
        using var _app = app;

        var response = await client.GetAsync($"/v1/blobs/{upload.Id}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task 只写密钥下载返回403()
    {
        var (app, client, writeKey, _, _, upload) = await SeedAsync();
        using var _app = app;

        var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"/v1/blobs/{upload.Id}").WithBearer(writeKey));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ErrorCodes.ScopeDenied, await TestHttp.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task 命名空间不匹配返回403()
    {
        using var app = new TestApp();
        var (_, writeKey) = await app.CreateTokenAsync(ns: "to-nas");
        var (_, otherReadKey) = await app.CreateReadTokenAsync(ns: "other");
        var client = app.NewClient();
        var upload = await TestHttp.UploadOkAsync(client, writeKey, Encoding.UTF8.GetBytes("x"));

        var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"/v1/blobs/{upload.Id}").WithBearer(otherReadKey));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ErrorCodes.ScopeDenied, await TestHttp.ErrorCodeAsync(response));
    }

    [Theory]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("short")]
    public async Task 非法或未知的id返回404(string id)
    {
        var (app, client, _, readKey, _, _) = await SeedAsync();
        using var _app = app;

        var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"/v1/blobs/{id}").WithBearer(readKey));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ErrorCodes.NotFound, await TestHttp.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task 过期文件返回404()
    {
        var (app, client, _, readKey, _, upload) = await SeedAsync();
        using var _app = app;
        await app.ExpireBlobAsync(upload.Id);

        var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"/v1/blobs/{upload.Id}").WithBearer(readKey));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task 浏览器输密钥后可下载且错误密钥被拒()
    {
        var (app, client, _, readKey, body, upload) = await SeedAsync();
        using var _app = app;
        var browser = app.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        var page = await browser.GetAsync($"/f/{upload.Id}");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("需要读取密钥", await page.Content.ReadAsStringAsync());

        var wrong = await browser.PostAsync($"/f/{upload.Id}",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("key", "ad_wrongkeywrongkeywrongkeywrongkey")]));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Contains("密钥无效", await wrong.Content.ReadAsStringAsync());

        var ok = await browser.PostAsync($"/f/{upload.Id}",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("key", readKey)]));
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        Assert.Contains(CookieNames.Download, ok.Headers.GetValues("Set-Cookie").Single());

        var card = await browser.GetAsync($"/f/{upload.Id}");
        Assert.Equal(HttpStatusCode.OK, card.StatusCode);
        Assert.Contains("下载文件", await card.Content.ReadAsStringAsync());

        var download = await browser.GetAsync($"/v1/blobs/{upload.Id}");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(body, await download.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task 下载响应类型固定为二进制流()
    {
        using var app = new TestApp();
        var (_, writeKey) = await app.CreateTokenAsync();
        var (_, readKey) = await app.CreateReadTokenAsync();
        var client = app.NewClient();
        // 上传者可以随便声明 Content-Type，但不能让它决定下载响应类型
        var upload = await TestHttp.UploadOkAsync(client, writeKey, Encoding.UTF8.GetBytes("<script>alert(1)</script>"),
            "evil.html", contentType: "text/html");

        var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"/v1/blobs/{upload.Id}").WithBearer(readKey));

        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("attachment", response.Content.Headers.GetValues("Content-Disposition").Single());
    }

    [Fact]
    public async Task 密文被截断到块边界时返回500而不是半截200()
    {
        // 配置下限是 4096，用它做多块文件
        using var app = new TestApp(("crypto:chunkSizeBytes", "4096"));
        var (_, writeKey) = await app.CreateTokenAsync();
        var (_, readKey) = await app.CreateReadTokenAsync();
        var client = app.NewClient();
        var upload = await TestHttp.UploadOkAsync(client, writeKey, RandomNumberGenerator.GetBytes(12000));

        // 正好砍掉一整块（明文 4096 + tag 16）：按旧逻辑 file 反推出的尺寸依然"合法"
        var path = app.BlobPath(upload.Id);
        var original = await File.ReadAllBytesAsync(path);
        await File.WriteAllBytesAsync(path, original[..^(4096 + BlobFormat.TagSize)]);

        var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"/v1/blobs/{upload.Id}").WithBearer(readKey));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(ErrorCodes.IntegrityError, await TestHttp.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task 中间块损坏不会被当成完整文件交付()
    {
        using var app = new TestApp(("crypto:chunkSizeBytes", "4096"));
        var (_, writeKey) = await app.CreateTokenAsync();
        var (_, readKey) = await app.CreateReadTokenAsync();
        var client = app.NewClient();
        var body = RandomNumberGenerator.GetBytes(12000);
        var upload = await TestHttp.UploadOkAsync(client, writeKey, body);

        // 破坏中间那一块：正文已经开始写出后才会失败
        var path = app.BlobPath(upload.Id);
        var bytes = await File.ReadAllBytesAsync(path);
        var middleChunk = BlobFormat.HeaderSize + (4096 + BlobFormat.TagSize) + 10;
        bytes[middleChunk] ^= 0x01;
        await File.WriteAllBytesAsync(path, bytes);

        byte[]? received = null;
        try
        {
            var response = await client.SendAsync(
                new HttpRequestMessage(HttpMethod.Get, $"/v1/blobs/{upload.Id}").WithBearer(readKey));
            received = await response.Content.ReadAsByteArrayAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            received = null;   // 连接被主动中断，符合预期
        }

        Assert.True(received is null || received.Length < body.Length,
            "损坏的密文既不能报成功，也不能交付完整长度的正文");
    }

    [Fact]
    public async Task 带删除权的密钥可以删除且用量回退()
    {
        var (app, client, _, _, _, upload) = await SeedAsync();
        using var _app = app;
        var (token, readDeleteKey) = await app.CreateReadTokenAsync(canDelete: true);
        var index = app.GetService<SqliteIndex>();
        var blob = await index.GetBlobAsync(upload.Id);
        Assert.Equal(blob!.Size, (await index.GetTokenAsync(blob.TokenId))!.UsedBytes);

        var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Delete, $"/v1/blobs/{upload.Id}").WithBearer(readDeleteKey));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(await index.GetBlobAsync(upload.Id));
        Assert.False(File.Exists(app.BlobPath(upload.Id)));
        Assert.Equal(0, (await index.GetTokenAsync(blob.TokenId))!.UsedBytes);
        Assert.Equal(0, (await index.GetTokenAsync(token.Id))!.UsedBytes);
    }

    [Fact]
    public async Task 无删除权的密钥删除返回403()
    {
        var (app, client, _, readKey, _, upload) = await SeedAsync();
        using var _app = app;

        var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Delete, $"/v1/blobs/{upload.Id}").WithBearer(readKey));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ErrorCodes.ScopeDenied, await TestHttp.ErrorCodeAsync(response));
        Assert.NotNull(await app.GetService<SqliteIndex>().GetBlobAsync(upload.Id));
    }
}

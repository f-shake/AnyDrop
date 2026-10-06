using System.Net;
using System.Net.Http.Json;
using System.Text;
using AnyDrop.Server;
using Xunit;

namespace AnyDrop.Tests;

/// <summary>
/// 管理端浏览器上传（POST /api/admin/files）：只认管理员会话 + CSRF，不需要 Bearer 密钥，
/// 不占用任何密钥的配额，落默认命名空间，用哨兵身份记账。
/// </summary>
public sealed class AdminUploadTests
{
    private static async Task<(HttpClient Client, string Csrf)> LoginAsync(TestApp app)
    {
        await app.SetAdminPasswordAsync();
        var client = app.NewClient();
        var response = await client.PostAsync(
            "/api/admin/login", TestApp.Json(new { username = "admin", password = TestApp.AdminPassword }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var state = await client.GetFromJsonAsync<SessionStateResponse>("/api/admin/session");
        return (client, state!.CsrfToken!);
    }

    /// <summary>文件名走 ?name=（URL 编码）：XHR 的请求头值必须是 ByteString，放不下中文文件名。</summary>
    private static HttpRequestMessage UploadRequest(byte[] body, string? csrf, string? name = null)
    {
        var url = name is null
            ? "/api/admin/files"
            : $"/api/admin/files?name={Uri.EscapeDataString(name)}";
        return TestHttp.Upload(null, body, url: url, fileName: null, csrf: csrf);
    }

    private static async Task<UploadResponse> UploadOkAsync(
        HttpClient client, string csrf, byte[] body, string? name = null)
    {
        var response = await client.SendAsync(UploadRequest(body, csrf, name));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var parsed = await response.Content.ReadFromJsonAsync<UploadResponse>();
        Assert.NotNull(parsed);
        return parsed!;
    }

    private static string[] StoredFiles(TestApp app)
    {
        var blobsDir = Path.Combine(app.DataDir, "blobs");
        return Directory.Exists(blobsDir)
            ? Directory.GetFiles(blobsDir, "*", SearchOption.AllDirectories)
            : [];
    }

    [Fact]
    public async Task 未登录上传返回401且不落任何文件()
    {
        using var app = new TestApp();
        var client = app.NewClient();

        var response = await client.SendAsync(UploadRequest(Encoding.UTF8.GetBytes("x"), csrf: null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(ErrorCodes.InvalidToken, await TestHttp.ErrorCodeAsync(response));
        Assert.Empty(StoredFiles(app));
    }

    [Fact]
    public async Task 缺少CSRF返回403且不落文件()
    {
        using var app = new TestApp();
        var (client, _) = await LoginAsync(app);

        var response = await client.SendAsync(UploadRequest(Encoding.UTF8.GetBytes("x"), csrf: null));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ErrorCodes.CsrfFailed, await TestHttp.ErrorCodeAsync(response));
        Assert.Empty(StoredFiles(app));
    }

    [Fact]
    public async Task CSRF不匹配返回403()
    {
        using var app = new TestApp();
        var (client, _) = await LoginAsync(app);

        var response = await client.SendAsync(UploadRequest(Encoding.UTF8.GetBytes("x"), csrf: "not-the-token"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ErrorCodes.CsrfFailed, await TestHttp.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task 上传成功返回201并出现在文件列表()
    {
        using var app = new TestApp();
        var (client, csrf) = await LoginAsync(app);
        var body = Encoding.UTF8.GetBytes("browser upload 浏览器上传");

        var upload = await UploadOkAsync(client, csrf, body, "报告.txt");

        Assert.Equal(body.Length, upload.Size);
        Assert.Equal(TestHttp.Sha256(body), upload.Sha256);
        Assert.True(Ids.IsBlobId(upload.Id));
        Assert.EndsWith($"/f/{upload.Id}", upload.Url);
        Assert.True(File.Exists(app.BlobPath(upload.Id)));

        var list = await client.GetFromJsonAsync<FileListResponse>("/api/admin/files");
        Assert.Equal(1, list!.Total);
        Assert.Equal(upload.Id, list.Items[0].Id);
        Assert.Equal("报告.txt", list.Items[0].Name);
    }

    [Fact]
    public async Task 磁盘上只有密文()
    {
        using var app = new TestApp();
        var (client, csrf) = await LoginAsync(app);
        var body = Encoding.UTF8.GetBytes("plaintext-marker-绝密内容");

        var upload = await UploadOkAsync(client, csrf, body);

        var onDisk = await File.ReadAllBytesAsync(app.BlobPath(upload.Id));
        Assert.Equal(BlobFormat.TotalFileSize(body.Length, app.Config.Crypto.ChunkSizeBytes), onDisk.Length);
        Assert.DoesNotContain("plaintext-marker", Encoding.UTF8.GetString(onDisk));
    }

    [Fact]
    public async Task 落默认命名空间且记账身份是哨兵()
    {
        using var app = new TestApp();
        var (client, csrf) = await LoginAsync(app);

        var upload = await UploadOkAsync(client, csrf, Encoding.UTF8.GetBytes("x"));

        var blob = await app.GetService<SqliteIndex>().GetBlobAsync(upload.Id, CancellationToken.None);
        Assert.NotNull(blob);
        Assert.Equal(TokenService.DefaultNamespace, blob!.Namespace);
        Assert.Equal(AdminUploadIdentity.TokenId, blob.TokenId);
    }

    [Fact]
    public async Task 上传不占用任何密钥的配额()
    {
        using var app = new TestApp();
        var (client, csrf) = await LoginAsync(app);
        var (token, _) = await app.CreateTokenAsync();

        await UploadOkAsync(client, csrf, new byte[4096]);

        var stored = await app.GetService<SqliteIndex>().GetTokenAsync(token.Id, CancellationToken.None);
        Assert.Equal(0, stored!.UsedBytes);
    }

    [Fact]
    public async Task 读密钥能取走管理员上传的文件()
    {
        using var app = new TestApp();
        var (client, csrf) = await LoginAsync(app);
        var (_, readKey) = await app.CreateReadTokenAsync();
        var body = Encoding.UTF8.GetBytes("hand-off to reader 交付给读密钥");

        var upload = await UploadOkAsync(client, csrf, body, "handoff.bin");

        // 必须用不带管理员 cookie 的客户端：否则请求同时带 admin 会话，IsAdmin 会绕过命名空间检查，
        // 这条断言就变成永远成立的空测试。
        var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/blobs/{upload.Id}").WithBearer(readKey);
        var response = await app.NewClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(body, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task 异命名空间的读密钥读不到管理员上传的文件()
    {
        using var app = new TestApp();
        var (client, csrf) = await LoginAsync(app);
        var (_, otherKey) = await app.CreateReadTokenAsync(ns: "other");

        var upload = await UploadOkAsync(client, csrf, Encoding.UTF8.GetBytes("x"));

        var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/blobs/{upload.Id}").WithBearer(otherKey);
        var response = await app.NewClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ErrorCodes.ScopeDenied, await TestHttp.ErrorCodeAsync(response));
    }

    /// <summary>
    /// 记录既有语义：命名空间是共享边界，删除权只看密钥自身的 canDelete，
    /// 管理员上传的文件**不会**因此获得额外保护——要改这条语义必须先改设计。
    /// </summary>
    [Fact]
    public async Task 同命名空间的可删密钥能删掉管理员上传的文件()
    {
        using var app = new TestApp();
        var (client, csrf) = await LoginAsync(app);
        var (_, pullKey) = await app.CreateReadTokenAsync(canDelete: true);

        var upload = await UploadOkAsync(client, csrf, Encoding.UTF8.GetBytes("x"));

        var request = new HttpRequestMessage(HttpMethod.Delete, $"/v1/blobs/{upload.Id}").WithBearer(pullKey);
        var response = await app.NewClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(StoredFiles(app));
    }

    [Fact]
    public async Task 超过上限返回413()
    {
        using var app = new TestApp(("server:maxUploadBytes", "16"));
        var (client, csrf) = await LoginAsync(app);

        var response = await client.SendAsync(UploadRequest(new byte[17], csrf));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(ErrorCodes.FileTooLarge, await TestHttp.ErrorCodeAsync(response));
        Assert.Empty(StoredFiles(app));
    }

    [Fact]
    public async Task 恰好等于上限可以上传()
    {
        using var app = new TestApp(("server:maxUploadBytes", "16"));
        var (client, csrf) = await LoginAsync(app);

        var upload = await UploadOkAsync(client, csrf, new byte[16]);

        Assert.Equal(16, upload.Size);
    }

    [Fact]
    public async Task 零字节文件可以上传()
    {
        using var app = new TestApp();
        var (client, csrf) = await LoginAsync(app);

        var upload = await UploadOkAsync(client, csrf, []);

        Assert.Equal(0, upload.Size);
        Assert.True(File.Exists(app.BlobPath(upload.Id)));
    }

    [Fact]
    public async Task 中文文件名通过name参数保存()
    {
        using var app = new TestApp();
        var (client, csrf) = await LoginAsync(app);

        var upload = await UploadOkAsync(client, csrf, Encoding.UTF8.GetBytes("x"), "季度报告 2026.txt");

        var blob = await app.GetService<SqliteIndex>().GetBlobAsync(upload.Id, CancellationToken.None);
        Assert.Equal("季度报告 2026.txt", blob!.Name);
    }

    [Fact]
    public async Task 文件名被净化且无名字时为null()
    {
        using var app = new TestApp();
        var (client, csrf) = await LoginAsync(app);

        var dirty = await UploadOkAsync(client, csrf, Encoding.UTF8.GetBytes("x"), "../../etc/pa:sswd*?.txt");
        var unnamed = await UploadOkAsync(client, csrf, Encoding.UTF8.GetBytes("x"));

        var index = app.GetService<SqliteIndex>();
        Assert.Equal("etcpasswd.txt", (await index.GetBlobAsync(dirty.Id, CancellationToken.None))!.Name);
        Assert.Null((await index.GetBlobAsync(unnamed.Id, CancellationToken.None))!.Name);
    }

    [Fact]
    public async Task 审计记为管理员上传()
    {
        using var app = new TestApp();
        var (client, csrf) = await LoginAsync(app);
        var upload = await UploadOkAsync(client, csrf, Encoding.UTF8.GetBytes("x"), "审计.txt");

        var audits = await app.GetService<SqliteIndex>().ListAuditAsync(50, CancellationToken.None);

        var row = Assert.Single(audits, a => a.Action == "blob.upload");
        Assert.Equal(AdminUploadIdentity.TokenId, row.TokenId);
        Assert.Equal(TokenService.DefaultNamespace, row.Namespace);
        Assert.Equal(upload.Id, row.BlobId);
        Assert.Equal("审计.txt", row.Detail);
    }

    [Fact]
    public async Task 磁盘水位越线返回507()
    {
        using var app = new TestApp(("storage:minFreeBytes", "999999999999999"));
        var (client, csrf) = await LoginAsync(app);

        var response = await client.SendAsync(UploadRequest(Encoding.UTF8.GetBytes("x"), csrf));

        Assert.Equal(HttpStatusCode.InsufficientStorage, response.StatusCode);
        Assert.Equal(ErrorCodes.DiskLow, await TestHttp.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task 重构后密钥上传仍照常占用配额()
    {
        using var app = new TestApp();
        var (token, key) = await app.CreateTokenAsync();
        var client = app.NewClient();
        var body = Encoding.UTF8.GetBytes("token upload still counts");

        await TestHttp.UploadOkAsync(client, key, body);

        var stored = await app.GetService<SqliteIndex>().GetTokenAsync(token.Id, CancellationToken.None);
        Assert.Equal(body.Length, stored!.UsedBytes);
    }

    [Fact]
    public async Task 删除管理员上传的文件不会失败()
    {
        using var app = new TestApp();
        var (client, csrf) = await LoginAsync(app);
        var upload = await UploadOkAsync(client, csrf, Encoding.UTF8.GetBytes("x"));

        var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/admin/files/{upload.Id}");
        request.Headers.TryAddWithoutValidation("X-CSRF-Token", csrf);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(StoredFiles(app));
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using AnyDrop.Server;
using Xunit;

namespace AnyDrop.Tests;

public sealed class AdminTests
{
    private static async Task<HttpClient> LoginAsync(TestApp app, string password = TestApp.AdminPassword)
    {
        await app.SetAdminPasswordAsync(password);
        var client = app.NewClient();
        var response = await client.PostAsync("/api/admin/login", TestApp.Json(new { username = "admin", password }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<string> CsrfAsync(HttpClient client)
    {
        var state = await client.GetFromJsonAsync<SessionStateResponse>("/api/admin/session");
        return state!.CsrfToken!;
    }

    private static HttpRequestMessage Mutate(HttpMethod method, string url, string csrf, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("X-CSRF-Token", csrf);
        if (body is not null) request.Content = TestApp.Json(body);
        return request;
    }

    [Fact]
    public async Task 未设置密码时登录返回503()
    {
        using var app = new TestApp();
        var client = app.NewClient();

        var response = await client.PostAsync("/api/admin/login", TestApp.Json(new { username = "admin", password = "x" }));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(AdminAuth.NotInitializedCode, await TestHttp.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task 登录成功后会话与CSRF可用()
    {
        using var app = new TestApp();
        var client = await LoginAsync(app);

        var state = await client.GetFromJsonAsync<SessionStateResponse>("/api/admin/session");

        Assert.True(state!.Authenticated);
        Assert.Equal("admin", state.Kind);
        Assert.False(string.IsNullOrWhiteSpace(state.CsrfToken));
    }

    [Fact]
    public async Task 密码错误返回401且连续失败会锁定()
    {
        using var app = new TestApp(("security:maxLoginFailures", "3"), ("security:lockoutMinutes", "15"));
        await app.SetAdminPasswordAsync();
        var client = app.NewClient();

        for (var i = 0; i < 3; i++)
        {
            var response = await client.PostAsync("/api/admin/login", TestApp.Json(new { username = "admin", password = "wrong-password" }));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal(ErrorCodes.InvalidCredentials, await TestHttp.ErrorCodeAsync(response));
        }

        var locked = await client.PostAsync("/api/admin/login", TestApp.Json(new { username = "admin", password = TestApp.AdminPassword }));
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.Equal(ErrorCodes.LoginLocked, await TestHttp.ErrorCodeAsync(locked));
    }

    [Fact]
    public async Task 未登录访问管理接口返回401()
    {
        using var app = new TestApp();
        var client = app.NewClient();

        foreach (var url in new[] { "/api/admin/files", "/api/admin/tokens", "/api/admin/audit" })
        {
            var response = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task 缺少CSRF的写操作返回403()
    {
        using var app = new TestApp();
        var client = await LoginAsync(app);

        var response = await client.PostAsync("/api/admin/tokens", TestApp.Json(new { name = "x" }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ErrorCodes.CsrfFailed, await TestHttp.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task 文件列表支持分页与搜索()
    {
        using var app = new TestApp();
        var (_, key) = await app.CreateTokenAsync();
        var client = await LoginAsync(app);
        for (var i = 0; i < 3; i++)
            await TestHttp.UploadOkAsync(client, key, Encoding.UTF8.GetBytes($"body-{i}"), $"report-{i}.md");
        await TestHttp.UploadOkAsync(client, key, Encoding.UTF8.GetBytes("img"), "photo.png");

        var all = await client.GetFromJsonAsync<FileListResponse>("/api/admin/files?page=1&size=2");
        Assert.Equal(4, all!.Total);
        Assert.Equal(2, all.Items.Count);

        var filtered = await client.GetFromJsonAsync<FileListResponse>("/api/admin/files?q=report");
        Assert.Equal(3, filtered!.Total);
        Assert.All(filtered.Items, item => Assert.Contains("report", item.Name));
    }

    [Fact]
    public async Task 改期与pin与删除()
    {
        using var app = new TestApp();
        var (_, key) = await app.CreateTokenAsync();
        var client = await LoginAsync(app);
        var csrf = await CsrfAsync(client);
        var upload = await TestHttp.UploadOkAsync(client, key, Encoding.UTF8.GetBytes("x"), "a.txt");

        var extend = await client.SendAsync(Mutate(HttpMethod.Post, $"/api/admin/files/{upload.Id}/expiry", csrf, new { days = 7 }));
        Assert.Equal(HttpStatusCode.OK, extend.StatusCode);
        var extended = await extend.Content.ReadFromJsonAsync<FileInfoDto>();
        Assert.True(Time.TryParse(extended!.ExpiresAt, out var expires));
        Assert.True(expires > DateTimeOffset.UtcNow.AddDays(6));

        var pin = await client.SendAsync(Mutate(HttpMethod.Post, $"/api/admin/files/{upload.Id}/pin", csrf, new { pinned = true }));
        Assert.Equal(HttpStatusCode.OK, pin.StatusCode);
        Assert.True((await pin.Content.ReadFromJsonAsync<FileInfoDto>())!.Pinned);

        var badDays = await client.SendAsync(Mutate(HttpMethod.Post, $"/api/admin/files/{upload.Id}/expiry", csrf, new { days = 0 }));
        Assert.Equal(HttpStatusCode.BadRequest, badDays.StatusCode);

        var delete = await client.SendAsync(Mutate(HttpMethod.Delete, $"/api/admin/files/{upload.Id}", csrf));
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        Assert.Null(await app.GetService<SqliteIndex>().GetBlobAsync(upload.Id));

        var again = await client.SendAsync(Mutate(HttpMethod.Delete, $"/api/admin/files/{upload.Id}", csrf));
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task token的创建列表与撤销()
    {
        using var app = new TestApp();
        var client = await LoginAsync(app);
        var csrf = await CsrfAsync(client);

        var create = await client.SendAsync(Mutate(HttpMethod.Post, "/api/admin/tokens", csrf,
            new CreateTokenRequest("ai-nas", "ai-write", "to-nas", null, null, null, 30, 1024L * 1024 * 1024, 1024L * 1024)));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<CreateTokenResponse>();
        Assert.StartsWith("ad_", created!.Key);
        Assert.Equal("to-nas", created.Token.Namespace);
        Assert.True(created.Token.CanUpload);
        Assert.False(created.Token.CanRead);

        var list = await client.GetFromJsonAsync<TokenListResponse>("/api/admin/tokens");
        Assert.Contains(list!.Items, t => t.Id == created.Token.Id);
        Assert.DoesNotContain(created.Key, list.Items.Select(t => t.KeyPrefix));

        var revoke = await client.SendAsync(Mutate(HttpMethod.Post, $"/api/admin/tokens/{created.Token.Id}/revoke", csrf));
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);

        var after = await client.GetFromJsonAsync<TokenListResponse>("/api/admin/tokens");
        Assert.NotNull(after!.Items.Single(t => t.Id == created.Token.Id).RevokedAt);

        var missing = await client.SendAsync(Mutate(HttpMethod.Post, $"/api/admin/tokens/{Ids.NewTokenId()}/revoke", csrf));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task 创建token的非法入参返回400()
    {
        using var app = new TestApp();
        var client = await LoginAsync(app);
        var csrf = await CsrfAsync(client);

        var response = await client.SendAsync(Mutate(HttpMethod.Post, "/api/admin/tokens", csrf, new { name = "", preset = "ai-write" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task 审计列表按时间倒序且含上传记录()
    {
        using var app = new TestApp();
        var (_, key) = await app.CreateTokenAsync();
        var client = await LoginAsync(app);
        var upload = await TestHttp.UploadOkAsync(client, key, Encoding.UTF8.GetBytes("x"), "a.txt");

        var audit = await client.GetFromJsonAsync<AuditListResponse>("/api/admin/audit?limit=20");

        Assert.Contains(audit!.Items, item => item.Action == "blob.upload" && item.BlobId == upload.Id);
        Assert.Equal("blob.upload", audit.Items[0].Action);
    }

    [Fact]
    public async Task 管理端注销缺CSRF时返回403且会话保留()
    {
        using var app = new TestApp();
        var client = await LoginAsync(app);

        var response = await client.PostAsync("/api/admin/logout", TestApp.Json(new { }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ErrorCodes.CsrfFailed, await TestHttp.ErrorCodeAsync(response));
        var state = await client.GetFromJsonAsync<SessionStateResponse>("/api/admin/session");
        Assert.True(state!.Authenticated);
    }

    [Fact]
    public async Task 下载会话注销不需要CSRF()
    {
        using var app = new TestApp();
        var (_, writeKey) = await app.CreateTokenAsync();
        var (_, readKey) = await app.CreateReadTokenAsync();
        var client = app.NewClient();
        var upload = await TestHttp.UploadOkAsync(client, writeKey, Encoding.UTF8.GetBytes("x"));

        // 有意不要求 CSRF：这是 AGENT_UPLOAD.md 文档化的脚本调用路径，且代价仅是重输读密钥
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/session", TestApp.Json(new { key = readKey }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/v1/blobs/{upload.Id}")).StatusCode);

        var logout = await client.DeleteAsync("/api/session");

        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/v1/blobs/{upload.Id}")).StatusCode);
    }

    [Fact]
    public async Task 退出登录后会话失效()
    {
        using var app = new TestApp();
        var client = await LoginAsync(app);
        var csrf = await CsrfAsync(client);

        var logout = await client.SendAsync(Mutate(HttpMethod.Post, "/api/admin/logout", csrf));
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);

        var state = await client.GetFromJsonAsync<SessionStateResponse>("/api/admin/session");
        Assert.False(state!.Authenticated);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/files")).StatusCode);
    }

    [Fact]
    public async Task 读取密钥可以通过API建立下载会话()
    {
        using var app = new TestApp();
        var (_, writeKey) = await app.CreateTokenAsync();
        var (_, readKey) = await app.CreateReadTokenAsync();
        var client = app.NewClient();
        var upload = await TestHttp.UploadOkAsync(client, writeKey, Encoding.UTF8.GetBytes("session-body"));

        var session = await client.PostAsync("/api/session", TestApp.Json(new { key = readKey }));
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        var body = await session.Content.ReadFromJsonAsync<SessionResponse>();
        Assert.Equal("download", body!.Kind);

        var anonymous = app.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/v1/blobs/{upload.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/v1/blobs/{upload.Id}")).StatusCode);
    }

    [Fact]
    public async Task 管理页在未构建前端时给兜底页_构建后给SPA与静态资源()
    {
        using var app = new TestApp();
        var client = app.NewClient();

        var response = await client.GetAsync("/admin");
        var html = await response.Content.ReadAsStringAsync();

        if (!app.GetService<WebAssetStore>().HasUi)
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Contains("管理界面未构建", html);
            return;
        }

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<div id=\"app\">", html);

        // index.html 里引用的 JS/CSS 必须真的能取到（MapFallback 带 :nonfile 约束，静态资源要单独路由）
        var index = await client.GetStringAsync("/");
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(index, "/drop/assets/[A-Za-z0-9._-]+"))
        {
            var assetPath = match.Value.Replace("/drop", string.Empty);
            var asset = await client.GetAsync(assetPath);
            Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
            Assert.True(asset.Content.Headers.ContentLength > 0, $"{assetPath} 内容为空");
        }

        var missing = await client.GetAsync("/assets/does-not-exist.js");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task 路径前缀下的接口与回退白名单()
    {
        using var app = new TestApp(
            ("server:pathBase", "/drop"),
            ("server:publicBaseUrl", "https://example.com/drop"));
        var (_, key) = await app.CreateTokenAsync();
        var client = app.NewClient();

        var upload = await TestHttp.UploadOkAsync(client, key, Encoding.UTF8.GetBytes("prefixed"), url: "/drop/v1/blobs");

        Assert.StartsWith("https://example.com/drop/f/", upload.Url);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/drop/f/{upload.Id}")).StatusCode);

        var api404 = await client.GetAsync("/drop/v1/nope");
        Assert.Equal(HttpStatusCode.NotFound, api404.StatusCode);
        Assert.Equal("application/json", api404.Content.Headers.ContentType!.MediaType);

        var page = await client.GetAsync("/drop/admin");
        if (app.GetService<WebAssetStore>().HasUi)
        {
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        }
        else
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, page.StatusCode);
        }
    }

    [Fact]
    public async Task 登录有与IP和用户名无关的全局限流()
    {
        using var app = new TestApp(("security:loginGlobalPerMinute", "2"));
        await app.SetAdminPasswordAsync();
        var client = app.NewClient();

        for (var i = 0; i < 2; i++)
        {
            // 用户名每次都不一样：靠 userKey 的窗口轮换绕不过全局闸门
            var response = await client.PostAsync("/api/admin/login",
                TestApp.Json(new { username = $"u{i}", password = "wrong-password" }));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        var third = await client.PostAsync("/api/admin/login",
            TestApp.Json(new { username = "u9", password = "wrong-password" }));

        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.Equal(ErrorCodes.RateLimited, await TestHttp.ErrorCodeAsync(third));
    }

    [Fact]
    public async Task 超大页码被夹住而不是静默返回第一页()
    {
        using var app = new TestApp();
        var (_, key) = await app.CreateTokenAsync();
        var client = await LoginAsync(app);
        await TestHttp.UploadOkAsync(client, key, Encoding.UTF8.GetBytes("x"), "a.txt");

        var page = await client.GetFromJsonAsync<FileListResponse>("/api/admin/files?page=2147483647&size=200");

        Assert.Equal(1_000_000, page!.Page);
        Assert.Empty(page.Items);
        Assert.Equal(1, page.Total);
    }

    [Fact]
    public async Task 浏览器深链接交给SPA而API客户端仍得JSON404()
    {
        using var app = new TestApp();
        var client = app.NewClient();

        var browserRequest = new HttpRequestMessage(HttpMethod.Get, "/settings");
        browserRequest.Headers.Accept.ParseAdd("text/html,application/xhtml+xml");
        var browser = await client.SendAsync(browserRequest);
        var api = await client.GetAsync("/settings");

        if (app.GetService<WebAssetStore>().HasUi)
        {
            Assert.Equal(HttpStatusCode.OK, browser.StatusCode);
        }
        else
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, browser.StatusCode);
        }
        Assert.Equal(HttpStatusCode.NotFound, api.StatusCode);
        Assert.Equal(ErrorCodes.NotFound, await TestHttp.ErrorCodeAsync(api));
    }

    [Fact]
    public async Task 未定义路径返回404JSON()
    {
        using var app = new TestApp();
        var client = app.NewClient();

        var response = await client.GetAsync("/definitely-not-here");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ErrorCodes.NotFound, await TestHttp.ErrorCodeAsync(response));
    }
}

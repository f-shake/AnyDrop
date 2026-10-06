using System.Globalization;
using System.Net;

namespace AnyDrop.Server;

/// <summary>下载页（服务端直出，不依赖前端构建）与 SPA 回退。</summary>
public static class PageEndpoints
{
    public static void MapPageEndpoints(this WebApplication app)
    {
        app.MapGet("/f/{id}", GetDownloadPageAsync);
        app.MapPost("/f/{id}", SubmitKeyAsync);
        // 必须显式映射：MapFallback 的模板带 :nonfile 约束，带扩展名的请求压根不会进兜底处理器。
        app.MapGet("/assets/{**path}", ServeAssetAsync);
        app.MapFallback(FallbackAsync);
    }

    private static async Task ServeAssetAsync(HttpContext context, WebAssetStore assets)
    {
        var path = context.Request.Path.Value ?? "/";
        if (assets.TryGet(path, out var content, out var contentType))
        {
            await WriteAssetAsync(context, content, contentType, immutable: true);
            return;
        }
        context.Response.StatusCode = StatusCodes.Status404NotFound;
    }

    private static async Task GetDownloadPageAsync(
        HttpContext context,
        string id,
        AppConfig config,
        BlobService blobs,
        SessionStore sessions)
    {
        var cancellationToken = context.RequestAborted;
        var admin = sessions.GetAdmin(context.Request.Cookies[CookieNames.Admin]);
        var session = sessions.GetDownload(context.Request.Cookies[CookieNames.Download]);
        var effective = ToToken(session);

        if (admin is not null || effective is not null)
        {
            var info = await blobs.GetFileInfoAsync(id, effective, admin is not null, cancellationToken);
            if (info.Ok)
            {
                await WriteHtmlAsync(context, RenderFileCard(info.Value!, config));
                return;
            }
        }

        await WriteHtmlAsync(context, RenderKeyForm(id, config, null));
    }

    private static async Task SubmitKeyAsync(
        HttpContext context,
        string id,
        AppConfig config,
        TokenService tokens,
        BlobService blobs,
        SessionStore sessions,
        RateLimiter limiter,
        IpAccessor ipAccessor)
    {
        var cancellationToken = context.RequestAborted;
        var ip = ipAccessor.Get(context);
        if (!limiter.IsAllowed($"dlkey:{ip}", perMinute: 10))
        {
            await WriteHtmlAsync(context, RenderKeyForm(id, config, "尝试过于频繁，请稍后重试"), StatusCodes.Status429TooManyRequests);
            return;
        }
        if (!context.Request.HasFormContentType)
        {
            await WriteHtmlAsync(context, RenderKeyForm(id, config, "请求格式不正确"), StatusCodes.Status400BadRequest);
            return;
        }

        var form = await context.Request.ReadFormAsync(cancellationToken);
        var key = form["key"].ToString();
        var auth = await tokens.AuthenticateAsync(key, cancellationToken);
        if (!auth.Ok)
        {
            await WriteHtmlAsync(context, RenderKeyForm(id, config, auth.Failure!.Value.Message), StatusCodes.Status401Unauthorized);
            return;
        }

        var token = auth.Value!;
        var info = await blobs.GetFileInfoAsync(id, token, false, cancellationToken);
        if (!info.Ok)
        {
            await WriteHtmlAsync(context, RenderKeyForm(id, config, "密钥无效，或该文件不存在"), StatusCodes.Status401Unauthorized);
            return;
        }

        var session = sessions.CreateDownload(token);
        AuthCookies.Append(context, CookieNames.Download, session.Id, session.ExpiresAt, config);
        context.Response.Redirect($"{config.PathBase}/f/{id}", permanent: false);
    }

    private static async Task FallbackAsync(HttpContext context, AppConfig config, WebAssetStore assets)
    {
        var method = context.Request.Method;
        if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method))
        {
            await WriteJsonAsync(context, StatusCodes.Status404NotFound, ErrorCodes.NotFound, "接口不存在");
            return;
        }

        var path = context.Request.Path.Value ?? "/";

        // 浏览器导航（Accept 含 text/html）也交给 SPA：前端用 history 路由，
        // 深链接刷新不该看到 JSON 404；纯 API 客户端仍拿到 JSON 错误。
        if (path is "/" || path.StartsWith("/admin", StringComparison.Ordinal) ||
            (HttpMethods.IsGet(method) && AcceptsHtml(context.Request)))
        {
            if (assets.HasUi && assets.TryGet("/index.html", out var html, out var htmlType))
            {
                await WriteAssetAsync(context, html, htmlType, immutable: false);
                return;
            }
            var page = PageTemplates.Load(PageTemplates.UiNotBuiltPath);
            await WriteHtmlAsync(context, page, StatusCodes.Status503ServiceUnavailable);
            return;
        }

        await WriteJsonAsync(context, StatusCodes.Status404NotFound, ErrorCodes.NotFound, "接口不存在");
    }

    private static bool AcceptsHtml(HttpRequest request) =>
        request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase);

    private static TokenRecord? ToToken(DownloadSession? session) => session is null        ? null
        : new TokenRecord
        {
            Id = session.TokenId,
            Namespace = session.Namespace,
            CanRead = true,
            CanDelete = session.CanDelete,
        };

    private static async Task WriteAssetAsync(HttpContext context, byte[] content, string contentType, bool immutable)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = contentType;
        context.Response.ContentLength = content.Length;
        context.Response.Headers.CacheControl = immutable
            ? "public, max-age=31536000, immutable"
            : "no-cache";
        if (HttpMethods.IsHead(context.Request.Method)) return;
        await context.Response.Body.WriteAsync(content, context.RequestAborted);
    }

    private static async Task WriteHtmlAsync(HttpContext context, string html, int status = StatusCodes.Status200OK)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsync(html, context.RequestAborted);
    }

    private static Task WriteJsonAsync(HttpContext context, int status, string code, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        return context.Response.WriteAsync(
            $"{{\"error\":{{\"code\":\"{code}\",\"message\":\"{message}\"}}}}", context.RequestAborted);
    }

    public static string RenderKeyForm(string id, AppConfig config, string? error)
    {
        var safeId = WebUtility.HtmlEncode(id);
        var errorBlock = error is null ? "" : $"<p class=\"error\">{WebUtility.HtmlEncode(error)}</p>";
        var content = $"""
            <h1>需要读取密钥</h1>
            <p>文件 <span class="hash">{safeId}</span> 受密钥保护。</p>
            <form method="post" action="{config.PathBase}/f/{safeId}">
              <label for="key">读取密钥</label>
              <input id="key" name="key" type="password" autocomplete="off" required autofocus>
              <button type="submit">验证并继续</button>
            </form>
            {errorBlock}
            """;
        return PageTemplates.Render(PageTemplates.DownloadPath, content);
    }

    public static string RenderFileCard(BlobRecord blob, AppConfig config)
    {
        var name = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(blob.Name) ? blob.Id : blob.Name);
        var content = $"""
            <h1>{name}</h1>
            <p>大小：{HumanSize(blob.Size)}</p>
            <p class="meta">SHA-256：<span class="hash">{blob.Sha256}</span></p>
            <p class="meta">过期时间：{WebUtility.HtmlEncode(blob.ExpiresAt)}（UTC）</p>
            <a class="button" href="{config.PathBase}/v1/blobs/{blob.Id}">下载文件</a>
            """;
        return PageTemplates.Render(PageTemplates.DownloadPath, content);
    }

    public static string HumanSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double value = bytes;
        string[] units = ["KiB", "MiB", "GiB", "TiB"];
        var unit = -1;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value.ToString("0.##", CultureInfo.InvariantCulture)} {units[unit]}";
    }
}

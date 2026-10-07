using System.Globalization;
using System.Net;

namespace AnyDrop.Server;

/// <summary>下载页（服务端直出，不依赖前端构建）与 SPA 回退。</summary>
public static class PageEndpoints
{
    public static void MapPageEndpoints(this WebApplication app)
    {
        app.MapGet("/f/{id}", GetDownloadPageAsync);
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

    /// <summary>
    /// 下载页不需要任何身份：id 就是凭证。取不到就是 404，不再有输密钥的表单。
    /// </summary>
    private static async Task GetDownloadPageAsync(
        HttpContext context,
        string id,
        AppConfig config,
        BlobService blobs)
    {
        var cancellationToken = context.RequestAborted;
        var info = await blobs.GetFileInfoAsync(id, cancellationToken);
        if (!info.Ok)
        {
            await WriteHtmlAsync(context, RenderNotFound(), StatusCodes.Status404NotFound);
            return;
        }
        await WriteHtmlAsync(context, RenderFileCard(info.Value!, config));
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
        // 页面地址里就带着下载凭证：别让它顺着 Referer 漏出去，也别被索引。
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        await context.Response.WriteAsync(html, context.RequestAborted);
    }

    private static Task WriteJsonAsync(HttpContext context, int status, string code, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        return context.Response.WriteAsync(
            $"{{\"error\":{{\"code\":\"{code}\",\"message\":\"{message}\"}}}}", context.RequestAborted);
    }

    public static string RenderNotFound() => PageTemplates.Render(PageTemplates.DownloadPath, """
        <h1>文件不存在或已过期</h1>
        <p>这个链接对应的文件已经不在了。请向给你链接的人确认，或让他重新上传。</p>
        """);

    public static string RenderFileCard(BlobRecord blob, AppConfig config)
    {
        var name = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(blob.Name) ? blob.Id : blob.Name);
        var content = $"""
            <h1>{name}</h1>
            <p>大小：{HumanSize(blob.Size)}</p>
            <p class="meta">SHA-256：<span class="hash">{blob.Sha256}</span></p>
            <p class="meta">过期时间：{WebUtility.HtmlEncode(HumanTime(blob.ExpiresAt))}（UTC）</p>
            <a class="button" href="{config.PathBase}/v1/blobs/{blob.Id}">下载文件</a>
            """;
        return PageTemplates.Render(PageTemplates.DownloadPath, content);
    }

    /// <summary>
    /// ISO（`2026-11-05T14:31:15Z`）→ `2026-11-05 14:31:15`。
    /// 这一页是给外部收件人看的，对方时区未知，所以固定按 UTC 显示并在页面上标注。
    /// 解析失败原样返回：宁可显示得难看，也不要变成空白。
    /// </summary>
    public static string HumanTime(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return "";
        return Time.TryParse(iso, out var parsed)
            ? parsed.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : iso;
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

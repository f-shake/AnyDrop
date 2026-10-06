using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AnyDrop.Server;

public static class BlobEndpoints
{
    private const int MaxFileNameLength = 200;

    public static void MapBlobEndpoints(this WebApplication app)
    {
        app.MapPost("/v1/blobs", UploadAsync);
        // 上传要密钥，下载不要；删除接口随之取消（传错了由管理面板删）。
        app.MapMethods("/v1/blobs/{id}", ["GET", "HEAD"], DownloadAsync);
    }

    private static async Task<IResult> UploadAsync(
        HttpContext context,
        TokenService tokens,
        BlobService blobs,
        RateLimiter limiter,
        IpAccessor ipAccessor)
    {
        var cancellationToken = context.RequestAborted;
        var auth = await tokens.AuthenticateAsync(context.Request.Headers.Authorization.ToString(), cancellationToken);
        if (!auth.Ok) return ApiErrors.From(auth.Failure!.Value);
        var token = auth.Value!;
        if (!limiter.IsAllowed($"upload:{token.Id}"))
            return ApiErrors.Json(429, ErrorCodes.RateLimited, "请求过于频繁，请稍后重试");

        var fileName = ResolveFileName(context.Request);
        var idempotencyKey = context.Request.Headers.TryGetValue("Idempotency-Key", out var idem)
            ? idem.ToString().Trim()
            : null;
        if (idempotencyKey is { Length: 0 }) idempotencyKey = null;
        if (idempotencyKey is { Length: > 128 }) idempotencyKey = idempotencyKey[..128];

        var request = new UploadRequest(
            token,
            fileName,
            context.Request.ContentType,
            idempotencyKey,
            context.Request.ContentLength ?? -1,
            context.Request.Body,
            ipAccessor.Get(context));

        return await ExecuteUploadAsync(request, blobs, cancellationToken);
    }

    /// <summary>
    /// 上传编排的异常统一映射成错误信封。token 上传与管理端上传共用这一份，
    /// 避免两条路径的状态码与文案各自漂移。
    /// </summary>
    public static async Task<IResult> ExecuteUploadAsync(
        UploadRequest request, BlobService blobs, CancellationToken cancellationToken)
    {
        try
        {
            var result = await blobs.UploadAsync(request, cancellationToken);
            return result.Ok
                ? HttpJson.Json(result.Value!, AppJsonContext.Default.UploadResponse, StatusCodes.Status201Created)
                : ApiErrors.From(result.Failure!.Value);
        }
        catch (PayloadTooLargeException)
        {
            return ApiErrors.FileTooLarge(blobs.EffectiveMaxUpload(request.Token));
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return ApiErrors.FileTooLarge(blobs.EffectiveMaxUpload(request.Token));
        }
        catch (BadHttpRequestException)
        {
            return ApiErrors.Json(400, ErrorCodes.IncompleteBody, "请求体不完整，请重试");
        }
        catch (IOException)
        {
            return ApiErrors.Json(400, ErrorCodes.IncompleteBody, "连接中断，请重试");
        }
    }

    /// <summary>
    /// 下载不校验任何身份：blob id 本身就是下载凭证。
    /// 这里只负责 Range、响应头与流式输出。
    /// </summary>
    private static async Task<IResult> DownloadAsync(
        HttpContext context,
        string id,
        BlobService blobs,
        IpAccessor ipAccessor)
    {
        var cancellationToken = context.RequestAborted;
        var opened = await blobs.OpenDownloadAsync(id, cancellationToken);
        if (!opened.Ok) return ApiErrors.From(opened.Failure!.Value);

        var handle = opened.Value!;
        using var file = handle.File;
        var isHead = HttpMethods.IsHead(context.Request.Method);

        long start = 0;
        long length = handle.Blob.Size;
        var isPartial = false;
        var rangeHeader = context.Request.Headers.Range.ToString();
        if (!string.IsNullOrWhiteSpace(rangeHeader))
        {
            if (!TryParseRange(rangeHeader, handle.Blob.Size, out start, out length))
            {
                context.Response.Headers.ContentRange = $"bytes */{handle.Blob.Size}";
                return ApiErrors.Json(416, ErrorCodes.RangeNotSatisfiable, "请求的字节范围无效");
            }
            isPartial = true;
        }

        var response = context.Response;
        response.StatusCode = isPartial ? StatusCodes.Status206PartialContent : StatusCodes.Status200OK;
        // 固定为二进制流：Content-Type 由上传者提供，回给下载方等于让上传者决定响应类型
        // （上传一个 text/html 就能在同源下渲染）。下载一律走 attachment。
        response.ContentType = "application/octet-stream";
        response.ContentLength = length;
        response.Headers.AcceptRanges = "bytes";
        response.Headers["X-File-Sha256"] = handle.Blob.Sha256;
        response.Headers["Content-Disposition"] = ContentDisposition(handle.Blob);
        response.Headers.CacheControl = "private, no-store";
        // 链接本身就是凭证：别让 id 顺着 Referer 漏给下游，也别让爬虫把它收进索引。
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        if (isPartial)
            response.Headers.ContentRange = $"bytes {start}-{start + length - 1}/{handle.Blob.Size}";

        if (isHead) return NoOp;

        try
        {
            await file.CopyRangeAsync(start, length, response.Body, cancellationToken);
        }
        catch
        {
            // 状态码与 Content-Length 已经发出去了，没法再改成 500：
            // 直接断开连接，别让客户端把「截断的 200」当成下载成功。
            context.Abort();
            throw;
        }
        await blobs.RecordDownloadAsync(handle.Blob, ipAccessor.Get(context), cancellationToken);
        return NoOp;
    }

    /// <summary>响应已经手写完毕（含流式正文），不要再让 IResult 覆盖状态码。</summary>
    private sealed class NoOpResult : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext) => Task.CompletedTask;
    }

    private static readonly IResult NoOp = new NoOpResult();

    public static string ContentDisposition(BlobRecord blob)
    {
        var name = string.IsNullOrWhiteSpace(blob.Name) ? blob.Id : blob.Name!;
        var ascii = new StringBuilder(name.Length);
        foreach (var ch in name)
            ascii.Append(ch is >= ' ' and <= '~' && ch != '"' && ch != '\\' ? ch : '_');
        var encoded = Uri.EscapeDataString(name);
        return $"attachment; filename=\"{ascii}\"; filename*=UTF-8''{encoded}";
    }

    /// <summary>支持 bytes=a-b / bytes=a- / bytes=-n；不合法返回 false（调用方回 416）。</summary>
    public static bool TryParseRange(string header, long size, out long start, out long length)
    {
        start = 0;
        length = size;
        if (size <= 0) return false;
        var value = header.Trim();
        if (value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) value = value[6..];
        if (value.Contains(',')) return false;
        var parts = value.Split('-', 2);
        if (parts.Length != 2) return false;
        var (rawStart, rawEnd) = (parts[0].Trim(), parts[1].Trim());

        if (rawStart.Length == 0)
        {
            if (!long.TryParse(rawEnd, NumberStyles.Integer, CultureInfo.InvariantCulture, out var suffix) || suffix <= 0)
                return false;
            if (suffix > size) suffix = size;
            start = size - suffix;
            length = suffix;
            return true;
        }

        if (!long.TryParse(rawStart, NumberStyles.Integer, CultureInfo.InvariantCulture, out start) || start < 0 || start >= size)
            return false;
        if (rawEnd.Length == 0)
        {
            length = size - start;
            return true;
        }
        if (!long.TryParse(rawEnd, NumberStyles.Integer, CultureInfo.InvariantCulture, out var end) || end < start)
            return false;
        if (end >= size) end = size - 1;
        length = end - start + 1;
        return true;
    }

    /// <summary>文件名优先取 ?name=（URL 编码，UTF-8 安全），否则取 X-Filename 并尝试还原 UTF-8。</summary>
    public static string? ResolveFileName(HttpRequest request)
    {
        string? raw = null;
        if (request.Query.TryGetValue("name", out var fromQuery) && fromQuery.Count > 0)
            raw = fromQuery[0];
        else if (request.Headers.TryGetValue("X-Filename", out var fromHeader))
            raw = DecodeHeaderFileName(fromHeader.ToString());
        return SanitizeFileName(raw);
    }

    /// <summary>
    /// HTTP 头按 Latin-1 解码。若原始字节其实是 UTF-8（curl 直接把 UTF-8 塞进头里就是这种情况），
    /// 这里还原回 Unicode；但**只有严格合法的 UTF-8 才还原**，否则原样使用——
    /// 否则客户端真的按 Latin-1 发的名字会被拆成字节再解码，变成一串 U+FFFD。
    /// </summary>
    public static string DecodeHeaderFileName(string raw)
    {
        if (raw.All(c => c <= 0x7F)) return raw;
        var bytes = Encoding.Latin1.GetBytes(raw);
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return raw;
        }
    }

    public static string? SanitizeFileName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var builder = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (char.IsControl(ch) || ch == '\uFFFD') continue;
            if (ch is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|') continue;
            builder.Append(ch);
        }
        var name = builder.ToString().Trim().Trim('.');
        if (name.Length == 0) return null;
        return name.Length > MaxFileNameLength ? name[..MaxFileNameLength] : name;
    }
}

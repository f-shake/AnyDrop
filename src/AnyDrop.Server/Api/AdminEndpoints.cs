namespace AnyDrop.Server;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        app.MapPost("/api/session", CreateSessionAsync);
        app.MapDelete("/api/session", DeleteSessionAsync);

        app.MapGet("/api/admin/session", GetSessionState);
        app.MapPost("/api/admin/login", LoginAsync);
        app.MapPost("/api/admin/logout", LogoutAsync);
        app.MapGet("/api/admin/files", ListFilesAsync);
        app.MapPost("/api/admin/files", UploadFileAsync);
        app.MapPost("/api/admin/files/{id}/expiry", SetExpiryAsync);
        app.MapPost("/api/admin/files/{id}/pin", SetPinnedAsync);
        app.MapDelete("/api/admin/files/{id}", DeleteFileAsync);
        app.MapGet("/api/admin/tokens", ListTokensAsync);
        app.MapPost("/api/admin/tokens", CreateTokenAsync);
        app.MapPost("/api/admin/tokens/{id}/revoke", RevokeTokenAsync);
        app.MapGet("/api/admin/audit", ListAuditAsync);
    }

    private static AdminSession? Admin(HttpContext context, SessionStore sessions) =>
        sessions.GetAdmin(context.Request.Cookies[CookieNames.Admin]);

    private static IResult NotLoggedIn() =>
        ApiErrors.Json(401, ErrorCodes.InvalidToken, "管理会话已失效，请重新登录");

    private static IResult CsrfFailed() =>
        ApiErrors.Json(403, ErrorCodes.CsrfFailed, "CSRF 校验失败，请刷新页面后重试");

    /// <summary>凭读取密钥建立下载会话（供脚本/AI 用；下载页用的是 /f/{id} 表单）。</summary>
    private static async Task<IResult> CreateSessionAsync(
        HttpContext context, AppConfig config, TokenService tokens, SessionStore sessions, RateLimiter limiter, IpAccessor ipAccessor)
    {
        var cancellationToken = context.RequestAborted;
        var ip = ipAccessor.Get(context);
        if (!limiter.IsAllowed($"dlsession:{ip}", perMinute: 10))
            return ApiErrors.Json(429, ErrorCodes.RateLimited, "请求过于频繁，请稍后重试");

        var request = await HttpJson.TryReadAsync(context.Request, AppJsonContext.Default.SessionRequest, cancellationToken);
        if (request is null) return ApiErrors.BadRequest("请求体必须是 JSON");

        var auth = await tokens.AuthenticateAsync(request.Key, cancellationToken);
        if (!auth.Ok) return ApiErrors.From(auth.Failure!.Value);
        var token = auth.Value!;
        if (!token.CanRead) return ApiErrors.Json(403, ErrorCodes.ScopeDenied, "该密钥无权下载文件");

        var session = sessions.CreateDownload(token);
        AuthCookies.Append(context, CookieNames.Download, session.Id, session.ExpiresAt, config);
        return HttpJson.Json(
            new SessionResponse("download", session.Namespace, Time.Iso(session.ExpiresAt)),
            AppJsonContext.Default.SessionResponse);
    }

    private static IResult DeleteSessionAsync(HttpContext context, AppConfig config, SessionStore sessions)
    {
        sessions.RemoveDownload(context.Request.Cookies[CookieNames.Download]);
        AuthCookies.Delete(context, CookieNames.Download, config);
        return HttpJson.Json(new SimpleStatusResponse("logged_out", null), AppJsonContext.Default.SimpleStatusResponse);
    }

    private static IResult GetSessionState(HttpContext context, SessionStore sessions)
    {
        var admin = Admin(context, sessions);
        return HttpJson.Json(
            admin is null
                ? new SessionStateResponse(false, null, null, null)
                : new SessionStateResponse(true, "admin", null, admin.Csrf),
            AppJsonContext.Default.SessionStateResponse);
    }

    private static async Task<IResult> LoginAsync(
        HttpContext context, AppConfig config, AdminAuth auth, SessionStore sessions, IpAccessor ipAccessor)
    {
        var cancellationToken = context.RequestAborted;
        var request = await HttpJson.TryReadAsync(context.Request, AppJsonContext.Default.AdminLoginRequest, cancellationToken);
        if (request is null) return ApiErrors.BadRequest("请求体必须是 JSON");

        var result = await auth.LoginAsync(request, ipAccessor.Get(context), cancellationToken);
        if (!result.Ok) return ApiErrors.From(result.Failure!.Value);

        var session = result.Value!;
        AuthCookies.Append(context, CookieNames.Admin, session.Id, session.ExpiresAt, config);
        return HttpJson.Json(
            new AdminLoginResponse(session.Csrf, session.Username, Time.Iso(session.ExpiresAt)),
            AppJsonContext.Default.AdminLoginResponse);
    }

    private static IResult LogoutAsync(HttpContext context, AppConfig config, SessionStore sessions)
    {
        // 与其它写操作保持一致：有会话就必须带 CSRF（已登出则只是清一次 cookie）。
        var admin = Admin(context, sessions);
        if (admin is not null && !AdminAuth.CheckCsrf(context, admin)) return CsrfFailed();
        sessions.RemoveAdmin(context.Request.Cookies[CookieNames.Admin]);
        AuthCookies.Delete(context, CookieNames.Admin, config);
        return HttpJson.Json(new SimpleStatusResponse("logged_out", null), AppJsonContext.Default.SimpleStatusResponse);
    }

    private static async Task<IResult> ListFilesAsync(
        HttpContext context, SessionStore sessions, BlobService blobs)
    {
        var admin = Admin(context, sessions);
        if (admin is null) return NotLoggedIn();
        var page = int.TryParse(context.Request.Query["page"], out var p) ? p : 1;
        var size = int.TryParse(context.Request.Query["size"], out var s) ? s : 50;
        var query = context.Request.Query["q"].ToString();
        var result = await blobs.ListFilesAsync(query, page, size, context.RequestAborted);
        return HttpJson.Json(result, AppJsonContext.Default.FileListResponse);
    }

    private static async Task<IResult> SetExpiryAsync(
        HttpContext context, string id, SessionStore sessions, BlobService blobs)
    {
        var admin = Admin(context, sessions);
        if (admin is null) return NotLoggedIn();
        if (!AdminAuth.CheckCsrf(context, admin)) return CsrfFailed();
        var request = await HttpJson.TryReadAsync(context.Request, AppJsonContext.Default.ExpiryRequest, context.RequestAborted);
        if (request?.Days is null) return ApiErrors.BadRequest("缺少 days");
        var result = await blobs.SetExpiryAsync(id, request.Days.Value, context.RequestAborted);
        return result.Ok
            ? HttpJson.Json(result.Value!, AppJsonContext.Default.FileInfoDto)
            : ApiErrors.From(result.Failure!.Value);
    }

    private static async Task<IResult> SetPinnedAsync(
        HttpContext context, string id, SessionStore sessions, BlobService blobs)
    {
        var admin = Admin(context, sessions);
        if (admin is null) return NotLoggedIn();
        if (!AdminAuth.CheckCsrf(context, admin)) return CsrfFailed();
        var request = await HttpJson.TryReadAsync(context.Request, AppJsonContext.Default.PinRequest, context.RequestAborted);
        if (request is null) return ApiErrors.BadRequest("请求体必须是 JSON");
        var result = await blobs.SetPinnedAsync(id, request.Pinned, context.RequestAborted);
        return result.Ok
            ? HttpJson.Json(result.Value!, AppJsonContext.Default.FileInfoDto)
            : ApiErrors.From(result.Failure!.Value);
    }

    private static async Task<IResult> DeleteFileAsync(
        HttpContext context, string id, SessionStore sessions, BlobService blobs, IpAccessor ipAccessor)
    {
        var admin = Admin(context, sessions);
        if (admin is null) return NotLoggedIn();
        if (!AdminAuth.CheckCsrf(context, admin)) return CsrfFailed();
        var result = await blobs.DeleteAsync(id, null, isAdmin: true, ipAccessor.Get(context), context.RequestAborted);
        return result.Ok
            ? HttpJson.Json(new SimpleStatusResponse("deleted", id), AppJsonContext.Default.SimpleStatusResponse)
            : ApiErrors.From(result.Failure!.Value);
    }

    /// <summary>
    /// 管理端从浏览器直接上传文件：原始字节体，文件名优先取 ?name=（URL 编码）——
    /// XHR 的请求头值必须是 ByteString，放不下中文文件名。
    /// 鉴权只用管理员会话 + CSRF，不需要任何 Bearer 密钥；配额不属于管理员，故不计入任何密钥。
    /// </summary>
    private static async Task<IResult> UploadFileAsync(
        HttpContext context, SessionStore sessions, BlobService blobs, RateLimiter limiter, IpAccessor ipAccessor)
    {
        var admin = Admin(context, sessions);
        if (admin is null) return NotLoggedIn();
        if (!AdminAuth.CheckCsrf(context, admin)) return CsrfFailed();
        if (!limiter.IsAllowed("upload:admin"))
            return ApiErrors.Json(429, ErrorCodes.RateLimited, "请求过于频繁，请稍后重试");

        var request = new UploadRequest(
            null,
            BlobEndpoints.ResolveFileName(context.Request),
            context.Request.ContentType,
            null,
            context.Request.ContentLength ?? -1,
            context.Request.Body,
            ipAccessor.Get(context));

        return await BlobEndpoints.ExecuteUploadAsync(request, blobs, context.RequestAborted);
    }

    private static async Task<IResult> ListTokensAsync(HttpContext context, SessionStore sessions, SqliteIndex index)
    {
        var admin = Admin(context, sessions);
        if (admin is null) return NotLoggedIn();
        var tokens = await index.ListTokensAsync(context.RequestAborted);
        return HttpJson.Json(new TokenListResponse(tokens.Select(t => t.ToDto()).ToList()),
            AppJsonContext.Default.TokenListResponse);
    }

    private static async Task<IResult> CreateTokenAsync(
        HttpContext context, SessionStore sessions, TokenService tokens)
    {
        var admin = Admin(context, sessions);
        if (admin is null) return NotLoggedIn();
        if (!AdminAuth.CheckCsrf(context, admin)) return CsrfFailed();
        var request = await HttpJson.TryReadAsync(context.Request, AppJsonContext.Default.CreateTokenRequest, context.RequestAborted);
        if (request is null) return ApiErrors.BadRequest("请求体必须是 JSON");
        var result = await tokens.CreateAsync(request, context.RequestAborted);
        return result.Ok
            ? HttpJson.Json(result.Value!, AppJsonContext.Default.CreateTokenResponse, StatusCodes.Status201Created)
            : ApiErrors.From(result.Failure!.Value);
    }

    private static async Task<IResult> RevokeTokenAsync(
        HttpContext context, string id, SessionStore sessions, SqliteIndex index, IpAccessor ipAccessor)
    {
        var admin = Admin(context, sessions);
        if (admin is null) return NotLoggedIn();
        if (!AdminAuth.CheckCsrf(context, admin)) return CsrfFailed();
        if (!Ids.IsTokenId(id)) return ApiErrors.Json(404, ErrorCodes.NotFound, "token 不存在");
        var revoked = await index.RevokeTokenAsync(id, Time.NowIso(), context.RequestAborted);
        if (!revoked) return ApiErrors.Json(404, ErrorCodes.NotFound, "token 不存在或已撤销");
        await index.InsertAuditAsync("token.revoke", id, null, null, ipAccessor.Get(context), null, null, context.RequestAborted);
        return HttpJson.Json(new SimpleStatusResponse("revoked", id), AppJsonContext.Default.SimpleStatusResponse);
    }

    private static async Task<IResult> ListAuditAsync(HttpContext context, SessionStore sessions, SqliteIndex index)
    {
        var admin = Admin(context, sessions);
        if (admin is null) return NotLoggedIn();
        var limit = int.TryParse(context.Request.Query["limit"], out var l) ? Math.Clamp(l, 1, 500) : 100;
        var items = await index.ListAuditAsync(limit, context.RequestAborted);
        return HttpJson.Json(new AuditListResponse(items), AppJsonContext.Default.AuditListResponse);
    }
}

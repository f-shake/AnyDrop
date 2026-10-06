using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace AnyDrop.Server;

public static class CookieNames
{
    public const string Admin = "anydrop_admin";
}

public static class AuthCookies
{
    /// <summary>只有确实走 HTTPS 时才给 cookie 打 Secure，否则本地 HTTP 调试会陷入登录循环。</summary>
    public static bool IsSecureRequest(HttpContext context) =>
        context.Request.IsHttps ||
        string.Equals(context.Request.Headers["X-Forwarded-Proto"].ToString(), "https", StringComparison.OrdinalIgnoreCase);

    public static void Append(HttpContext context, string name, string value, DateTimeOffset expires, AppConfig config) =>
        context.Response.Cookies.Append(name, value, new CookieOptions
        {
            HttpOnly = true,
            Secure = config.Server.CookieSecure && IsSecureRequest(context),
            SameSite = SameSiteMode.Lax,
            Path = config.CookiePath,
            Expires = expires,
            IsEssential = true,
        });

    public static void Delete(HttpContext context, string name, AppConfig config) =>
        context.Response.Cookies.Delete(name, new CookieOptions
        {
            HttpOnly = true,
            Secure = config.Server.CookieSecure && IsSecureRequest(context),
            SameSite = SameSiteMode.Lax,
            Path = config.CookiePath,
            IsEssential = true,
        });
}

public sealed class AdminSession
{
    public string Id { get; init; } = "";
    public string Csrf { get; init; } = "";
    public string Username { get; init; } = "";
    public string Ip { get; init; } = "";
    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>进程内超管会话存储。重启即失效，单实例部署。</summary>
public sealed class SessionStore
{
    private readonly ConcurrentDictionary<string, AdminSession> _admins = new(StringComparer.Ordinal);

    public AdminSession CreateAdmin(string username, string ip)
    {
        var session = new AdminSession
        {
            Id = Ids.NewSessionId(),
            Csrf = Ids.NewSessionId(),
            Username = username,
            Ip = ip,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(12),
        };
        _admins[session.Id] = session;
        Prune();
        return session;
    }

    public AdminSession? GetAdmin(string? id)
    {
        if (string.IsNullOrEmpty(id) || !_admins.TryGetValue(id, out var session)) return null;
        if (session.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _admins.TryRemove(id, out _);
            return null;
        }
        return session;
    }

    public void RemoveAdmin(string? id)
    {
        if (!string.IsNullOrEmpty(id)) _admins.TryRemove(id, out _);
    }

    private void Prune()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (key, value) in _admins)
            if (value.ExpiresAt <= now) _admins.TryRemove(key, out _);
    }
}

public sealed class AdminAuth(
    SqliteIndex index,
    AppConfig config,
    SessionStore sessions,
    RateLimiter limiter,
    ILogger<AdminAuth> logger)
{
    public const int MinPasswordLength = 16;
    public const string NotInitializedCode = "admin_not_initialized";

    public async Task<ServiceResult<bool>> SetPasswordAsync(string password, CancellationToken cancellationToken)
    {
        if (password.Length < MinPasswordLength)
            return ServiceResult<bool>.Fail(400, ErrorCodes.BadRequest, $"管理员密码至少 {MinPasswordLength} 位");
        var (hash, salt, iterations) = SecretHasher.HashPassword(password);
        await index.SetAdminPasswordAsync(hash, salt, iterations, cancellationToken);
        logger.LogInformation("管理员密码已更新");
        return ServiceResult<bool>.Success(true);
    }

    public async Task<ServiceResult<AdminSession>> LoginAsync(AdminLoginRequest request, string ip, CancellationToken cancellationToken)
    {
        var username = (request.Username ?? "").Trim();
        var password = request.Password ?? "";
        var baseKey = $"login:{ip}";
        var userKey = $"login:{ip}:{username}";

        if (limiter.IsLockedOut(baseKey, out var minutes) || limiter.IsLockedOut(userKey, out minutes))
            return ServiceResult<AdminSession>.Fail(429, ErrorCodes.LoginLocked, $"登录失败过多，请 {minutes} 分钟后重试");
        if (!limiter.IsAllowed(baseKey, perMinute: 10))
            return ServiceResult<AdminSession>.Fail(429, ErrorCodes.RateLimited, "请求过于频繁，请稍后重试");
        // 全局闸门：与 IP、用户名都无关。每次尝试都要跑 210k 次 PBKDF2，
        // 没有这道闸门时轮换用户名 + 伪造 IP 就能把 CPU 打满。
        if (!limiter.IsAllowed("login:global", perMinute: config.Security.LoginGlobalPerMinute))
            return ServiceResult<AdminSession>.Fail(429, ErrorCodes.RateLimited, "登录请求过于频繁，请稍后重试");

        var admin = await index.GetAdminAsync(cancellationToken);
        if (admin is null)
            return ServiceResult<AdminSession>.Fail(503, NotInitializedCode,
                "尚未设置管理员密码，请在服务器上运行 AnyDrop.Server --set-password");

        if (!string.Equals(username, config.Admin.Username, StringComparison.Ordinal) ||
            !SecretHasher.VerifyPassword(password, admin.Value.Hash, admin.Value.Salt, admin.Value.Iterations))
        {
            limiter.RecordFailure(baseKey);
            limiter.RecordFailure(userKey);
            await index.InsertAuditAsync("admin.login.failed", null, null, ip, null, username, cancellationToken);
            return ServiceResult<AdminSession>.Fail(401, ErrorCodes.InvalidCredentials, "用户名或密码错误");
        }

        limiter.ResetFailures(baseKey);
        limiter.ResetFailures(userKey);
        var session = sessions.CreateAdmin(username, ip);
        await index.InsertAuditAsync("admin.login", null, null, ip, null, username, cancellationToken);
        return ServiceResult<AdminSession>.Success(session);
    }

    /// <summary>写操作必须带匹配的 CSRF 头。</summary>
    public static bool CheckCsrf(HttpContext context, AdminSession session)
    {
        if (!context.Request.Headers.TryGetValue("X-CSRF-Token", out var values)) return false;
        var provided = values.ToString().Trim();
        return provided.Length > 0 && string.Equals(provided, session.Csrf, StringComparison.Ordinal);
    }
}

using Microsoft.Extensions.Logging;

namespace AnyDrop.Server;

public sealed class TokenService(SqliteIndex index, AppConfig config, ILogger<TokenService> logger)
{
    public const string DefaultNamespace = "default";

    public async Task<ServiceResult<CreateTokenResponse>> CreateAsync(CreateTokenRequest request, CancellationToken cancellationToken)
    {
        var name = (request.Name ?? "").Trim();
        if (name.Length == 0) return ServiceResult<CreateTokenResponse>.Fail(400, ErrorCodes.BadRequest, "name 不能为空");
        if (name.Length > 64) name = name[..64];

        if (!TokenPresets.IsKnown(request.Preset))
            return ServiceResult<CreateTokenResponse>.Fail(400, ErrorCodes.BadRequest, "preset 只能是 ai-write / me-read / nas-pull");

        var (upload, read, delete) = TokenPresets.Resolve(request.Preset ?? TokenPresets.AiWrite);
        var canUpload = request.CanUpload ?? upload;
        var canRead = request.CanRead ?? read;
        var canDelete = request.CanDelete ?? delete;
        if (!canUpload && !canRead && !canDelete)
            return ServiceResult<CreateTokenResponse>.Fail(400, ErrorCodes.BadRequest, "token 至少要有一项能力");

        var ns = (request.Namespace ?? DefaultNamespace).Trim();
        if (!IsValidNamespace(ns))
            return ServiceResult<CreateTokenResponse>.Fail(400, ErrorCodes.BadRequest, "namespace 只能是 1-32 位小写字母、数字、点、下划线或短横线");

        var ttlDays = request.TtlDays ?? 0;
        if (ttlDays < 0 || ttlDays > 3650)
            return ServiceResult<CreateTokenResponse>.Fail(400, ErrorCodes.BadRequest, "ttlDays 必须在 0 到 3650 之间");

        var quota = request.QuotaBytes ?? 20L * 1024 * 1024 * 1024;
        if (quota <= 0) return ServiceResult<CreateTokenResponse>.Fail(400, ErrorCodes.BadRequest, "quotaBytes 必须大于 0");

        var maxFile = request.MaxFileBytes ?? config.Server.MaxUploadBytes;
        if (maxFile <= 0) return ServiceResult<CreateTokenResponse>.Fail(400, ErrorCodes.BadRequest, "maxFileBytes 必须大于 0");
        maxFile = Math.Min(maxFile, config.Server.MaxUploadBytes);

        var key = Ids.NewTokenKey();
        var token = new TokenRecord
        {
            Id = Ids.NewTokenId(),
            Name = name,
            KeyHash = SecretHasher.Sha256Hex(key),
            KeyPrefix = key[..9],
            Namespace = ns,
            CanUpload = canUpload,
            CanRead = canRead,
            CanDelete = canDelete,
            MaxFileBytes = maxFile,
            QuotaBytes = quota,
            UsedBytes = 0,
            CreatedAt = Time.NowIso(),
            ExpiresAt = ttlDays > 0 ? Time.AddDaysIso(ttlDays) : null,
        };
        await index.InsertTokenAsync(token, cancellationToken);
        await index.InsertAuditAsync("token.create", token.Id, null, ns, null, null, $"{name} preset={request.Preset ?? "custom"}", cancellationToken);
        logger.LogInformation("已创建 token {TokenId} ({Name}) 能力 upload={Upload} read={Read} delete={Delete}",
            token.Id, token.Name, canUpload, canRead, canDelete);
        return ServiceResult<CreateTokenResponse>.Success(new CreateTokenResponse(token.ToDto(), key));
    }

    /// <summary>从 Authorization: Bearer 头解析并校验 token。</summary>
    public async Task<ServiceResult<TokenRecord>> AuthenticateAsync(string? authorization, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(authorization))
            return Unauthorized(ErrorCodes.InvalidToken, "缺少 Authorization: Bearer 密钥");
        var value = authorization.Trim();
        if (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) value = value[7..].Trim();
        if (value.Length < 16 || !value.StartsWith("ad_", StringComparison.Ordinal))
            return Unauthorized(ErrorCodes.InvalidToken, "密钥无效或已撤销");

        var hash = SecretHasher.Sha256Hex(value);
        var token = await index.FindTokenByHashAsync(hash, cancellationToken);
        if (token is null || !SecretHasher.FixedTimeEqualsHex(token.KeyHash, hash))
            return Unauthorized(ErrorCodes.InvalidToken, "密钥无效或已撤销");
        if (token.IsRevoked)
            return Unauthorized(ErrorCodes.InvalidToken, "密钥无效或已撤销");
        if (token.IsExpired(DateTimeOffset.UtcNow))
            return Unauthorized(ErrorCodes.TokenExpired, "密钥已过期");
        return ServiceResult<TokenRecord>.Success(token);
    }

    public async Task TouchAsync(TokenRecord token, CancellationToken cancellationToken) =>
        await index.TouchTokenAsync(token.Id, Time.NowIso(), cancellationToken);

    private static ServiceResult<TokenRecord> Unauthorized(string code, string message) =>
        ServiceResult<TokenRecord>.Fail(401, code, message);

    public static bool IsValidNamespace(string ns)
    {
        if (ns.Length is 0 or > 32) return false;
        foreach (var c in ns)
        {
            var ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c is '.' or '_' or '-';
            if (!ok) return false;
        }
        return ns[0] != '.' && ns[0] != '-' && ns[0] != '_';
    }
}

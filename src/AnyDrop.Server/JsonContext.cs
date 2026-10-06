using System.Text.Json.Serialization;

namespace AnyDrop.Server;

public sealed record SessionRequest(string? Key);

public sealed record SessionResponse(string Kind, string Namespace, string ExpiresAt);

public sealed record AdminLoginRequest(string? Username, string? Password);

public sealed record AdminLoginResponse(string CsrfToken, string Username, string ExpiresAt);

public sealed record SessionStateResponse(bool Authenticated, string? Kind, string? Namespace, string? CsrfToken);

public sealed record UploadResponse(string Id, string Url, string Sha256, long Size, string ExpiresAt);

public sealed record FileInfoDto(
    string Id,
    string? Name,
    long Size,
    string Sha256,
    string? ContentType,
    string Namespace,
    string TokenId,
    string CreatedAt,
    string ExpiresAt,
    bool Pinned,
    int DownloadCount);

public sealed record FileListResponse(int Total, int Page, int Size, List<FileInfoDto> Items);

public sealed record ExpiryRequest(int? Days);

public sealed record PinRequest(bool Pinned);

public sealed record TokenDto(
    string Id,
    string Name,
    string KeyPrefix,
    string Namespace,
    bool CanUpload,
    bool CanRead,
    bool CanDelete,
    long MaxFileBytes,
    long QuotaBytes,
    long UsedBytes,
    string CreatedAt,
    string? ExpiresAt,
    string? RevokedAt,
    string? LastUsedAt);

public sealed record TokenListResponse(List<TokenDto> Items);

public sealed record CreateTokenRequest(
    string? Name,
    string? Preset,
    string? Namespace,
    bool? CanUpload,
    bool? CanRead,
    bool? CanDelete,
    int? TtlDays,
    long? QuotaBytes,
    long? MaxFileBytes);

public sealed record CreateTokenResponse(TokenDto Token, string Key);

public sealed record AuditDto(
    long Id,
    string Ts,
    string Action,
    string? TokenId,
    string? BlobId,
    string? Namespace,
    string? Ip,
    long? Bytes,
    string? Detail);

public sealed record AuditListResponse(List<AuditDto> Items);

public sealed record HealthResponse(string Status, long DiskFreeBytes, long BlobCount, long UptimeSeconds);

public sealed record SimpleStatusResponse(string Status, string? Id);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ApiError))]
[JsonSerializable(typeof(ApiErrorEnvelope))]
[JsonSerializable(typeof(SessionRequest))]
[JsonSerializable(typeof(SessionResponse))]
[JsonSerializable(typeof(AdminLoginRequest))]
[JsonSerializable(typeof(AdminLoginResponse))]
[JsonSerializable(typeof(SessionStateResponse))]
[JsonSerializable(typeof(UploadResponse))]
[JsonSerializable(typeof(FileInfoDto))]
[JsonSerializable(typeof(FileListResponse))]
[JsonSerializable(typeof(ExpiryRequest))]
[JsonSerializable(typeof(PinRequest))]
[JsonSerializable(typeof(TokenDto))]
[JsonSerializable(typeof(TokenListResponse))]
[JsonSerializable(typeof(CreateTokenRequest))]
[JsonSerializable(typeof(CreateTokenResponse))]
[JsonSerializable(typeof(AuditDto))]
[JsonSerializable(typeof(AuditListResponse))]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(SimpleStatusResponse))]
public sealed partial class AppJsonContext : JsonSerializerContext;

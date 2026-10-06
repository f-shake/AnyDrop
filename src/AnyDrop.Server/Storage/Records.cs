namespace AnyDrop.Server;

/// <summary>
/// 一把上传密钥。它只有「能不能传」这一件事：下载不需要任何密钥，
/// 所以这里既没有能力位，也没有命名空间。
/// </summary>
public sealed class TokenRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string KeyHash { get; set; } = "";
    public string KeyPrefix { get; set; } = "";
    public long MaxFileBytes { get; set; }
    public long QuotaBytes { get; set; }
    public long UsedBytes { get; set; }
    public string CreatedAt { get; set; } = "";
    public string? ExpiresAt { get; set; }
    public string? RevokedAt { get; set; }
    public string? LastUsedAt { get; set; }

    public bool IsRevoked => !string.IsNullOrEmpty(RevokedAt);

    public bool IsExpired(DateTimeOffset now) => Time.IsExpired(ExpiresAt, now);

    public TokenDto ToDto() => new(
        Id, Name, KeyPrefix, MaxFileBytes, QuotaBytes, UsedBytes, CreatedAt, ExpiresAt, RevokedAt, LastUsedAt);
}

public sealed class BlobRecord
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public string? ContentType { get; set; }
    public string CipherPath { get; set; } = "";
    public int KeyVersion { get; set; }
    public byte[] FileNonce { get; set; } = [];
    public int ChunkSize { get; set; }
    public byte[] WrappedDek { get; set; } = [];
    public byte[] DekNonce { get; set; } = [];
    /// <summary>上传它的密钥 id；管理员在管理面板里直接上传时为 null，即不属于任何密钥。</summary>
    public string? TokenId { get; set; }
    public string CreatedAt { get; set; } = "";
    public string ExpiresAt { get; set; } = "";
    public int DownloadCount { get; set; }
    public bool Pinned { get; set; }

    /// <summary>分享链接由调用方带上（只有它知道 publicBaseUrl）。</summary>
    public FileInfoDto ToDto(string url) => new(
        Id, url, Name, Size, Sha256, ContentType, TokenId, CreatedAt, ExpiresAt, Pinned, DownloadCount);
}

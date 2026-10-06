namespace AnyDrop.Server;

public sealed class TokenRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string KeyHash { get; set; } = "";
    public string KeyPrefix { get; set; } = "";
    public string Namespace { get; set; } = "";
    public bool CanUpload { get; set; }
    public bool CanRead { get; set; }
    public bool CanDelete { get; set; }
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
        Id, Name, KeyPrefix, Namespace, CanUpload, CanRead, CanDelete,
        MaxFileBytes, QuotaBytes, UsedBytes, CreatedAt, ExpiresAt, RevokedAt, LastUsedAt);
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
    public string TokenId { get; set; } = "";
    public string Namespace { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string ExpiresAt { get; set; } = "";
    public int DownloadCount { get; set; }
    public bool Pinned { get; set; }

    public FileInfoDto ToDto() => new(
        Id, Name, Size, Sha256, ContentType, Namespace, TokenId, CreatedAt, ExpiresAt, Pinned, DownloadCount);
}

/// <summary>token 的三种预设能力组合。</summary>
public static class TokenPresets
{
    public const string AiWrite = "ai-write";
    public const string MeRead = "me-read";
    public const string NasPull = "nas-pull";

    public static (bool Upload, bool Read, bool Delete) Resolve(string? preset) => preset switch
    {
        MeRead => (false, true, false),
        NasPull => (false, true, true),
        _ => (true, false, false),
    };

    public static bool IsKnown(string? preset) =>
        preset is null or AiWrite or MeRead or NasPull;
}

/// <summary>
/// 管理端直接从浏览器上传时使用的记账身份。
/// <see cref="BlobRecord.TokenId"/> 对应的列是 NOT NULL 且没有外键，而管理员上传不属于任何密钥，
/// 只能落到这个哨兵值。它不会匹配任何真实 token 行（token id 是 13 位 Crockford Base32，字母表不含 'i'），
/// 因此既不会占用任何密钥的配额，也不会出现在密钥列表里。
/// </summary>
public static class AdminUploadIdentity
{
    public const string TokenId = "admin";
}

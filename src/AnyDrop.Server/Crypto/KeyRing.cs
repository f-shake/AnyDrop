using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace AnyDrop.Server;

/// <summary>
/// 主密钥（KEK）管理：首次启动生成 32 字节随机密钥并落盘，之后只从磁盘读取。
/// 每个文件用自己的随机 DEK 加密，DEK 由 KEK 用 AES-GCM 包裹后写入文件头。
/// </summary>
public sealed class KeyRing
{
    public const int MasterKeySize = 32;
    public const int DekSize = 32;
    public const int WrapTagSize = 16;

    private readonly ILogger<KeyRing> _logger;
    private readonly byte[] _kek;

    public int KeyVersion { get; } = 1;
    public string KeyPath { get; }

    public KeyRing(AppConfig config, ILogger<KeyRing> logger)
    {
        _logger = logger;
        KeyPath = config.MasterKeyPath;
        _kek = LoadOrCreate(KeyPath, logger);
    }

    private static byte[] LoadOrCreate(string path, ILogger logger)
    {
        if (File.Exists(path))
        {
            var existing = File.ReadAllBytes(path);
            if (existing.Length != MasterKeySize)
                throw new InvalidOperationException($"主密钥文件 {path} 长度不是 {MasterKeySize} 字节，拒绝启动");
            return existing;
        }

        var key = RandomNumberGenerator.GetBytes(MasterKeySize);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, key);
        RestrictPermissions(path, logger);
        logger.LogInformation("已生成新的主密钥：{Path}（请备份，丢失后已加密的文件无法恢复）", path);
        return key;
    }

    public static void RestrictPermissions(string path, ILogger logger)
    {
        if (OperatingSystem.IsWindows())
        {
            logger.LogWarning("运行在 Windows 上，请自行确认 {Path} 的 ACL 仅对服务账号可读", path);
            return;
        }
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "无法把 {Path} 权限设置为 600，请手动处理", path);
        }
    }

    public byte[] NewDek() => RandomNumberGenerator.GetBytes(DekSize);

    /// <summary>用 KEK 包裹 DEK：返回 32 字节密钥 + 16 字节 tag，共 48 字节。</summary>
    public byte[] WrapDek(byte[] dek, byte[] nonce, ReadOnlySpan<byte> aad)
    {
        var wrapped = new byte[dek.Length + WrapTagSize];
        using var gcm = new AesGcm(_kek, WrapTagSize);
        gcm.Encrypt(nonce, dek, wrapped.AsSpan(0, dek.Length), wrapped.AsSpan(dek.Length, WrapTagSize), aad);
        return wrapped;
    }

    public byte[] UnwrapDek(ReadOnlySpan<byte> wrapped, byte[] nonce, ReadOnlySpan<byte> aad)
    {
        if (wrapped.Length != DekSize + WrapTagSize)
            throw new InvalidDataException("被包裹的 DEK 长度不合法");
        var dek = new byte[DekSize];
        using var gcm = new AesGcm(_kek, WrapTagSize);
        gcm.Decrypt(nonce, wrapped[..DekSize], wrapped[DekSize..], dek, aad);
        return dek;
    }
}

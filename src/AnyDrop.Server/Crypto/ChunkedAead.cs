using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace AnyDrop.Server;

/// <summary>请求体超过 maxUploadBytes 时抛出，端点映射为 413。</summary>
public sealed class PayloadTooLargeException(long maxBytes)
    : Exception($"请求体超过上限 {maxBytes} 字节")
{
    public long MaxBytes { get; } = maxBytes;
}

public sealed record BlobEncryptResult(
    long Size,
    string Sha256,
    long ChunkCount,
    int ChunkSize,
    int KeyVersion,
    byte[] FileNonce,
    byte[] DekNonce,
    byte[] WrappedDek);

/// <summary>
/// 磁盘格式：96 字节文件头 + N 个 AEAD 分块。
/// 文件头：magic(8) | version(1) | reserved(3) | chunkSize(4 LE) | keyVersion(4 LE) |
///         fileNonce(4) | dekNonce(12) | wrappedDek(48) | reserved(12)
/// 每个分块：密文(明文长度) || tag(16)；nonce = fileNonce(4) || 块序号(8 BE)。
/// </summary>
public static class BlobFormat
{
    public const int HeaderSize = 96;
    public const int TagSize = 16;
    public const int NonceSize = 12;
    public const int FileNonceSize = 4;
    public const byte Version = 1;

    private static ReadOnlySpan<byte> Magic => "ANYDROP1"u8;

    public static byte[] BuildHeader(int chunkSize, int keyVersion, byte[] fileNonce, byte[] dekNonce, byte[] wrappedDek)
    {
        var header = new byte[HeaderSize];
        Magic.CopyTo(header);
        header[8] = Version;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), chunkSize);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), keyVersion);
        fileNonce.CopyTo(header.AsSpan(20));
        dekNonce.CopyTo(header.AsSpan(24));
        wrappedDek.CopyTo(header.AsSpan(36));
        return header;
    }

    /// <summary>DEK 的附加认证数据：文件头前 24 字节，把格式与 fileNonce 绑定进包裹。</summary>
    public static ReadOnlySpan<byte> HeaderAad(ReadOnlySpan<byte> header) => header[..24];

    public static byte[] ChunkNonce(byte[] fileNonce, long chunkIndex)
    {
        var nonce = new byte[NonceSize];
        fileNonce.CopyTo(nonce, 0);
        BinaryPrimitives.WriteInt64BigEndian(nonce.AsSpan(4), chunkIndex);
        return nonce;
    }

    public static byte[] ChunkAad(long chunkIndex)
    {
        var aad = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(aad, chunkIndex);
        return aad;
    }

    public static long ChunkCount(long size, int chunkSize) =>
        size <= 0 ? 0 : (size + chunkSize - 1) / chunkSize;

    public static long TotalFileSize(long size, int chunkSize) =>
        HeaderSize + size + ChunkCount(size, chunkSize) * TagSize;

    /// <summary>把明文流加密写入 output，返回明文哈希与文件头所需字段。超过 maxBytes 抛 PayloadTooLargeException。</summary>
    public static async Task<BlobEncryptResult> EncryptStreamAsync(
        Stream plaintext,
        Stream output,
        KeyRing keyRing,
        int chunkSize,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        var fileNonce = Ids.RandomBytes(FileNonceSize);
        var dekNonce = Ids.RandomBytes(NonceSize);
        var dek = keyRing.NewDek();
        var wrappedDek = keyRing.WrapDek(dek, dekNonce, HeaderAad(BuildHeaderPrefix(chunkSize, keyRing.KeyVersion, fileNonce)));
        var header = BuildHeader(chunkSize, keyRing.KeyVersion, fileNonce, dekNonce, wrappedDek);
        await output.WriteAsync(header, cancellationToken);

        var plain = ArrayPool<byte>.Shared.Rent(chunkSize);
        var cipher = ArrayPool<byte>.Shared.Rent(chunkSize);
        var tag = new byte[TagSize];
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var gcm = new AesGcm(dek, TagSize);
        try
        {
            long total = 0;
            long index = 0;
            while (true)
            {
                var read = await ReadAtMostAsync(plaintext, plain, chunkSize, cancellationToken);
                if (read <= 0) break;
                if (total + read > maxBytes)
                    throw new PayloadTooLargeException(maxBytes);
                hasher.AppendData(plain, 0, read);
                gcm.Encrypt(
                    BlobFormat.ChunkNonce(fileNonce, index),
                    plain.AsSpan(0, read),
                    cipher.AsSpan(0, read),
                    tag,
                    ChunkAad(index));
                await output.WriteAsync(cipher.AsMemory(0, read), cancellationToken);
                await output.WriteAsync(tag, cancellationToken);
                total += read;
                index++;
            }
            var sha256 = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            return new BlobEncryptResult(total, sha256, index, chunkSize, keyRing.KeyVersion, fileNonce, dekNonce, wrappedDek);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
            ArrayPool<byte>.Shared.Return(plain);
            ArrayPool<byte>.Shared.Return(cipher);
        }
    }

    private static byte[] BuildHeaderPrefix(int chunkSize, int keyVersion, byte[] fileNonce)
    {
        var prefix = new byte[24];
        Magic.CopyTo(prefix);
        prefix[8] = Version;
        BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(12), chunkSize);
        BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(16), keyVersion);
        fileNonce.CopyTo(prefix.AsSpan(20));
        return prefix;
    }

    internal static async Task<int> ReadAtMostAsync(Stream stream, byte[] buffer, int count, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, count - total), cancellationToken);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    internal static async Task ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, count - total), cancellationToken);
            if (read == 0) throw new InvalidDataException("密文文件被截断");
            total += read;
        }
    }
}

/// <summary>只读的加密文件视图，支持按字节区间解密（Range 下载）。</summary>
public sealed class BlobFile : IDisposable
{
    private readonly Stream _stream;
    private readonly AesGcm _gcm;
    private readonly bool _leaveOpen;
    private readonly byte[] _fileNonce;

    public long Size { get; }
    public int ChunkSize { get; }
    public long ChunkCount { get; }
    public int KeyVersion { get; }

    /// <summary>文件头里的每文件随机前缀（nonce 的高 4 字节）。</summary>
    public byte[] FileNonce => _fileNonce;

    private BlobFile(Stream stream, AesGcm gcm, bool leaveOpen, byte[] fileNonce, long size, int chunkSize, int keyVersion)
    {
        _stream = stream;
        _gcm = gcm;
        _leaveOpen = leaveOpen;
        _fileNonce = fileNonce;
        Size = size;
        ChunkSize = chunkSize;
        ChunkCount = BlobFormat.ChunkCount(size, chunkSize);
        KeyVersion = keyVersion;
    }

    public static BlobFile Open(Stream stream, KeyRing keyRing, bool leaveOpen = false)
    {
        var header = new byte[BlobFormat.HeaderSize];
        var read = 0;
        while (read < header.Length)
        {
            var n = stream.Read(header, read, header.Length - read);
            if (n == 0) throw new InvalidDataException("文件头不完整");
            read += n;
        }
        if (!header.AsSpan(0, 8).SequenceEqual("ANYDROP1"u8))
            throw new InvalidDataException("文件头 magic 不匹配");
        if (header[8] != BlobFormat.Version)
            throw new InvalidDataException($"不支持的文件格式版本 {header[8]}");

        var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12));
        var keyVersion = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(16));
        if (chunkSize <= 0 || chunkSize > 16 * 1024 * 1024)
            throw new InvalidDataException("文件头 chunkSize 不合法");

        var fileNonce = header[20..24];
        var dekNonce = header[24..36];
        var wrappedDek = header[36..84];
        var dek = keyRing.UnwrapDek(wrappedDek, dekNonce, BlobFormat.HeaderAad(header));
        var gcm = new AesGcm(dek, BlobFormat.TagSize);
        CryptographicOperations.ZeroMemory(dek);

        var payload = stream.Length - BlobFormat.HeaderSize;
        if (payload < 0) throw new InvalidDataException("密文文件长度小于文件头");
        var size = RecoverSize(payload, chunkSize);
        return new BlobFile(stream, gcm, leaveOpen, fileNonce, size, chunkSize, keyVersion);
    }

    /// <summary>由密文总长反推明文长度：整块数 × chunkSize + 尾块明文长度。</summary>
    private static long RecoverSize(long payload, int chunkSize)
    {
        if (payload == 0) return 0;
        var stride = chunkSize + (long)BlobFormat.TagSize;
        var full = payload / stride;
        var tail = payload - full * stride;
        if (tail == 0) return full * chunkSize;
        if (tail <= BlobFormat.TagSize) throw new InvalidDataException("尾块密文长度不合法");
        return full * chunkSize + (tail - BlobFormat.TagSize);
    }

    private long ChunkPlainLength(long index) =>
        index == ChunkCount - 1 ? Size - index * ChunkSize : ChunkSize;

    /// <summary>把 [start, start+length) 的明文写入 output。</summary>
    public async Task CopyRangeAsync(long start, long length, Stream output, CancellationToken cancellationToken)
    {
        if (length <= 0) return;
        if (start < 0 || start + length > Size)
            throw new ArgumentOutOfRangeException(nameof(length), "请求范围超出文件大小");

        var stride = ChunkSize + (long)BlobFormat.TagSize;
        var index = start / ChunkSize;
        var offsetInChunk = (int)(start % ChunkSize);
        var remaining = length;

        var plain = ArrayPool<byte>.Shared.Rent(ChunkSize);
        var cipher = ArrayPool<byte>.Shared.Rent(ChunkSize + BlobFormat.TagSize);
        try
        {
            while (remaining > 0 && index < ChunkCount)
            {
                var plainLen = (int)ChunkPlainLength(index);
                var cipherLen = plainLen + BlobFormat.TagSize;
                _stream.Seek(BlobFormat.HeaderSize + index * stride, SeekOrigin.Begin);
                await BlobFormat.ReadExactAsync(_stream, cipher, cipherLen, cancellationToken);
                _gcm.Decrypt(
                    BlobFormat.ChunkNonce(_fileNonce, index),
                    cipher.AsSpan(0, plainLen),
                    cipher.AsSpan(plainLen, BlobFormat.TagSize),
                    plain.AsSpan(0, plainLen),
                    BlobFormat.ChunkAad(index));

                var available = plainLen - offsetInChunk;
                var take = (int)Math.Min(available, remaining);
                await output.WriteAsync(plain.AsMemory(offsetInChunk, take), cancellationToken);
                remaining -= take;
                offsetInChunk = 0;
                index++;
            }
            if (remaining > 0) throw new InvalidDataException("解密时提前到达文件末尾");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(plain);
            ArrayPool<byte>.Shared.Return(cipher);
        }
    }

    public void Dispose()
    {
        _gcm.Dispose();
        if (!_leaveOpen) _stream.Dispose();
    }
}

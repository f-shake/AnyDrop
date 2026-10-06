using System.Globalization;
using System.Security.Cryptography;

namespace AnyDrop.Server;

public static class Ids
{
    private const string Crockford = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>16 字节随机 → 26 位 Crockford Base32（不含 I/L/O/U，避免歧义）。</summary>
    public static string NewBlobId() => Base32(RandomNumberGenerator.GetBytes(16));

    /// <summary>8 字节随机 → 13 位 Crockford Base32。</summary>
    public static string NewTokenId() => Base32(RandomNumberGenerator.GetBytes(8));

    /// <summary>24 字节随机 → 39 位 Crockford Base32，带 ad_ 前缀便于识别泄露的 key。</summary>
    public static string NewTokenKey() => "ad_" + Base32(RandomNumberGenerator.GetBytes(24));

    public static string NewSessionId() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static byte[] RandomBytes(int count) => RandomNumberGenerator.GetBytes(count);

    public static string Base32(ReadOnlySpan<byte> data)
    {
        var chars = new char[(data.Length * 8 + 4) / 5];
        var buffer = 0;
        var bits = 0;
        var index = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                chars[index++] = Crockford[(buffer >> bits) & 0x1F];
            }
        }
        if (bits > 0) chars[index++] = Crockford[(buffer << (5 - bits)) & 0x1F];
        return new string(chars, 0, index);
    }

    public static string Base64Url(ReadOnlySpan<byte> data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool IsBlobId(string? id) =>
        id is { Length: 26 } && id.All(c => Crockford.IndexOf(c) >= 0);

    public static bool IsTokenId(string? id) =>
        id is { Length: 13 } && id.All(c => Crockford.IndexOf(c) >= 0);
}

public static class Time
{
    public const string Format = "yyyy-MM-ddTHH:mm:ssZ";

    public static string NowIso() => Iso(DateTimeOffset.UtcNow);

    public static string Iso(DateTimeOffset value) =>
        value.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture);

    public static string AddDaysIso(int days) => Iso(DateTimeOffset.UtcNow.AddDays(days));

    public static bool TryParse(string? value, out DateTimeOffset parsed) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out parsed);

    /// <summary>ISO 字符串按固定格式写入，因此可以直接做字典序比较；解析失败按"已过期"处理。</summary>
    public static bool IsExpired(string? iso, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(iso)) return false;
        if (!TryParse(iso, out var parsed)) return true;
        return parsed <= now;
    }
}

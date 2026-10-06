using System.Security.Cryptography;
using System.Text;

namespace AnyDrop.Server;

public static class SecretHasher
{
    public const int DefaultIterations = 210_000;
    private const int SaltSize = 16;
    private const int KeySize = 32;

    public static (string Hash, string Salt, int Iterations) HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, DefaultIterations, HashAlgorithmName.SHA256, KeySize);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt), DefaultIterations);
    }

    public static bool VerifyPassword(string password, string hashBase64, string saltBase64, int iterations)
    {
        if (string.IsNullOrEmpty(hashBase64) || string.IsNullOrEmpty(saltBase64)) return false;
        byte[] expected;
        byte[] salt;
        try
        {
            expected = Convert.FromBase64String(hashBase64);
            salt = Convert.FromBase64String(saltBase64);
        }
        catch (FormatException)
        {
            return false;
        }
        var actual = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations <= 0 ? DefaultIterations : iterations,
            HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public static string Sha256Hex(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    /// <summary>常数时间比较两个十六进制串。</summary>
    public static bool FixedTimeEqualsHex(string? a, string? b)
    {
        if (a is null || b is null || a.Length != b.Length) return false;
        byte[] left;
        byte[] right;
        try
        {
            left = Convert.FromHexString(a);
            right = Convert.FromHexString(b);
        }
        catch (FormatException)
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(left, right);
    }
}

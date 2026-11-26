using System.Security.Cryptography;
using System.Text;

namespace schoolmusic_backend.Services;

/// <summary>
/// Narzędzia kryptograficzne do generowania losowych tokenów jednorazowych oraz ich skrótów SHA-256.
/// </summary>
public static class TokenHashing
{
    public static string GenerateUrlSafeToken(int byteCount = 32)
    {
        var bytes = RandomNumberGenerator.GetBytes(byteCount);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static string HashToken(string rawToken)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}

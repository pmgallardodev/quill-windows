using System.Security.Cryptography;
using System.Text;

namespace Quill.Core.Api;

/// <summary>
/// Per-install bearer token of the local API: 32 random bytes (256 bits) as unpadded base64url,
/// so it is always 43 characters from [A-Za-z0-9_-]. The same token keys the webhook signature.
/// </summary>
public static class ApiToken
{
    public const int Length = 43;

    public static string Generate() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static bool IsWellFormed(string? token) =>
        token is { Length: Length } && token.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    /// <summary>
    /// Constant-time comparison. Both sides are hashed first so neither the content nor the length
    /// of the expected token leaks through timing. A missing or malformed expected token never matches.
    /// </summary>
    public static bool Matches(string? expected, string? presented)
    {
        if (!IsWellFormed(expected) || presented is null)
        {
            return false;
        }

        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected!));
        var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        return CryptographicOperations.FixedTimeEquals(expectedHash, presentedHash);
    }

    internal static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

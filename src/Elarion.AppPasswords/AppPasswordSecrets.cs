using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Elarion.AppPasswords;

/// <summary>
/// How app-password secrets are made and hashed (ADR-0111). The hash is a compatibility contract: an application that
/// moves its own app passwords into Elarion's store keeps its rows when its old store used the same encoding.
/// </summary>
public static class AppPasswordSecrets {
    /// <summary>The random bytes of a new secret: 32, so 256 bits.</summary>
    public const int RandomByteCount = 32;

    /// <summary>The longest secret a verification looks up; a longer one is refused without hashing it.</summary>
    public const int MaxSecretLength = 512;

    /// <summary>
    /// Creates a secret: <paramref name="prefix"/> followed by <see cref="RandomByteCount"/> bytes from
    /// <see cref="RandomNumberGenerator"/>, base64url-encoded without padding (43 characters).
    /// </summary>
    /// <param name="prefix">The prefix, as <see cref="AppPasswordOptions.SecretPrefix"/>.</param>
    public static string Generate(string prefix) {
        ArgumentNullException.ThrowIfNull(prefix);
        Span<byte> random = stackalloc byte[RandomByteCount];
        RandomNumberGenerator.Fill(random);
        var secret = prefix + Base64Url.EncodeToString(random);
        CryptographicOperations.ZeroMemory(random);
        return secret;
    }

    /// <summary>
    /// The stored hash of <paramref name="secret"/>: SHA-256 over the UTF-8 bytes of the whole secret as the client
    /// sends it, prefix included, as standard Base64 with padding (44 characters).
    /// </summary>
    /// <example>
    /// <code>
    /// // The same value in SQL, for a migration that copies rows from an old store keeping SHA-256 hex:
    /// // encode(decode(old_hash_hex, 'hex'), 'base64')
    /// string hash = AppPasswordSecrets.ComputeHash("elap_…");
    /// </code>
    /// </example>
    public static string ComputeHash(string secret) {
        ArgumentNullException.ThrowIfNull(secret);
        var byteCount = Encoding.UTF8.GetByteCount(secret);
        var bytes = byteCount <= 1024 ? stackalloc byte[byteCount] : new byte[byteCount];
        Encoding.UTF8.GetBytes(secret, bytes);
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(bytes, hash);
        return Convert.ToBase64String(hash);
    }

    /// <summary>Whether <paramref name="value"/> holds only ASCII letters, digits, <c>-</c> and <c>_</c>.</summary>
    internal static bool IsUrlSafe(ReadOnlySpan<char> value) {
        foreach (var c in value)
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))
                return false;
        return true;
    }
}

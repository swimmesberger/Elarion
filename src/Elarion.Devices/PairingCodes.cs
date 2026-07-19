using System.Security.Cryptography;
using System.Text;

namespace Elarion.Devices;

/// <summary>
/// Code generation, normalization, and hashing shared by the pairing service. Centralized so the
/// security-relevant choices (CSPRNG sampling, hash-at-rest) live in exactly one place.
/// </summary>
internal static class PairingCodes {
    /// <summary>Generates a code by unbiased CSPRNG sampling of the alphabet.</summary>
    public static string Generate(string alphabet, int length) {
        return new string(RandomNumberGenerator.GetItems<char>(alphabet, length));
    }

    /// <summary>
    /// Normalizes a code as a human may have relayed it: uppercased, with separators (dashes,
    /// spaces, dots) removed — so redeem accepts <c>"abcd-efgh"</c> for an issued <c>ABCDEFGH</c>.
    /// </summary>
    public static string Normalize(string code) {
        var builder = new StringBuilder(code.Length);
        foreach (var ch in code) {
            if (ch is '-' or '.' || char.IsWhiteSpace(ch)) continue;

            builder.Append(char.ToUpperInvariant(ch));
        }

        return builder.ToString();
    }

    /// <summary>SHA-256 hex of a normalized code — the only form a store ever sees.</summary>
    public static string Hash(string normalizedCode) {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedCode)));
    }
}

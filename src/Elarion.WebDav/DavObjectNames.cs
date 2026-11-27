using System.Security.Cryptography;
using System.Text;

namespace Elarion.WebDav;

/// <summary>The stable member name of a published object, shared by the protocols.</summary>
internal static class DavObjectNames {
    private const string Base32Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

    /// <summary>
    /// The member name of <paramref name="uid"/>: the UID itself when it is 1 to 200 of <c>A–Z a–z 0–9 . _ @ -</c>, else the
    /// base32 of its SHA-256; <paramref name="extension"/> (<c>.ics</c>, <c>.vcf</c>) appended. Stable, so re-publishing a
    /// UID never moves its URL.
    /// </summary>
    public static string NameOf(string uid, string extension) {
        ArgumentException.ThrowIfNullOrEmpty(uid);
        var plain = uid.Length <= 200;
        foreach (var c in uid)
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '@' or '-')) {
                plain = false;
                break;
            }

        if (plain) return uid + extension;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(uid));
        var builder = new StringBuilder(56);
        int buffer = 0, bits = 0;
        foreach (var b in hash) {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5) {
                builder.Append(Base32Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0) builder.Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);
        return builder.Append(extension).ToString();
    }
}

using System.Security.Cryptography;
using System.Text;

namespace Elarion.Migrations;

/// <summary>
/// Content normalization and checksumming (ADR-0057): SHA-256 over BOM-stripped, CRLF→LF-normalized
/// content, so line-ending churn (git <c>autocrlf</c>, editor settings) can never invalidate an applied
/// script — deliberately not Flyway's CRC32 over raw bytes.
/// </summary>
internal static class MigrationChecksum {
    /// <summary>Strips a leading BOM and normalizes CRLF to LF.</summary>
    public static string Normalize(string content) {
        if (content.Length > 0 && content[0] == '﻿') content = content[1..];

        return content.Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>Computes the lowercase-hex SHA-256 of the (already normalized) content's UTF-8 bytes.</summary>
    public static string Compute(string normalizedContent) {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedContent)));
    }
}

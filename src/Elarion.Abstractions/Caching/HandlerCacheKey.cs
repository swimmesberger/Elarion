using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Elarion.Abstractions.Caching;

/// <summary>
/// Helper methods used by generated cache policies to build stable cache key suffixes.
/// </summary>
/// <remarks>
/// Key parts are formatted culture-invariantly and hashed so user input does not become a
/// long or unsafe physical cache key.
/// </remarks>
public static class HandlerCacheKey {
    /// <summary>The key suffix used for request types without public properties.</summary>
    public const string Empty = "empty";

    /// <summary>
    /// Creates a named key part from a generated request property access.
    /// </summary>
    /// <remarks>
    /// The property name is included so two request shapes with the same values in different
    /// properties do not accidentally produce the same pre-hash key.
    /// </remarks>
    public static string Part<T>(string name, T value) {
        return $"{name}={Format(value)}";
    }

    /// <summary>
    /// Hashes generated key parts into a compact, user-input-safe key suffix.
    /// </summary>
    /// <remarks>
    /// The order of <paramref name="parts"/> matters. Generated policies pass parts in a
    /// deterministic property order.
    /// </remarks>
    public static string Build(params string[] parts) {
        if (parts.Length == 0) return Empty;

        var joined = string.Join("\u001f", parts);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(joined));

        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string Format<T>(T value) {
        return value switch {
            null => "<null>",
            DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            TimeOnly time => time.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
            DateTime dateTime => dateTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToUniversalTime()
                .ToString("O", CultureInfo.InvariantCulture),
            Guid guid => guid.ToString("D", CultureInfo.InvariantCulture),
            bool boolean => boolean ? "true" : "false",
            Enum enumValue => enumValue.ToString(),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };
    }
}

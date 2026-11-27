namespace Elarion.WebDav;

/// <summary>How a <c>text-match</c> compares (RFC 6352 §10.5.4; CalDAV knows only <see cref="Contains"/>).</summary>
internal enum DavMatchType {
    Equals,
    Contains,
    StartsWith,
    EndsWith,
}

/// <summary>
/// The <c>text-match</c> of CalDAV and CardDAV: the collations <c>i;ascii-casemap</c>, <c>i;unicode-casemap</c> and
/// <c>i;octet</c> (RFC 4790), and the match types. Each protocol keeps its own default collation.
/// </summary>
internal static class DavTextMatch {
    public const string AsciiCasemap = "i;ascii-casemap";
    public const string UnicodeCasemap = "i;unicode-casemap";
    public const string Octet = "i;octet";

    /// <summary>The supported collations.</summary>
    public static IReadOnlyList<string> Collations { get; } = [AsciiCasemap, UnicodeCasemap, Octet];

    /// <summary>Whether <paramref name="collation"/> is supported.</summary>
    public static bool IsSupported(string collation) => Collations.Contains(collation);

    /// <summary>Reads a <c>match-type</c> attribute; <see langword="null"/> for an unknown value.</summary>
    public static DavMatchType? ParseMatchType(string? value) => value switch {
        null or "contains" => DavMatchType.Contains,
        "equals" => DavMatchType.Equals,
        "starts-with" => DavMatchType.StartsWith,
        "ends-with" => DavMatchType.EndsWith,
        _ => null,
    };

    /// <summary>
    /// Whether <paramref name="value"/> matches <paramref name="needle"/> under <paramref name="collation"/> (a supported
    /// one) and <paramref name="matchType"/>, before any <c>negate-condition</c>.
    /// </summary>
    public static bool Matches(string value, string needle, string collation, DavMatchType matchType) {
        if (collation == AsciiCasemap) {
            value = AsciiFold(value);
            needle = AsciiFold(needle);
        }

        // i;unicode-casemap folds case beyond ASCII; ordinal-ignore-case is the simple case folding .NET offers without a
        // culture, which covers the letters people type into a search.
        var comparison = collation == UnicodeCasemap ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return matchType switch {
            DavMatchType.Equals => string.Equals(value, needle, comparison),
            DavMatchType.StartsWith => value.StartsWith(needle, comparison),
            DavMatchType.EndsWith => value.EndsWith(needle, comparison),
            _ => value.Contains(needle, comparison),
        };
    }

    private static string AsciiFold(string text) => string.Create(text.Length, text, static (span, source) => {
        for (var i = 0; i < source.Length; i++) span[i] = source[i] is >= 'a' and <= 'z' ? (char)(source[i] - 32) : source[i];
    });
}

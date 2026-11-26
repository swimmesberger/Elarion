using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Elarion.Spreadsheets.Reading;

/// <summary>
/// The one culture-independent rule for values people type into a text cell (ADR-0108, decision 7; FR-043 to FR-046),
/// so an import reads a decimal comma and a decimal point, day-first dates, and yes and no alike on every server.
/// </summary>
internal static partial class LenientText {
    private static readonly string[] IsoDateFormats = [
        "yyyy'-'MM'-'dd",
        "yyyy'-'MM'-'dd'T'HH':'mm",
        "yyyy'-'MM'-'dd'T'HH':'mm':'ss",
        "yyyy'-'MM'-'dd'T'HH':'mm':'ss.FFFFFFF",
        "yyyy'-'MM'-'dd' 'HH':'mm",
        "yyyy'-'MM'-'dd' 'HH':'mm':'ss"
    ];

    private static readonly string[] DottedDateFormats = [
        "d'.'M'.'yyyy",
        "d'.'M'.'yyyy' 'H':'mm",
        "d'.'M'.'yyyy' 'H':'mm':'ss"
    ];

    private static readonly string[] TimeFormats = ["H':'mm", "H':'mm':'ss", "H':'mm':'ss.FFFFFFF"];

    private static readonly string[] TrueWords = ["true", "yes", "x", "1"];
    private static readonly string[] FalseWords = ["false", "no", "0"];

    /// <summary>
    /// A decimal typed by hand (FR-043): spaces ignored; with both <c>,</c> and <c>.</c> the rightmost is the decimal
    /// mark and the other groups thousands; a separator that occurs more than once alone groups thousands; a single one
    /// alone is the decimal mark. Grouping must come in threes, so <c>1.23.4</c> is no number.
    /// </summary>
    public static bool TryParseDecimal(string text, out decimal value) {
        value = 0;
        var compact = new StringBuilder(text.Length);
        foreach (var c in text)
            if (!char.IsWhiteSpace(c))
                compact.Append(c);

        if (compact.Length == 0) return false;

        var s = compact.ToString();
        var negative = false;
        if (s[0] is '+' or '-') {
            negative = s[0] == '-';
            s = s[1..];
        }

        var lastComma = s.LastIndexOf(',');
        var lastDot = s.LastIndexOf('.');
        char? decimalMark = null;
        char? grouping = null;
        if (lastComma >= 0 && lastDot >= 0) {
            decimalMark = lastComma > lastDot ? ',' : '.';
            grouping = lastComma > lastDot ? '.' : ',';
            if (s.Count(c => c == decimalMark) != 1) return false;
        }
        else if (lastComma >= 0 || lastDot >= 0) {
            var separator = lastComma >= 0 ? ',' : '.';
            if (s.Count(c => c == separator) > 1) grouping = separator;
            else decimalMark = separator;
        }

        string integral;
        var fraction = "";
        if (decimalMark is { } mark) {
            var at = s.LastIndexOf(mark);
            integral = s[..at];
            fraction = s[(at + 1)..];
            if (fraction.Length == 0 || !fraction.All(char.IsAsciiDigit)) return false;
        }
        else {
            integral = s;
        }

        if (integral.Length == 0) {
            if (fraction.Length == 0) return false;

            integral = "0";
        }

        if (grouping is { } separatorChar) {
            var groups = integral.Split(separatorChar);
            if (groups[0].Length is < 1 or > 3) return false;
            for (var i = 1; i < groups.Length; i++)
                if (groups[i].Length != 3)
                    return false;

            integral = string.Concat(groups);
        }

        if (!integral.All(char.IsAsciiDigit)) return false;

        var normalized = (negative ? "-" : "") + integral + (fraction.Length > 0 ? "." + fraction : "");
        return decimal.TryParse(normalized, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out value);
    }

    /// <summary>An integral number typed by hand: digits and an optional sign only (FR-043).</summary>
    public static bool TryParseInteger(string text, out decimal value) {
        value = 0;
        var s = text.Trim();
        var digits = s.Length > 0 && s[0] is '+' or '-' ? s[1..] : s;
        return digits.Length > 0 && digits.All(char.IsAsciiDigit)
                                 && decimal.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// A date or date and time typed by hand (FR-044): ISO 8601, <c>d.M.yyyy</c> or <c>d.M.yy</c> (a two-digit year in
    /// the 2000s) with an optional <c>H:mm</c> or <c>H:mm:ss</c>, or one of <paramref name="formats"/>.
    /// </summary>
    public static bool TryParseDateTime(string text, IReadOnlyList<string> formats, out DateTime value) {
        var s = text.Trim();
        if (DateTime.TryParseExact(s, IsoDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out value)) return true;
        if (DateTime.TryParseExact(s, DottedDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out value)) return true;

        var shortYear = ShortYearDate().Match(s);
        if (shortYear.Success) {
            var expanded = $"{shortYear.Groups[1].Value}.{shortYear.Groups[2].Value}.20{shortYear.Groups[3].Value}{shortYear.Groups[4].Value}";
            if (DateTime.TryParseExact(expanded, DottedDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out value))
                return true;
        }

        foreach (var format in formats)
            if (DateTime.TryParseExact(s, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out value))
                return true;

        value = default;
        return false;
    }

    /// <summary>A time of day typed by hand: <c>H:mm</c> or <c>H:mm:ss</c>.</summary>
    public static bool TryParseTime(string text, out TimeOnly value) {
        return TimeOnly.TryParseExact(text.Trim(), TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }

    /// <summary>
    /// An instant (FR-046): only ISO 8601 that states its offset (<c>Z</c> or <c>±hh:mm</c>), so an import never
    /// depends on the server's time zone.
    /// </summary>
    public static bool TryParseDateTimeOffset(string text, out DateTimeOffset value) {
        var s = text.Trim();
        if (IsoInstant().IsMatch(s))
            return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);

        value = default;
        return false;
    }

    /// <summary>
    /// A boolean typed by hand (FR-045, FR-010): <c>true</c>, <c>yes</c>, <c>x</c>, <c>1</c>, <c>false</c>, <c>no</c>,
    /// <c>0</c>, the column's labels and the configured words, ignoring case and surrounding white space.
    /// </summary>
    public static bool TryParseBoolean(
        string text, IReadOnlyList<string> labels, IReadOnlyList<string> trueWords, IReadOnlyList<string> falseWords,
        out bool value) {
        var s = text.Trim();
        if (labels.Count == 2 && Matches(s, labels[0])) return Found(true, out value);
        if (labels.Count == 2 && Matches(s, labels[1])) return Found(false, out value);

        foreach (var word in TrueWords.Concat(trueWords))
            if (Matches(s, word))
                return Found(true, out value);

        foreach (var word in FalseWords.Concat(falseWords))
            if (Matches(s, word))
                return Found(false, out value);

        value = false;
        return false;
    }

    private static bool Found(bool found, out bool value) {
        value = found;
        return true;
    }

    private static bool Matches(string text, string word) {
        return string.Equals(text, word.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"^(\d{1,2})\.(\d{1,2})\.(\d{2})((?: \d{1,2}:\d{2}(?::\d{2})?)?)$", RegexOptions.CultureInvariant)]
    private static partial Regex ShortYearDate();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(?::\d{2}(?:\.\d{1,7})?)?(?:Z|[+-]\d{2}(?::?\d{2})?)$", RegexOptions.CultureInvariant)]
    private static partial Regex IsoInstant();
}

using Elarion.ContentLines;

namespace Elarion.Contacts.Reading;

/// <summary>The documented value rules of a read (ADR-0110, decision 6): types, labels, dates and phone numbers.</summary>
internal static class ContactValueRules {
    public const string AnniversaryLabel = "_$!<Anniversary>!$_";

    // FR-022: Apple's well-known labels map to flags instead of a custom label.
    private static readonly Dictionary<string, ContactTypes> AppleLabels = new(StringComparer.OrdinalIgnoreCase) {
        ["_$!<Mobile>!$_"] = ContactTypes.Cell,
        ["_$!<iPhone>!$_"] = ContactTypes.Cell,
        ["_$!<Main>!$_"] = ContactTypes.Main,
        ["_$!<Home>!$_"] = ContactTypes.Home,
        ["_$!<Work>!$_"] = ContactTypes.Work,
        ["_$!<HomeFAX>!$_"] = ContactTypes.Home | ContactTypes.Fax,
        ["_$!<WorkFAX>!$_"] = ContactTypes.Work | ContactTypes.Fax,
        ["_$!<Pager>!$_"] = ContactTypes.Pager,
        ["_$!<HomePage>!$_"] = ContactTypes.Home,
    };

    /// <summary>The flags and the preferred mark of a property's <c>TYPE</c> and <c>PREF</c> parameters (FR-021, FR-022).</summary>
    public static (ContactTypes Types, bool Preferred) ReadTypes(ContactProperty property) {
        var types = ContactTypes.None;
        var preferred = false;
        foreach (var parameter in property.Parameters) {
            if (string.Equals(parameter.Name, "PREF", StringComparison.OrdinalIgnoreCase)) {
                if (parameter.Values.Count == 0 || parameter.Values.Any(static value => value.Trim() is "1" or "")) preferred = true;
                continue;
            }

            if (!string.Equals(parameter.Name, "TYPE", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var value in parameter.Values) {
                foreach (var token in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
                    if (token.Equals("PREF", StringComparison.OrdinalIgnoreCase)) preferred = true;
                    else types |= TypeOf(token);
                }
            }
        }

        return (types, preferred);
    }

    /// <summary>The flags of an Apple label, or <see langword="null"/> for a custom label.</summary>
    public static ContactTypes? AppleLabelTypes(string label) => AppleLabels.TryGetValue(label.Trim(), out var types) ? types : null;

    /// <summary>A custom label with Apple's <c>_$!&lt;</c> and <c>&gt;!$_</c> removed.</summary>
    public static string CustomLabel(string label) {
        var text = label.Trim();
        if (text.StartsWith("_$!<", StringComparison.Ordinal) && text.EndsWith(">!$_", StringComparison.Ordinal) && text.Length >= 8)
            text = text[4..^4];
        return text;
    }

    /// <summary>A phone number without the Unicode directional formatting characters (FR-018).</summary>
    public static string CleanPhoneNumber(string number) {
        if (number.AsSpan().IndexOfAny(DirectionalMarks) < 0) return number;
        return string.Create(number.Length - Count(number), number, static (span, source) => {
            var i = 0;
            foreach (var c in source)
                if (!IsDirectional(c)) span[i++] = c;
        });

        static int Count(string text) {
            var count = 0;
            foreach (var c in text)
                if (IsDirectional(c)) count++;
            return count;
        }
    }

    /// <summary>
    /// Reads a birthday or an anniversary (FR-023): <c>YYYYMMDD</c>, <c>YYYY-MM-DD</c>, a date-time whose time is
    /// dropped, <c>--MMDD</c> and <c>--MM-DD</c>; year 1604, or the year <c>X-APPLE-OMIT-YEAR</c> names, is no year.
    /// </summary>
    public static bool TryReadDate(ContactProperty property, out ContactDate date) {
        date = default;
        var text = ContentText.Unescape(property.Value).Trim();
        var time = text.IndexOfAny(['T', 't']);
        if (time >= 0) text = text[..time];

        int? year;
        int month, day;
        if (text.StartsWith("--", StringComparison.Ordinal)) {
            var rest = text[2..];
            if (rest.Length == 5 && rest[2] == '-') rest = string.Concat(rest.AsSpan(0, 2), rest.AsSpan(3));
            if (rest.Length != 4 || !AllDigits(rest)) return false;
            year = null;
            month = Number(rest, 0, 2);
            day = Number(rest, 2, 2);
        }
        else {
            if (text.Length == 10 && text[4] == '-' && text[7] == '-') text = string.Concat(text.AsSpan(0, 4), text.AsSpan(5, 2), text.AsSpan(8, 2));
            if (text.Length != 8 || !AllDigits(text)) return false;
            year = Number(text, 0, 4);
            month = Number(text, 4, 2);
            day = Number(text, 6, 2);
        }

        if (year is { } stated) {
            var omitted = property.GetParameter("X-APPLE-OMIT-YEAR");
            if (stated == 1604 || omitted.Any(value => int.TryParse(value, out var omit) && omit == stated)) year = null;
        }

        if (!ContactDate.IsValid(year, month, day)) return false;
        date = new ContactDate(year, month, day);
        return true;
    }

    private static ContactTypes TypeOf(string token) => token.ToUpperInvariant() switch {
        "HOME" => ContactTypes.Home,
        "WORK" => ContactTypes.Work,
        "CELL" or "MOBILE" => ContactTypes.Cell,
        "VOICE" => ContactTypes.Voice,
        "FAX" => ContactTypes.Fax,
        "PAGER" => ContactTypes.Pager,
        "TEXT" => ContactTypes.Text,
        "VIDEO" => ContactTypes.Video,
        "MAIN" or "MAIN-NUMBER" => ContactTypes.Main,
        _ => ContactTypes.None,
    };

    private const string DirectionalMarks = "‎‏‪‫‬‭‮⁦⁧⁨⁩";

    private static bool IsDirectional(char c) => c is '‎' or '‏' or >= '‪' and <= '‮' or >= '⁦' and <= '⁩';

    private static bool AllDigits(string text) {
        foreach (var c in text)
            if (!char.IsAsciiDigit(c)) return false;
        return true;
    }

    private static int Number(string text, int start, int length) {
        var value = 0;
        for (var i = start; i < start + length; i++) value = value * 10 + (text[i] - '0');
        return value;
    }
}

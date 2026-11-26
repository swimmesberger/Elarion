using System.Text;

namespace Elarion.Spreadsheets.Writing;

/// <summary>Cell references, date serials and the limits of the spreadsheet format.</summary>
internal static class SpreadsheetCells {
    public const int MaxRows = 1_048_576;
    public const int MaxColumns = 16_384;
    public const int MaxTextLength = 32_767;
    public const int MaxWorksheetNameLength = 31;

    private static readonly DateTime FirstDate = new(1900, 1, 1);
    private static readonly DateTime FirstDateAfterLeapBug = new(1900, 3, 1);

    // The 1900 date system counts 1900-01-01 as day 1 and, like Lotus 1-2-3, a 29 February 1900 that never was, so
    // every day from 1 March 1900 on is one more than the plain count from 1899-12-31.
    private static readonly DateTime EpochBeforeLeapBug = new(1899, 12, 31);
    private static readonly DateTime EpochAfterLeapBug = new(1899, 12, 30);

    /// <summary>The letters of the zero-based column <paramref name="index"/>, for example <c>AB</c> for 27.</summary>
    public static string ColumnLetters(int index) {
        var builder = new StringBuilder(3);
        for (var n = index + 1; n > 0; n = (n - 1) / 26) builder.Insert(0, (char)('A' + (n - 1) % 26));

        return builder.ToString();
    }

    /// <summary>The serial number of <paramref name="value"/>, or <see langword="false"/> before 1900-01-01.</summary>
    public static bool TryGetSerial(DateTime value, out double serial) {
        if (value < FirstDate) {
            serial = 0;
            return false;
        }

        var epoch = value < FirstDateAfterLeapBug ? EpochBeforeLeapBug : EpochAfterLeapBug;
        var days = (value.Date - epoch).Days;
        serial = days + (double)value.TimeOfDay.Ticks / TimeSpan.TicksPerDay;
        return true;
    }

    /// <summary>The serial number of a time of day: the fraction of the day.</summary>
    public static double GetSerial(TimeOnly value) {
        return (double)value.Ticks / TimeSpan.TicksPerDay;
    }

    /// <summary>
    /// The reason <paramref name="name"/> cannot name a worksheet, or <see langword="null"/>: empty, longer than 31
    /// characters, one of <c>[ ] : * ? / \</c>, an apostrophe at either end, or a character XML cannot carry.
    /// </summary>
    public static string? ValidateWorksheetName(string name) {
        if (name.Length == 0) return "A worksheet name must not be empty.";
        if (name.Length > MaxWorksheetNameLength)
            return $"The worksheet name '{name}' is longer than {MaxWorksheetNameLength} characters.";

        var invalid = name.AsSpan().IndexOfAny("[]:*?/\\");
        if (invalid >= 0) return $"The worksheet name '{name}' contains '{name[invalid]}', which a worksheet name may not contain.";
        if (name[0] == '\'' || name[^1] == '\'')
            return $"The worksheet name '{name}' starts or ends with an apostrophe.";
        if (!Utf8XmlBuffer.IsXmlCharacterData(name) || name.AsSpan().IndexOfAny('\t', '\n', '\r') >= 0)
            return $"The worksheet name '{name}' contains a control character.";

        return null;
    }
}

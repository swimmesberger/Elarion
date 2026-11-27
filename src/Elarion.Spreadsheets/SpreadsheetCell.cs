using System.Globalization;

namespace Elarion.Spreadsheets;

/// <summary>The kind of value a worksheet cell holds, as the file stores it.</summary>
public enum SpreadsheetCellKind {
    /// <summary>No value: no cell, or an empty one.</summary>
    Blank,

    /// <summary>Text, including the cached result of a text formula.</summary>
    Text,

    /// <summary>A number whose number format is not a date or time format.</summary>
    Number,

    /// <summary>A boolean.</summary>
    Boolean,

    /// <summary>A number whose number format, built-in or custom, is a date or time format.</summary>
    Date,

    /// <summary>An error value such as <c>#DIV/0!</c>, usually a formula's cached result.</summary>
    Error
}

/// <summary>
/// One typed cell of a worksheet read without a record (ADR-0108, decision 9), and the raw cell behind a record's
/// column: its kind, its text, and its value.
/// </summary>
/// <remarks>
/// <para>
/// A formula cell holds its last calculated value; the formula is never evaluated (FR-040). A number holds the value of
/// at most 15 significant digits a spreadsheet shows (FR-038). A date is read in the date system the workbook declares
/// (1900 or 1904, FR-039); a time without a date reads as that time on 1899-12-30.
/// </para>
/// <para>
/// <see cref="Text"/> follows FR-037: the text of a text cell, the number in invariant notation without an exponent,
/// a date in ISO 8601 (<c>2026-11-24</c>, <c>2026-11-24T10:30:00</c>, or <c>10:30:00</c> for a time), <c>true</c> or
/// <c>false</c>, an error's code such as <c>#N/A</c>, and empty for a blank cell. The struct holds a kind, one number
/// and two strings (the text and the number format); a number's text is computed on access.
/// </para>
/// </remarks>
public readonly struct SpreadsheetCell : IEquatable<SpreadsheetCell> {
    // Days since 1899-12-30, the OLE Automation date origin, so that both date systems read into one scale.
    private static readonly DateTime DateOrigin = new(1899, 12, 30);

    private readonly string? _text;
    private readonly double _number;

    private SpreadsheetCell(SpreadsheetCellKind kind, string? text, double number, string? numberFormat = null) {
        Kind = kind;
        _text = text;
        _number = number;
        NumberFormat = numberFormat;
    }

    /// <summary>A blank cell.</summary>
    public static SpreadsheetCell Blank => default;

    /// <summary>The kind of value the cell holds.</summary>
    public SpreadsheetCellKind Kind { get; }

    /// <summary>
    /// The number format a number or date cell carries in the file, in the spreadsheet's format notation, for example
    /// <c>#,##0.00 "€"</c>, <c>dd.mm.yyyy</c> or <c>General</c>; <see langword="null"/> for the other kinds and for a
    /// cell built without one (FR-080). It lets a caller show a value the way the file does; <see cref="Text"/> stays
    /// invariant, and the format takes no part in equality.
    /// </summary>
    public string? NumberFormat { get; }

    /// <summary>The cell's text, as FR-037 defines it; empty for a blank cell.</summary>
    public string Text => Kind switch {
        SpreadsheetCellKind.Text or SpreadsheetCellKind.Error => _text ?? "",
        SpreadsheetCellKind.Number => FormatNumber(_number),
        SpreadsheetCellKind.Boolean => _number != 0 ? "true" : "false",
        SpreadsheetCellKind.Date => FormatDate(_number),
        _ => ""
    };

    /// <summary>Creates a text cell; <see langword="null"/> or an empty text is a blank cell.</summary>
    public static SpreadsheetCell FromText(string? text) {
        return string.IsNullOrEmpty(text) ? default : new SpreadsheetCell(SpreadsheetCellKind.Text, text, 0);
    }

    /// <summary>Creates a number cell, rounded to the 15 significant digits a spreadsheet holds.</summary>
    /// <param name="value">The number.</param>
    /// <param name="numberFormat">The cell's <see cref="NumberFormat"/>, or <see langword="null"/>.</param>
    public static SpreadsheetCell FromNumber(double value, string? numberFormat = null) {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value), value, "A cell holds only finite numbers.");

        return new SpreadsheetCell(SpreadsheetCellKind.Number, null, RoundToSpreadsheetPrecision(value), numberFormat);
    }

    /// <summary>Creates a boolean cell.</summary>
    public static SpreadsheetCell FromBoolean(bool value) {
        return new SpreadsheetCell(SpreadsheetCellKind.Boolean, null, value ? 1 : 0);
    }

    /// <summary>Creates a date cell holding the wall-clock date and time of <paramref name="value"/>.</summary>
    /// <param name="value">The date and time.</param>
    /// <param name="numberFormat">The cell's <see cref="NumberFormat"/>, or <see langword="null"/>.</param>
    public static SpreadsheetCell FromDate(DateTime value, string? numberFormat = null) {
        return new SpreadsheetCell(
            SpreadsheetCellKind.Date, null, (double)(value - DateOrigin).Ticks / TimeSpan.TicksPerDay, numberFormat);
    }

    /// <summary>Creates an error cell with its code, for example <c>#DIV/0!</c>.</summary>
    public static SpreadsheetCell FromError(string code) {
        ArgumentException.ThrowIfNullOrEmpty(code);
        return new SpreadsheetCell(SpreadsheetCellKind.Error, code, 0);
    }

    /// <summary>A date cell from its days since 1899-12-30; a value below one is a time without a date.</summary>
    internal static SpreadsheetCell FromDays(double days, string? numberFormat = null) {
        return new SpreadsheetCell(SpreadsheetCellKind.Date, null, days, numberFormat);
    }

    /// <summary>The raw day count of a date cell, whose fraction is the time of day.</summary>
    internal double DateValue => _number;

    /// <summary>The value of a number cell as a <see cref="decimal"/>, at most 15 significant digits.</summary>
    /// <returns><see langword="false"/> when the cell is not a number, or the number exceeds the decimal range.</returns>
    public bool TryGetNumber(out decimal value) {
        if (Kind == SpreadsheetCellKind.Number && Math.Abs(_number) < 7.9e28) {
            value = ToDecimal(_number);
            return true;
        }

        value = 0;
        return false;
    }

    /// <summary>The value of a number cell as a <see cref="double"/>, at most 15 significant digits.</summary>
    public bool TryGetDouble(out double value) {
        value = Kind == SpreadsheetCellKind.Number ? _number : 0;
        return Kind == SpreadsheetCellKind.Number;
    }

    /// <summary>The value of a boolean cell.</summary>
    public bool TryGetBoolean(out bool value) {
        value = Kind == SpreadsheetCellKind.Boolean && _number != 0;
        return Kind == SpreadsheetCellKind.Boolean;
    }

    /// <summary>The value of a date cell; a time without a date reads as that time on 1899-12-30.</summary>
    public bool TryGetDate(out DateTime value) {
        if (Kind == SpreadsheetCellKind.Date) {
            value = ToDateTime(_number);
            return true;
        }

        value = default;
        return false;
    }

    /// <inheritdoc />
    public bool Equals(SpreadsheetCell other) {
        return Kind == other.Kind && _number.Equals(other._number) && string.Equals(_text, other._text, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) {
        return obj is SpreadsheetCell other && Equals(other);
    }

    /// <inheritdoc />
    public override int GetHashCode() {
        return HashCode.Combine(Kind, _number, _text);
    }

    /// <summary>Compares two cells by kind and value.</summary>
    public static bool operator ==(SpreadsheetCell left, SpreadsheetCell right) {
        return left.Equals(right);
    }

    /// <summary>Compares two cells by kind and value.</summary>
    public static bool operator !=(SpreadsheetCell left, SpreadsheetCell right) {
        return !left.Equals(right);
    }

    /// <inheritdoc />
    public override string ToString() {
        return Text;
    }

    /// <summary>
    /// The value a spreadsheet shows: 15 significant digits, so the binary fraction nearest 1234.56 reads as 1234.56.
    /// </summary>
    internal static double RoundToSpreadsheetPrecision(double value) {
        return double.Parse(value.ToString("G15", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    internal static DateTime ToDateTime(double days) {
        // Rounded to the millisecond: a serial carries a binary fraction of a day, not exact ticks.
        var milliseconds = Math.Round(days * 86_400_000d);
        return DateOrigin.AddTicks((long)milliseconds * TimeSpan.TicksPerMillisecond);
    }

    /// <summary>The decimal of the 15 significant digits, not of the binary fraction: 1234.56, not 1234.5599999….</summary>
    private static decimal ToDecimal(double value) {
        return decimal.Parse(value.ToString("G15", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private static string FormatNumber(double value) {
        // A decimal never formats with an exponent; beyond the decimal range the fixed-point form of the double does.
        return Math.Abs(value) < 7.9e28
            ? ToDecimal(value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("F0", CultureInfo.InvariantCulture);
    }

    private static string FormatDate(double days) {
        var value = ToDateTime(days);
        if (days is >= 0 and < 1) return value.ToString("HH':'mm':'ss", CultureInfo.InvariantCulture);

        return value.TimeOfDay == TimeSpan.Zero
            ? value.ToString("yyyy'-'MM'-'dd", CultureInfo.InvariantCulture)
            : value.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss", CultureInfo.InvariantCulture);
    }
}

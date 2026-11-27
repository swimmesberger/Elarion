using System.ComponentModel;
using System.Globalization;
using System.Text;
using Elarion.Spreadsheets.Writing;

namespace Elarion.Spreadsheets.Generated;

/// <summary>
/// Receives the values of one row from a generated <c>{T}SpreadsheetRowMapper</c>, one call per column in column order,
/// and renders them as worksheet cells. Generated code calls it; application code does not.
/// </summary>
/// <remarks>
/// A value a spreadsheet cannot hold fails with a <see cref="SpreadsheetWriteException"/> that names the row, the column
/// and the cell (FR-026). Text is always an inline-string cell, never a formula (FR-015). The same instance first measures
/// the sampled records for the column widths and then renders every row, so both passes see the same values. A column
/// the worksheet leaves out (<see cref="SpreadsheetWorksheetOptions.IncludedColumns"/>,
/// <see cref="SpreadsheetWorksheetOptions.ExcludedColumns"/>) still receives its call, which writes nothing.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class SpreadsheetCellWriter {
    private const int DateWidth = 10;
    private const int DateTimeWidth = 16;
    private const int TimeWidth = 8;
    private const int BooleanWidth = 5;

    private readonly string _worksheet;
    private readonly string[] _headers;
    private readonly SpreadsheetValueKind[] _kinds;
    private readonly string?[] _formats;
    private readonly int[] _styles;
    private readonly byte[][] _letters;
    private readonly int[] _widths;
    private readonly Utf8XmlBuffer _xml;
    private readonly byte[] _rowDigits = new byte[16];

    // The worksheet column of each column the mapper writes, or -1 for one the worksheet leaves out; null when every
    // column is written in place.
    private readonly int[]? _map;

    private int _rowDigitsLength;
    private int _row;
    private int _column;
    private bool _measuring;
    private bool _mapped;

    internal SpreadsheetCellWriter(
        string worksheet,
        IReadOnlyList<WorksheetColumn> columns,
        int[] styles,
        int[]? map,
        Utf8XmlBuffer xml) {
        _worksheet = worksheet;
        _headers = [.. columns.Select(static column => column.Header)];
        _kinds = [.. columns.Select(static column => column.Kind)];
        _formats = [.. columns.Select(static column => column.EffectiveFormat)];
        _styles = styles;
        _map = map;
        _xml = xml;
        _letters = new byte[_headers.Length][];
        _widths = new int[_headers.Length];
        for (var i = 0; i < _headers.Length; i++) {
            _letters[i] = Encoding.ASCII.GetBytes(SpreadsheetCells.ColumnLetters(i));
            _widths[i] = _headers[i].Length;
        }
    }

    /// <summary>The longest text measured per column: its header and the values of the measured rows.</summary>
    internal ReadOnlySpan<int> MeasuredWidths => _widths;

    /// <summary>Writes a text cell, or an empty cell for <see langword="null"/>.</summary>
    public void WriteText(string? value) {
        var column = NextColumn();
        if (column < 0) return;

        if (value is null) WriteBlank(column);
        else WriteTextCell(column, value, _styles[column]);
    }

    /// <summary>Writes a boolean cell, or an empty cell for <see langword="null"/>.</summary>
    public void WriteBoolean(bool? value) {
        var column = NextColumn();
        if (column < 0) return;

        if (value is { } flag) WriteBooleanCell(column, flag, _styles[column]);
        else WriteBlank(column);
    }

    /// <summary>Writes a signed integral number cell, or an empty cell for <see langword="null"/>.</summary>
    public void WriteInteger(long? value) {
        var column = NextColumn();
        if (column < 0) return;

        if (value is { } number) WriteNumberCell(column, number, null, _styles[column], _formats[column]);
        else WriteBlank(column);
    }

    /// <summary>Writes an unsigned integral number cell, or an empty cell for <see langword="null"/>.</summary>
    public void WriteUnsignedInteger(ulong? value) {
        var column = NextColumn();
        if (column < 0) return;

        if (value is { } number) WriteNumberCell(column, number, null, _styles[column], _formats[column]);
        else WriteBlank(column);
    }

    /// <summary>Writes a number cell, or an empty cell for <see langword="null"/>; refuses <c>NaN</c> and infinities.</summary>
    public void WriteSingle(float? value) {
        var column = NextColumn();
        if (column < 0) return;

        if (value is { } number) WriteFloatingCell(column, number, _styles[column], _formats[column]);
        else WriteBlank(column);
    }

    /// <summary>Writes a number cell, or an empty cell for <see langword="null"/>; refuses <c>NaN</c> and infinities.</summary>
    public void WriteDouble(double? value) {
        var column = NextColumn();
        if (column < 0) return;

        if (value is { } number) WriteFloatingCell(column, number, _styles[column], _formats[column]);
        else WriteBlank(column);
    }

    /// <summary>Writes a number cell, or an empty cell for <see langword="null"/>.</summary>
    public void WriteDecimal(decimal? value) {
        var column = NextColumn();
        if (column < 0) return;

        if (value is { } number) WriteNumberCell(column, number, null, _styles[column], _formats[column]);
        else WriteBlank(column);
    }

    /// <summary>
    /// Writes a date-and-time cell holding the wall-clock date and time of <paramref name="value"/>, whatever its
    /// <see cref="DateTime.Kind"/> (FR-064), or an empty cell for <see langword="null"/>; refuses dates before 1900-01-01.
    /// </summary>
    public void WriteDateTime(DateTime? value) {
        var column = NextColumn();
        if (column < 0) return;

        if (value is { } dateTime) WriteDateTimeCell(column, dateTime, _styles[column], _formats[column]);
        else WriteBlank(column);
    }

    /// <summary>Writes a date cell, or an empty cell for <see langword="null"/>; refuses dates before 1900-01-01.</summary>
    public void WriteDate(DateOnly? value) {
        var column = NextColumn();
        if (column < 0) return;

        if (value is { } date) WriteDateOnlyCell(column, date, _styles[column], _formats[column]);
        else WriteBlank(column);
    }

    /// <summary>Writes a time cell (the fraction of a day), or an empty cell for <see langword="null"/>.</summary>
    public void WriteTime(TimeOnly? value) {
        var column = NextColumn();
        if (column < 0) return;

        if (value is { } time) WriteDateCell(column, SpreadsheetCells.GetSerial(time), TimeWidth, _styles[column], _formats[column]);
        else WriteBlank(column);
    }

    /// <summary>
    /// Writes ISO 8601 text with the offset (FR-014), for example <c>2026-11-24T10:00:00+01:00</c>, because a spreadsheet
    /// date has no time zone; or an empty cell for <see langword="null"/>.
    /// </summary>
    public void WriteDateTimeOffset(DateTimeOffset? value) {
        var column = NextColumn();
        if (column < 0) return;

        if (value is { } instant) WriteDateTimeOffsetCell(column, instant, _styles[column]);
        else WriteBlank(column);
    }

    /// <summary>Writes a text cell holding the <see cref="Guid"/> in its hyphenated form, or an empty cell for <see langword="null"/>.</summary>
    public void WriteGuid(Guid? value) {
        var column = NextColumn();
        if (column < 0) return;

        if (value is { } guid) WriteGuidCell(column, guid, _styles[column]);
        else WriteBlank(column);
    }

    /// <summary>
    /// Writes one cell of a row whose values carry their own type (a worksheet built at run time, a footer row): the
    /// value's type decides the cell's, and the column's format applies when the value is of the column's kind, so a
    /// label in a date column is a text cell (FR-074).
    /// </summary>
    internal void WriteValue(object? value) {
        var column = NextColumn();
        if (column < 0) return;

        switch (value) {
            case null:
                WriteBlank(column);
                break;
            case string text:
                WriteTextCell(column, text, Style(column, SpreadsheetValueKind.Text));
                break;
            case bool flag:
                WriteBooleanCell(column, flag, Style(column, SpreadsheetValueKind.Boolean));
                break;
            case sbyte or short or int or long:
                WriteNumberCell(column, Convert.ToInt64(value, CultureInfo.InvariantCulture), null,
                    Style(column, SpreadsheetValueKind.Integer), Format(column, SpreadsheetValueKind.Integer));
                break;
            case byte or ushort or uint or ulong:
                WriteNumberCell(column, Convert.ToUInt64(value, CultureInfo.InvariantCulture), null,
                    Style(column, SpreadsheetValueKind.Integer), Format(column, SpreadsheetValueKind.Integer));
                break;
            case float number:
                WriteFloatingCell(column, number, Style(column, SpreadsheetValueKind.Number), Format(column, SpreadsheetValueKind.Number));
                break;
            case double number:
                WriteFloatingCell(column, number, Style(column, SpreadsheetValueKind.Number), Format(column, SpreadsheetValueKind.Number));
                break;
            case decimal number:
                WriteNumberCell(column, number, null, Style(column, SpreadsheetValueKind.Number), Format(column, SpreadsheetValueKind.Number));
                break;
            case DateTime dateTime:
                WriteDateTimeCell(column, dateTime, Style(column, SpreadsheetValueKind.DateTime), Format(column, SpreadsheetValueKind.DateTime));
                break;
            case DateOnly date:
                WriteDateOnlyCell(column, date, Style(column, SpreadsheetValueKind.Date), Format(column, SpreadsheetValueKind.Date));
                break;
            case TimeOnly time:
                WriteDateCell(column, SpreadsheetCells.GetSerial(time), TimeWidth, Style(column, SpreadsheetValueKind.Time),
                    Format(column, SpreadsheetValueKind.Time));
                break;
            case DateTimeOffset instant:
                WriteDateTimeOffsetCell(column, instant, Style(column, SpreadsheetValueKind.DateTimeOffset));
                break;
            case Guid guid:
                WriteGuidCell(column, guid, Style(column, SpreadsheetValueKind.Guid));
                break;
            case Enum member:
                WriteTextCell(column, member.ToString(), Style(column, SpreadsheetValueKind.Text));
                break;
            default:
                throw Refuse(column,
                    $"a value of type '{value.GetType()}' is not a cell value; use text, a boolean, a number, a date, a time, "
                    + "a DateTimeOffset, a Guid, an enum or null.");
        }
    }

    /// <summary>
    /// Writes the values of a row whose values carry their own type, then empty cells for the columns after its last
    /// value; refuses a row with more values than the worksheet has columns.
    /// </summary>
    internal void WriteValues(IReadOnlyList<object?> values) {
        if (values.Count > _headers.Length)
            throw new SpreadsheetWriteException(
                $"Cannot write row {_row} of worksheet '{_worksheet}': it has {values.Count} values, but the worksheet has "
                + $"{_headers.Length} columns.",
                _worksheet,
                _row);

        for (var i = 0; i < values.Count; i++) WriteValue(values[i]);

        while (_column < _headers.Length) WriteBlank(NextColumn());
    }

    /// <summary>
    /// Starts a row. <paramref name="mapped"/> routes the calls of a record's mapper through the column filter; a row of
    /// values that carry their own type addresses the written columns directly.
    /// </summary>
    internal void BeginRow(int rowNumber, bool measuring, bool mapped = true) {
        if (rowNumber > SpreadsheetCells.MaxRows)
            throw new SpreadsheetWriteException(
                $"Cannot write row {rowNumber} of worksheet '{_worksheet}': a worksheet holds at most {SpreadsheetCells.MaxRows} rows.",
                _worksheet,
                rowNumber);

        _row = rowNumber;
        _column = 0;
        _measuring = measuring;
        _mapped = mapped && _map is not null;
        ((uint)rowNumber).TryFormat(_rowDigits, out _rowDigitsLength, default, CultureInfo.InvariantCulture);
        if (measuring) return;

        _xml.Append("<row r=\""u8);
        _xml.Append(_rowDigits.AsSpan(0, _rowDigitsLength));
        _xml.Append("\">"u8);
    }

    internal void EndRow() {
        var expected = _mapped ? _map!.Length : _headers.Length;
        if (_column != expected)
            throw new InvalidOperationException(
                $"The row mapper wrote {_column} of {expected} columns of worksheet '{_worksheet}'.");

        if (!_measuring) _xml.Append("</row>"u8);
    }

    internal void WriteHeaderRow() {
        BeginRow(1, measuring: false, mapped: false);
        for (var column = 0; column < _headers.Length; column++) {
            BeginCell(column, SpreadsheetStyles.Header, "inlineStr"u8);
            AppendInlineString(_headers[column]);
            _column++;
        }

        EndRow();
    }

    private int NextColumn() {
        var count = _mapped ? _map!.Length : _headers.Length;
        if (_column >= count)
            throw new InvalidOperationException(
                $"The row mapper wrote more than {count} columns of worksheet '{_worksheet}'.");

        var column = _column++;
        return _mapped ? _map![column] : column;
    }

    // The column's own style for a value of its kind, or of its family with a declared format (an integer in a number
    // column, a date-and-time in a date column); otherwise the value's built-in style, so a label in a date column is
    // plain text and a number in a text column a plain number.
    private int Style(int column, SpreadsheetValueKind kind) {
        return UsesColumnFormat(column, kind) ? _styles[column] : SpreadsheetStyles.BuiltInStyleFor(kind);
    }

    private string? Format(int column, SpreadsheetValueKind kind) {
        return UsesColumnFormat(column, kind) ? _formats[column] : null;
    }

    private bool UsesColumnFormat(int column, SpreadsheetValueKind kind) {
        var own = _kinds[column];
        if (own == kind) return true;

        return _formats[column] is not null && Family(own) == Family(kind);
    }

    private static int Family(SpreadsheetValueKind kind) {
        return kind switch {
            SpreadsheetValueKind.Integer or SpreadsheetValueKind.Number => 1,
            SpreadsheetValueKind.Date or SpreadsheetValueKind.DateTime or SpreadsheetValueKind.Time => 2,
            _ => 0
        };
    }

    private void WriteBlank(int column) {
        var style = _styles[column];
        if (_measuring || style == SpreadsheetStyles.Default) return;

        AppendCellStart(column, style);
        _xml.Append("/>"u8);
    }

    private void WriteBooleanCell(int column, bool flag, int style) {
        if (_measuring) {
            Measure(column, BooleanWidth);
            return;
        }

        BeginCell(column, style, "b"u8);
        _xml.Append(flag ? "<v>1</v></c>"u8 : "<v>0</v></c>"u8);
    }

    private void WriteFloatingCell(int column, double number, int style, string? format) {
        if (!double.IsFinite(number))
            throw Refuse(column, $"the value {number.ToString(CultureInfo.InvariantCulture)} is not a finite number.");

        WriteNumberCell(column, number, "R", style, format);
    }

    private void WriteFloatingCell(int column, float number, int style, string? format) {
        if (!float.IsFinite(number))
            throw Refuse(column, $"the value {number.ToString(CultureInfo.InvariantCulture)} is not a finite number.");

        WriteNumberCell(column, number, "R", style, format);
    }

    private void WriteDateTimeCell(int column, DateTime dateTime, int style, string? format) {
        if (!SpreadsheetCells.TryGetSerial(dateTime, out var serial))
            throw Refuse(column, $"the date {dateTime.ToString("O", CultureInfo.InvariantCulture)} is before 1900-01-01, the first date a spreadsheet holds.");

        WriteDateCell(column, serial, DateTimeWidth, style, format);
    }

    private void WriteDateOnlyCell(int column, DateOnly date, int style, string? format) {
        if (!SpreadsheetCells.TryGetSerial(date.ToDateTime(TimeOnly.MinValue), out var serial))
            throw Refuse(column, $"the date {date.ToString("O", CultureInfo.InvariantCulture)} is before 1900-01-01, the first date a spreadsheet holds.");

        WriteDateCell(column, serial, DateWidth, style, format);
    }

    private void WriteDateTimeOffsetCell(int column, DateTimeOffset instant, int style) {
        Span<char> text = stackalloc char[40];
        var format = instant.Ticks % TimeSpan.TicksPerSecond == 0 ? "yyyy'-'MM'-'dd'T'HH':'mm':'sszzz" : "O";
        instant.TryFormat(text, out var written, format, CultureInfo.InvariantCulture);
        WriteTextCell(column, text[..written], style);
    }

    private void WriteGuidCell(int column, Guid guid, int style) {
        Span<char> text = stackalloc char[36];
        guid.TryFormat(text, out var written, "D");
        WriteTextCell(column, text[..written], style);
    }

    private void WriteTextCell(int column, ReadOnlySpan<char> text, int style) {
        if (text.Length > SpreadsheetCells.MaxTextLength)
            throw Refuse(column, $"the text is {text.Length} characters long; a cell holds at most {SpreadsheetCells.MaxTextLength}.");

        if (_measuring) {
            Measure(column, text.Length);
            return;
        }

        BeginCell(column, style, "inlineStr"u8);
        AppendInlineString(text);
    }

    private void AppendInlineString(ReadOnlySpan<char> text) {
        // xml:space="preserve" keeps leading and trailing whitespace that a reader would otherwise be free to drop.
        var preserve = text.Length > 0 && (char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[^1]));
        _xml.Append(preserve ? "<is><t xml:space=\"preserve\">"u8 : "<is><t>"u8);
        _xml.AppendText(text);
        _xml.Append("</t></is></c>"u8);
    }

    private void WriteNumberCell<TValue>(int column, TValue value, string? numberFormat, int style, string? format)
        where TValue : IUtf8SpanFormattable {
        Span<byte> digits = stackalloc byte[64];
        value.TryFormat(digits, out var written, numberFormat, CultureInfo.InvariantCulture);
        if (_measuring) {
            Measure(column, Math.Max(written, format?.Length ?? 0));
            return;
        }

        AppendCellStart(column, style);
        _xml.Append("><v>"u8);
        _xml.Append(digits[..written]);
        _xml.Append("</v></c>"u8);
    }

    private void WriteDateCell(int column, double serial, int width, int style, string? format) {
        if (_measuring) {
            Measure(column, format?.Length ?? width);
            return;
        }

        AppendCellStart(column, style);
        _xml.Append("><v>"u8);
        _xml.AppendFormatted(serial, "R");
        _xml.Append("</v></c>"u8);
    }

    private void BeginCell(int column, int style, ReadOnlySpan<byte> type) {
        AppendCellStart(column, style);
        _xml.Append(" t=\""u8);
        _xml.Append(type);
        _xml.Append("\">"u8);
    }

    private void AppendCellStart(int column, int style) {
        _xml.Append("<c r=\""u8);
        _xml.Append(_letters[column]);
        _xml.Append(_rowDigits.AsSpan(0, _rowDigitsLength));
        _xml.Append((byte)'"');
        if (style == SpreadsheetStyles.Default) return;

        _xml.Append(" s=\""u8);
        _xml.AppendFormatted(style);
        _xml.Append((byte)'"');
    }

    private void Measure(int column, int length) {
        if (length > _widths[column]) _widths[column] = length;
    }

    private SpreadsheetWriteException Refuse(int column, string reason) {
        var header = _headers[column];
        var cell = Encoding.ASCII.GetString(_letters[column]) + _row.ToString(CultureInfo.InvariantCulture);
        return new SpreadsheetWriteException(
            $"Cannot write row {_row}, column '{header}' (cell {cell}) of worksheet '{_worksheet}': {reason}",
            _worksheet,
            _row,
            header,
            cell);
    }
}

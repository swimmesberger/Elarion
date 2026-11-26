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
/// the sampled records for the column widths and then renders every row, so both passes see the same values.
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

    private int _rowDigitsLength;
    private int _row;
    private int _column;
    private bool _measuring;

    internal SpreadsheetCellWriter(
        string worksheet,
        string[] headers,
        SpreadsheetValueKind[] kinds,
        string?[] formats,
        int[] styles,
        Utf8XmlBuffer xml) {
        _worksheet = worksheet;
        _headers = headers;
        _kinds = kinds;
        _formats = formats;
        _styles = styles;
        _xml = xml;
        _letters = new byte[headers.Length][];
        _widths = new int[headers.Length];
        for (var i = 0; i < headers.Length; i++) {
            _letters[i] = Encoding.ASCII.GetBytes(SpreadsheetCells.ColumnLetters(i));
            _widths[i] = headers[i].Length;
        }
    }

    /// <summary>The longest text measured per column: its header and the values of the measured rows.</summary>
    internal ReadOnlySpan<int> MeasuredWidths => _widths;

    /// <summary>Writes a text cell, or an empty cell for <see langword="null"/>.</summary>
    public void WriteText(string? value) {
        var column = NextColumn();
        if (value is null) WriteBlank(column);
        else WriteTextCell(column, value);
    }

    /// <summary>Writes a boolean cell, or an empty cell for <see langword="null"/>.</summary>
    public void WriteBoolean(bool? value) {
        var column = NextColumn();
        if (value is not { } flag) {
            WriteBlank(column);
            return;
        }

        if (_measuring) {
            Measure(column, BooleanWidth);
            return;
        }

        BeginCell(column, _styles[column], "b"u8);
        _xml.Append(flag ? "<v>1</v></c>"u8 : "<v>0</v></c>"u8);
    }

    /// <summary>Writes a signed integral number cell, or an empty cell for <see langword="null"/>.</summary>
    public void WriteInteger(long? value) {
        var column = NextColumn();
        if (value is { } number) WriteNumberCell(column, number, null);
        else WriteBlank(column);
    }

    /// <summary>Writes an unsigned integral number cell, or an empty cell for <see langword="null"/>.</summary>
    public void WriteUnsignedInteger(ulong? value) {
        var column = NextColumn();
        if (value is { } number) WriteNumberCell(column, number, null);
        else WriteBlank(column);
    }

    /// <summary>Writes a number cell, or an empty cell for <see langword="null"/>; refuses <c>NaN</c> and infinities.</summary>
    public void WriteSingle(float? value) {
        var column = NextColumn();
        if (value is not { } number) {
            WriteBlank(column);
            return;
        }

        if (!float.IsFinite(number)) throw Refuse(column, $"the value {number.ToString(CultureInfo.InvariantCulture)} is not a finite number.");

        WriteNumberCell(column, number, "R");
    }

    /// <summary>Writes a number cell, or an empty cell for <see langword="null"/>; refuses <c>NaN</c> and infinities.</summary>
    public void WriteDouble(double? value) {
        var column = NextColumn();
        if (value is not { } number) {
            WriteBlank(column);
            return;
        }

        if (!double.IsFinite(number)) throw Refuse(column, $"the value {number.ToString(CultureInfo.InvariantCulture)} is not a finite number.");

        WriteNumberCell(column, number, "R");
    }

    /// <summary>Writes a number cell, or an empty cell for <see langword="null"/>.</summary>
    public void WriteDecimal(decimal? value) {
        var column = NextColumn();
        if (value is { } number) WriteNumberCell(column, number, null);
        else WriteBlank(column);
    }

    /// <summary>
    /// Writes a date-and-time cell holding the wall-clock date and time of <paramref name="value"/>, whatever its
    /// <see cref="DateTime.Kind"/> (FR-064), or an empty cell for <see langword="null"/>; refuses dates before 1900-01-01.
    /// </summary>
    public void WriteDateTime(DateTime? value) {
        var column = NextColumn();
        if (value is not { } dateTime) {
            WriteBlank(column);
            return;
        }

        if (!SpreadsheetCells.TryGetSerial(dateTime, out var serial))
            throw Refuse(column, $"the date {dateTime.ToString("O", CultureInfo.InvariantCulture)} is before 1900-01-01, the first date a spreadsheet holds.");

        WriteDateCell(column, serial, DateTimeWidth);
    }

    /// <summary>Writes a date cell, or an empty cell for <see langword="null"/>; refuses dates before 1900-01-01.</summary>
    public void WriteDate(DateOnly? value) {
        var column = NextColumn();
        if (value is not { } date) {
            WriteBlank(column);
            return;
        }

        if (!SpreadsheetCells.TryGetSerial(date.ToDateTime(TimeOnly.MinValue), out var serial))
            throw Refuse(column, $"the date {date.ToString("O", CultureInfo.InvariantCulture)} is before 1900-01-01, the first date a spreadsheet holds.");

        WriteDateCell(column, serial, DateWidth);
    }

    /// <summary>Writes a time cell (the fraction of a day), or an empty cell for <see langword="null"/>.</summary>
    public void WriteTime(TimeOnly? value) {
        var column = NextColumn();
        if (value is { } time) WriteDateCell(column, SpreadsheetCells.GetSerial(time), TimeWidth);
        else WriteBlank(column);
    }

    /// <summary>
    /// Writes ISO 8601 text with the offset (FR-014), for example <c>2026-11-24T10:00:00+01:00</c>, because a spreadsheet
    /// date has no time zone; or an empty cell for <see langword="null"/>.
    /// </summary>
    public void WriteDateTimeOffset(DateTimeOffset? value) {
        var column = NextColumn();
        if (value is not { } instant) {
            WriteBlank(column);
            return;
        }

        Span<char> text = stackalloc char[40];
        var format = instant.Ticks % TimeSpan.TicksPerSecond == 0 ? "yyyy'-'MM'-'dd'T'HH':'mm':'sszzz" : "O";
        instant.TryFormat(text, out var written, format, CultureInfo.InvariantCulture);
        WriteTextCell(column, text[..written]);
    }

    /// <summary>Writes a text cell holding the <see cref="Guid"/> in its hyphenated form, or an empty cell for <see langword="null"/>.</summary>
    public void WriteGuid(Guid? value) {
        var column = NextColumn();
        if (value is not { } guid) {
            WriteBlank(column);
            return;
        }

        Span<char> text = stackalloc char[36];
        guid.TryFormat(text, out var written, "D");
        WriteTextCell(column, text[..written]);
    }

    internal void BeginRow(int rowNumber, bool measuring) {
        if (rowNumber > SpreadsheetCells.MaxRows)
            throw new SpreadsheetWriteException(
                $"Cannot write row {rowNumber} of worksheet '{_worksheet}': a worksheet holds at most {SpreadsheetCells.MaxRows} rows.",
                _worksheet,
                rowNumber);

        _row = rowNumber;
        _column = 0;
        _measuring = measuring;
        ((uint)rowNumber).TryFormat(_rowDigits, out _rowDigitsLength, default, CultureInfo.InvariantCulture);
        if (measuring) return;

        _xml.Append("<row r=\""u8);
        _xml.Append(_rowDigits.AsSpan(0, _rowDigitsLength));
        _xml.Append("\">"u8);
    }

    internal void EndRow() {
        if (_column != _headers.Length)
            throw new InvalidOperationException(
                $"The row mapper wrote {_column} of {_headers.Length} columns of worksheet '{_worksheet}'.");

        if (!_measuring) _xml.Append("</row>"u8);
    }

    internal void WriteHeaderRow() {
        BeginRow(1, measuring: false);
        for (var column = 0; column < _headers.Length; column++) {
            BeginCell(column, SpreadsheetStyles.Header, "inlineStr"u8);
            AppendInlineString(_headers[column]);
            _column++;
        }

        EndRow();
    }

    private int NextColumn() {
        if (_column >= _headers.Length)
            throw new InvalidOperationException(
                $"The row mapper wrote more than {_headers.Length} columns of worksheet '{_worksheet}'.");

        return _column++;
    }

    private void WriteBlank(int column) {
        var style = _styles[column];
        if (_measuring || style == SpreadsheetStyles.Default) return;

        AppendCellStart(column, style);
        _xml.Append("/>"u8);
    }

    private void WriteTextCell(int column, ReadOnlySpan<char> text) {
        if (text.Length > SpreadsheetCells.MaxTextLength)
            throw Refuse(column, $"the text is {text.Length} characters long; a cell holds at most {SpreadsheetCells.MaxTextLength}.");

        if (_measuring) {
            Measure(column, text.Length);
            return;
        }

        BeginCell(column, _styles[column], "inlineStr"u8);
        AppendInlineString(text);
    }

    private void AppendInlineString(ReadOnlySpan<char> text) {
        // xml:space="preserve" keeps leading and trailing whitespace that a reader would otherwise be free to drop.
        var preserve = text.Length > 0 && (char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[^1]));
        _xml.Append(preserve ? "<is><t xml:space=\"preserve\">"u8 : "<is><t>"u8);
        _xml.AppendText(text);
        _xml.Append("</t></is></c>"u8);
    }

    private void WriteNumberCell<TValue>(int column, TValue value, string? format) where TValue : IUtf8SpanFormattable {
        Span<byte> digits = stackalloc byte[64];
        value.TryFormat(digits, out var written, format, CultureInfo.InvariantCulture);
        if (_measuring) {
            Measure(column, Math.Max(written, _formats[column]?.Length ?? 0));
            return;
        }

        AppendCellStart(column, _styles[column]);
        _xml.Append("><v>"u8);
        _xml.Append(digits[..written]);
        _xml.Append("</v></c>"u8);
    }

    private void WriteDateCell(int column, double serial, int width) {
        if (_measuring) {
            Measure(column, _formats[column]?.Length ?? width);
            return;
        }

        AppendCellStart(column, _styles[column]);
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

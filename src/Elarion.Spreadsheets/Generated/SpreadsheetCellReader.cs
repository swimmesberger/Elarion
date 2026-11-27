using System.ComponentModel;
using Elarion.Spreadsheets.Reading;
using Elarion.Spreadsheets.Writing;

namespace Elarion.Spreadsheets.Generated;

/// <summary>
/// Hands the cells of one row to a generated <c>{T}SpreadsheetRowMapper</c>, one typed read per column, and collects
/// the located errors of the row. Generated code calls it; application code does not.
/// </summary>
/// <remarks>
/// Every read returns <see langword="null"/> when the file lacks the column, when the cell is empty (a
/// <see cref="SpreadsheetErrorCodes.Required"/> error for a column whose property is not nullable), or when the value
/// does not convert (a <see cref="SpreadsheetErrorCodes.InvalidValue"/> error). Number and date cells read as the
/// spreadsheet shows them (FR-037 to FR-039); text cells by the lenient rules (FR-043 to FR-046).
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class SpreadsheetCellReader {
    private readonly string _worksheet;
    private readonly string[] _headers;
    private readonly SpreadsheetValueKind[] _kinds;
    private readonly bool[] _nullable;
    private readonly IReadOnlyList<string>[] _choices;
    private readonly int[] _ordinals;
    private readonly SpreadsheetReadOptions _options;

    private SpreadsheetCell[] _cells = [];
    private List<SpreadsheetError> _errors = [];
    private int _rowNumber;

    internal SpreadsheetCellReader(
        string worksheet,
        string[] headers,
        SpreadsheetValueKind[] kinds,
        bool[] nullable,
        IReadOnlyList<string>[] choices,
        int[] ordinals,
        SpreadsheetReadOptions options) {
        _worksheet = worksheet;
        _headers = headers;
        _kinds = kinds;
        _nullable = nullable;
        _choices = choices;
        _ordinals = ordinals;
        _options = options;
    }

    /// <summary>Whether a read of the current row has found an error.</summary>
    public bool HasErrors => _errors.Count > 0;

    internal void BeginRow(int rowNumber, SpreadsheetCell[] cells, List<SpreadsheetError> errors) {
        _rowNumber = rowNumber;
        _cells = cells;
        _errors = errors;
    }

    /// <summary>Reads a text column: a text cell as it is, any other cell as its text (FR-037).</summary>
    public string? ReadText(int column) {
        if (!TryGetCell(column, out var cell)) return null;
        if (cell.Kind == SpreadsheetCellKind.Error) return Invalid<string>(column, cell);

        return cell.Text;
    }

    /// <summary>Reads a boolean column, or a labelled boolean column through its labels (FR-010, FR-045).</summary>
    public bool? ReadBoolean(int column) {
        if (!TryGetCell(column, out var cell)) return null;

        switch (cell.Kind) {
            case SpreadsheetCellKind.Boolean when cell.TryGetBoolean(out var flag):
                return flag;
            case SpreadsheetCellKind.Number when cell.TryGetDouble(out var number):
                return number != 0;
            case SpreadsheetCellKind.Text when LenientText.TryParseBoolean(
                cell.Text, _choices[column], _options.TrueWords, _options.FalseWords, out var parsed):
                return parsed;
            default:
                return Invalid<bool>(column, cell);
        }
    }

    /// <summary>Reads a signed integral column within <paramref name="min"/> and <paramref name="max"/> (FR-038, FR-043).</summary>
    public long? ReadInteger(int column, long min, long max) {
        if (!TryGetWhole(column, out var value)) return null;
        if (value < min || value > max) return Invalid<long>(column, _cells[column]);

        return (long)value;
    }

    /// <summary>Reads an unsigned 64-bit integral column (FR-038, FR-043).</summary>
    public ulong? ReadUnsignedInteger(int column) {
        if (!TryGetWhole(column, out var value)) return null;
        if (value < 0 || value > ulong.MaxValue) return Invalid<ulong>(column, _cells[column]);

        return (ulong)value;
    }

    /// <summary>Reads a <see cref="decimal"/> column: a number cell at 15 significant digits, or lenient text.</summary>
    public decimal? ReadDecimal(int column) {
        if (!TryGetCell(column, out var cell)) return null;
        if (cell.TryGetNumber(out var number)) return number;
        if (cell.Kind == SpreadsheetCellKind.Text && LenientText.TryParseDecimal(cell.Text, out var parsed)) return parsed;

        return Invalid<decimal>(column, cell);
    }

    /// <summary>Reads a <see cref="double"/> column: a number cell at 15 significant digits, or lenient text.</summary>
    public double? ReadDouble(int column) {
        if (!TryGetCell(column, out var cell)) return null;
        if (cell.TryGetDouble(out var number)) return number;
        if (cell.Kind == SpreadsheetCellKind.Text && LenientText.TryParseDecimal(cell.Text, out var parsed)) return (double)parsed;

        return Invalid<double>(column, cell);
    }

    /// <summary>Reads a <see cref="float"/> column.</summary>
    public float? ReadSingle(int column) {
        var value = ReadDouble(column);
        if (value is not { } number) return null;
        if (!float.IsFinite((float)number)) return Invalid<float>(column, _cells[column]);

        return (float)number;
    }

    /// <summary>Reads a <see cref="DateTime"/> column: a date cell, or lenient text (FR-039, FR-044).</summary>
    public DateTime? ReadDateTime(int column) {
        if (!TryGetCell(column, out var cell)) return null;
        if (cell.TryGetDate(out var date)) return date;
        if (cell.Kind == SpreadsheetCellKind.Text && LenientText.TryParseDateTime(cell.Text, _options.DateFormats, out var parsed))
            return parsed;

        return Invalid<DateTime>(column, cell);
    }

    /// <summary>Reads a <see cref="DateOnly"/> column: the date of a date cell, or a date typed without a time.</summary>
    public DateOnly? ReadDate(int column) {
        if (!TryGetCell(column, out var cell)) return null;
        if (cell.TryGetDate(out var date)) return DateOnly.FromDateTime(date);
        if (cell.Kind == SpreadsheetCellKind.Text
            && LenientText.TryParseDateTime(cell.Text, _options.DateFormats, out var parsed)
            && parsed.TimeOfDay == TimeSpan.Zero)
            return DateOnly.FromDateTime(parsed);

        return Invalid<DateOnly>(column, cell);
    }

    /// <summary>
    /// Reads a <see cref="TimeOnly"/> column from the fraction of a time cell, whose value must be less than a day, or
    /// from <c>H:mm</c> text.
    /// </summary>
    public TimeOnly? ReadTime(int column) {
        if (!TryGetCell(column, out var cell)) return null;
        if (cell.Kind == SpreadsheetCellKind.Date && cell.DateValue is >= 0 and < 1) {
            var ticks = (long)Math.Round(cell.DateValue * 86_400_000d) * TimeSpan.TicksPerMillisecond;
            if (ticks < TimeSpan.TicksPerDay) return new TimeOnly(ticks);
        }

        if (cell.Kind == SpreadsheetCellKind.Text && LenientText.TryParseTime(cell.Text, out var parsed)) return parsed;

        return Invalid<TimeOnly>(column, cell);
    }

    /// <summary>Reads a <see cref="DateTimeOffset"/> column: only ISO 8601 text that states an offset (FR-046).</summary>
    public DateTimeOffset? ReadDateTimeOffset(int column) {
        if (!TryGetCell(column, out var cell)) return null;
        if (cell.Kind == SpreadsheetCellKind.Text && LenientText.TryParseDateTimeOffset(cell.Text, out var parsed)) return parsed;

        return Invalid<DateTimeOffset>(column, cell);
    }

    /// <summary>Reads a <see cref="Guid"/> column from its text.</summary>
    public Guid? ReadGuid(int column) {
        if (!TryGetCell(column, out var cell)) return null;
        if (cell.Kind == SpreadsheetCellKind.Text && Guid.TryParse(cell.Text.Trim(), out var parsed)) return parsed;

        return Invalid<Guid>(column, cell);
    }

    /// <summary>
    /// Reads the trimmed text of a choice column, which the generated mapper turns into an enum member; a text that names
    /// none it reports with <see cref="RejectChoice"/>.
    /// </summary>
    public string? ReadChoice(int column) {
        if (!TryGetCell(column, out var cell)) return null;
        if (cell.Kind == SpreadsheetCellKind.Error) return Invalid<string>(column, cell);

        return cell.Text.Trim();
    }

    /// <summary>
    /// Reads a text column with <c>StrictChoices</c>: the trimmed text when it equals one of the column's labels ignoring
    /// case, returned as the label is spelled; any other text is an <see cref="SpreadsheetErrorCodes.InvalidValue"/>
    /// error that carries the labels (FR-078).
    /// </summary>
    public string? ReadTextChoice(int column) {
        var text = ReadChoice(column);
        if (text is null) return null;

        foreach (var choice in _choices[column])
            if (string.Equals(choice.Trim(), text, StringComparison.OrdinalIgnoreCase))
                return choice;

        RejectChoice(column);
        return null;
    }

    /// <summary>Reports that the choice column's text names no member: the error carries the allowed labels.</summary>
    public void RejectChoice(int column) {
        Invalid<string>(column, _cells[column]);
    }

    private bool TryGetWhole(int column, out decimal value) {
        value = 0;
        if (!TryGetCell(column, out var cell)) return false;
        if (cell.TryGetNumber(out value) && value == decimal.Truncate(value)) return true;
        if (cell.Kind == SpreadsheetCellKind.Text && LenientText.TryParseInteger(cell.Text, out value)) return true;

        Invalid<decimal>(column, cell);
        return false;
    }

    /// <summary>
    /// The cell of <paramref name="column"/> when it holds a value; otherwise reports a required error for a column
    /// whose property is not nullable. Text of white space only counts as empty, as it does for skipping a row.
    /// </summary>
    private bool TryGetCell(int column, out SpreadsheetCell cell) {
        cell = _cells[column];
        var empty = cell.Kind == SpreadsheetCellKind.Blank
                    || (cell.Kind == SpreadsheetCellKind.Text && string.IsNullOrWhiteSpace(cell.Text));
        if (!empty) return true;

        if (!_nullable[column] && _ordinals[column] >= 0)
            _errors.Add(new SpreadsheetError(
                SpreadsheetErrorCodes.Required, "A value is required.", _worksheet, _rowNumber, _headers[column],
                CellReference(column)));

        return false;
    }

    private TValue? Invalid<TValue>(int column, SpreadsheetCell cell) {
        var kind = _kinds[column];
        var text = cell.Text;
        var choices = kind == SpreadsheetValueKind.Choice ? _choices[column] : null;
        var message = choices is not null
            ? $"'{text}' is not one of: {string.Join(", ", choices)}."
            : $"'{text}' is not {Describe(kind)}.";
        _errors.Add(new SpreadsheetError(
            SpreadsheetErrorCodes.InvalidValue, message, _worksheet, _rowNumber, _headers[column], CellReference(column),
            kind, choices));
        return default;
    }

    private string CellReference(int column) {
        return SpreadsheetCells.ColumnLetters(_ordinals[column]) + _rowNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string Describe(SpreadsheetValueKind kind) {
        return kind switch {
            SpreadsheetValueKind.Text => "text",
            SpreadsheetValueKind.Integer => "a whole number in the column's range",
            SpreadsheetValueKind.Number => "a number",
            SpreadsheetValueKind.Boolean => "yes or no",
            SpreadsheetValueKind.Date => "a date",
            SpreadsheetValueKind.DateTime => "a date and time",
            SpreadsheetValueKind.Time => "a time of day",
            SpreadsheetValueKind.DateTimeOffset => "a date and time with an offset, such as 2026-11-24T10:00:00+01:00",
            SpreadsheetValueKind.Guid => "a GUID",
            _ => "a valid value"
        };
    }
}

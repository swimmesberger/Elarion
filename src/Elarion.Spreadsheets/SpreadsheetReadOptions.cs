namespace Elarion.Spreadsheets;

/// <summary>
/// The limits and lenient-text additions of a read (ADR-0108, decisions 7 and 10). <see cref="SpreadsheetOptions.ReadDefaults"/>
/// holds the application's defaults; a read that passes its own options uses those instead, so derive them from the
/// defaults with <c>with</c> to change one setting.
/// </summary>
/// <remarks>
/// The defaults are sized for the 1–10 node tier (ADR-0025). The library reads the whole upload into memory, so an
/// import declares upload limits as any upload does; these limits bound what the file expands to. The properties are
/// settable because the configuration binder fills <see cref="SpreadsheetOptions.ReadDefaults"/> in place; do not
/// change an instance a read may be using.
/// </remarks>
public sealed record SpreadsheetReadOptions {
    /// <summary>The default of <see cref="MaxUnpackedBytes"/>: 256 MiB.</summary>
    public const long DefaultMaxUnpackedBytes = 256L * 1024 * 1024;

    /// <summary>The default of <see cref="MaxRows"/>: 100,000.</summary>
    public const int DefaultMaxRows = 100_000;

    /// <summary>The default of <see cref="MaxColumns"/>: 256.</summary>
    public const int DefaultMaxColumns = 256;

    /// <summary>
    /// The most bytes a workbook may hold, unpacked (FR-055): the sum of the sizes a zip-based workbook declares, or the
    /// size of an <c>.xls</c> file. A larger workbook is refused with <see cref="SpreadsheetErrorCodes.TooLarge"/> before
    /// anything is unpacked.
    /// </summary>
    public long MaxUnpackedBytes { get; set; } = DefaultMaxUnpackedBytes;

    /// <summary>
    /// The most data rows read from a worksheet (FR-056); empty rows do not count. Reading stops with
    /// <see cref="SpreadsheetErrorCodes.TooManyRows"/> and keeps the rows before it.
    /// </summary>
    public int MaxRows { get; set; } = DefaultMaxRows;

    /// <summary>
    /// The most columns of a row read without a record (FR-059). Reading stops with
    /// <see cref="SpreadsheetErrorCodes.TooManyColumns"/> and keeps the rows before it.
    /// </summary>
    public int MaxColumns { get; set; } = DefaultMaxColumns;

    /// <summary>
    /// Date formats a text cell in a date or date-and-time column may use besides ISO 8601, <c>d.M.yyyy</c> and
    /// <c>d.M.yy</c> (FR-044), in .NET custom format notation read with the invariant culture, for example
    /// <c>M/d/yyyy</c>.
    /// </summary>
    public IReadOnlyList<string> DateFormats { get; set; } = [];

    /// <summary>Words a boolean column reads as <see langword="true"/> besides <c>true</c>, <c>yes</c>, <c>x</c> and <c>1</c> (FR-045), ignoring case.</summary>
    public IReadOnlyList<string> TrueWords { get; set; } = [];

    /// <summary>Words a boolean column reads as <see langword="false"/> besides <c>false</c>, <c>no</c> and <c>0</c> (FR-045), ignoring case.</summary>
    public IReadOnlyList<string> FalseWords { get; set; } = [];

    internal void Validate() {
        if (MaxUnpackedBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxUnpackedBytes), MaxUnpackedBytes, "The unpacked-size limit must be positive.");
        if (MaxRows <= 0) throw new ArgumentOutOfRangeException(nameof(MaxRows), MaxRows, "The row limit must be positive.");
        if (MaxColumns <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxColumns), MaxColumns, "The column limit must be positive.");
    }
}

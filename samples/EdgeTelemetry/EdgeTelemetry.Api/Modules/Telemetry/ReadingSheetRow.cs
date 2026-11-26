using System.ComponentModel.DataAnnotations;
using Elarion.Spreadsheets;

namespace EdgeTelemetry.Api.Modules.Telemetry;

/// <summary>
/// A reading as a spreadsheet row: <c>[SpreadsheetRecord]</c> generates the mapping both ways at compile time, so the
/// native binary exports and imports workbooks without reflection (ADR-0108). The validation attributes are checked on
/// every row read; a row that breaks one reports the rule at its cell.
/// </summary>
[SpreadsheetRecord("Readings")]
public sealed partial class ReadingSheetRow {
    [SpreadsheetColumn("Device")]
    [StringLength(64, MinimumLength = 1)]
    public required string DeviceId { get; init; }

    [StringLength(64, MinimumLength = 1)]
    public required string Metric { get; init; }

    /// <summary>
    /// The instant in UTC. A cell holds a date and time without an offset, so the row carries a <see cref="DateTime"/>
    /// that the handlers treat as UTC; a <see cref="DateTimeOffset"/> would be written as text.
    /// </summary>
    [SpreadsheetColumn("Recorded at (UTC)", Format = "yyyy-mm-dd hh:mm:ss")]
    public required DateTime RecordedAt { get; init; }

    public required double Value { get; init; }
}

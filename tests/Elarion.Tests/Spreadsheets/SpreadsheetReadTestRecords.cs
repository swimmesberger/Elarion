using Elarion.Spreadsheets;

namespace Elarion.Tests.Spreadsheets;

/// <summary>A record whose key and name are required and whose other columns are optional, for updates from trimmed files.</summary>
[SpreadsheetRecord("Contacts")]
public sealed partial class ContactUpdateRow {
    public required string Key { get; init; }

    public required string Name { get; init; }

    public string? Phone { get; init; }

    public string? Notes { get; init; }
}

/// <summary>Every kind as a nullable column, for the lenient text rules and the cell rules.</summary>
[SpreadsheetRecord("Values")]
public sealed partial class LenientRow {
    public string? Text { get; init; }

    public decimal? Amount { get; init; }

    public double? Ratio { get; init; }

    public int? Count { get; init; }

    public byte? Small { get; init; }

    public DateOnly? On { get; init; }

    public DateTime? At { get; init; }

    public TimeOnly? Time { get; init; }

    public DateTimeOffset? Instant { get; init; }

    public bool? Flag { get; init; }

    public Guid? Id { get; init; }

    public ContactKind? Kind { get; init; }

    [SpreadsheetColumn(TrueLabel = "Ja", FalseLabel = "Nein")]
    public bool? Agreed { get; init; }
}

/// <summary>A positional record: the reader builds it through its constructor.</summary>
[SpreadsheetRecord("Accounts")]
public sealed partial record AccountRow(string Iban, decimal Balance) {
    public string? Holder { get; init; }
}

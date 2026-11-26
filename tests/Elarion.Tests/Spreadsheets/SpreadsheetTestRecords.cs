using System.ComponentModel.DataAnnotations;
using Elarion.Spreadsheets;

namespace Elarion.Tests.Spreadsheets;

public enum ContactKind {
    [Display(Name = "Private person")] Person,
    [Display(Name = "Company")] Organization,
    Authority
}

[SpreadsheetRecord("Contacts")]
public sealed partial class ContactRow {
    public required string Name { get; init; }

    public string? Phone { get; init; }

    [SpreadsheetColumn("E-mail", Order = 1, Aliases = ["Mail"])]
    public string? Email { get; init; }

    [Display(Name = "Customer since")]
    public DateOnly? CustomerSince { get; init; }

    [SpreadsheetColumn(Format = "#,##0.00")]
    public decimal Balance { get; init; }

    public int Visits { get; init; }

    public bool Newsletter { get; init; }

    public ContactKind Kind { get; init; }

    [SpreadsheetColumn(TrueLabel = "Ja", FalseLabel = "Nein")]
    public bool Active { get; init; }

    [SpreadsheetIgnore]
    public string? InternalNote { get; init; }

    public int NameLength => Name.Length;
}

[SpreadsheetRecord("Bank accounts")]
public sealed partial record BankAccountRow {
    public required string Iban { get; init; }

    public string? Holder { get; init; }
}

[SpreadsheetRecord]
public sealed partial class ValueRow {
    public string? Text { get; init; }

    public double Amount { get; init; }

    public float Ratio { get; init; }

    public long Count { get; init; }

    public ulong Big { get; init; }

    public DateTime At { get; init; } = new(2026, 11, 24, 10, 30, 0);

    public DateOnly On { get; init; } = new(2026, 11, 24);

    public TimeOnly Time { get; init; } = new(10, 30);

    public DateTimeOffset Instant { get; init; } = new(2026, 11, 24, 10, 0, 0, TimeSpan.FromHours(1));

    public Guid Id { get; init; }

    public int? Missing { get; init; }
}

[SpreadsheetRecord("Data")]
public sealed partial class DataRow {
    public string? Value { get; init; }
}

[SpreadsheetRecord("data")]
public sealed partial class LowerDataRow {
    public string? Value { get; init; }
}

[SpreadsheetRecord]
public sealed partial class WidthRow {
    public string? Short { get; init; }

    [SpreadsheetColumn("A rather long header")]
    public string? Long { get; init; }

    [SpreadsheetColumn(Width = 30)]
    public string? Fixed { get; init; }

    public string? Huge { get; init; }
}

public enum SalesRegion {
    [Display(Name = "Sales region number 01")] Region01,
    [Display(Name = "Sales region number 02")] Region02,
    [Display(Name = "Sales region number 03")] Region03,
    [Display(Name = "Sales region number 04")] Region04,
    [Display(Name = "Sales region number 05")] Region05,
    [Display(Name = "Sales region number 06")] Region06,
    [Display(Name = "Sales region number 07")] Region07,
    [Display(Name = "Sales region number 08")] Region08,
    [Display(Name = "Sales region number 09")] Region09,
    [Display(Name = "Sales region number 10")] Region10,
    [Display(Name = "Sales region number 11")] Region11,
    [Display(Name = "Sales region number 12")] Region12,
    [Display(Name = "Sales region number 13")] Region13,
    [Display(Name = "Sales region number 14")] Region14,
    [Display(Name = "Sales region number 15")] Region15,
    [Display(Name = "Sales region number 16")] Region16,
    [Display(Name = "Sales region number 17")] Region17,
    [Display(Name = "Sales region number 18")] Region18,
    [Display(Name = "Sales region number 19")] Region19,
    [Display(Name = "Sales region number 20")] Region20,
    [Display(Name = "Sales region number 21")] Region21,
    [Display(Name = "Sales region number 22")] Region22,
    [Display(Name = "Sales region number 23")] Region23,
    [Display(Name = "Sales region number 24")] Region24,
    [Display(Name = "Sales region number 25")] Region25,
    [Display(Name = "Sales region number 26")] Region26,
    [Display(Name = "Sales region number 27")] Region27,
    [Display(Name = "Sales region number 28")] Region28,
    [Display(Name = "Sales region number 29")] Region29,
    [Display(Name = "Sales region number 30")] Region30,
    [Display(Name = "Sales region number 31")] Region31,
    [Display(Name = "Sales region number 32")] Region32,
    [Display(Name = "Sales region number 33")] Region33,
    [Display(Name = "Sales region number 34")] Region34,
    [Display(Name = "Sales region number 35")] Region35,
    [Display(Name = "Sales region number 36")] Region36,
    [Display(Name = "Sales region number 37")] Region37,
    [Display(Name = "Sales region number 38")] Region38,
    [Display(Name = "Sales region number 39")] Region39,
    [Display(Name = "Sales region number 40")] Region40,
    [Display(Name = "Sales region number 41")] Region41,
    [Display(Name = "Sales region number 42")] Region42,
    [Display(Name = "Sales region number 43")] Region43,
    [Display(Name = "Sales region number 44")] Region44,
    [Display(Name = "Sales region number 45")] Region45,
    [Display(Name = "Sales region number 46")] Region46,
    [Display(Name = "Sales region number 47")] Region47,
    [Display(Name = "Sales region number 48")] Region48,
    [Display(Name = "Sales region number 49")] Region49,
    [Display(Name = "Sales region number 50")] Region50,
    [Display(Name = "Sales region number 51")] Region51,
    [Display(Name = "Sales region number 52")] Region52,
    [Display(Name = "Sales region number 53")] Region53,
    [Display(Name = "Sales region number 54")] Region54,
    [Display(Name = "Sales region number 55")] Region55,
    [Display(Name = "Sales region number 56")] Region56,
    [Display(Name = "Sales region number 57")] Region57,
    [Display(Name = "Sales region number 58")] Region58,
    [Display(Name = "Sales region number 59")] Region59,
    [Display(Name = "Sales region number 60")] Region60,
}

[SpreadsheetRecord("Regions")]
public sealed partial class RegionRow {
    public string? Name { get; init; }

    public SalesRegion Region { get; init; }

    public SalesRegion? Backup { get; init; }
}

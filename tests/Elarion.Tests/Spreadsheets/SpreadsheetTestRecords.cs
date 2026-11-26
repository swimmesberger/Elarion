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

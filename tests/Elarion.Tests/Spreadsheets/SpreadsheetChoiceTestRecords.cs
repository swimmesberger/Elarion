using Elarion.Spreadsheets;

namespace Elarion.Tests.Spreadsheets;

/// <summary>Text columns whose dropdown labels are data rather than an enum (FR-077, FR-078).</summary>
[SpreadsheetRecord("Channels")]
public sealed partial class ChannelRow {
    public static readonly string[] Platforms = ["Website", "Mail", "Phone"];

    public static IReadOnlyList<string> Labels { get; } = ["Private", "Work"];

    public required string Name { get; init; }

    [SpreadsheetColumn(Choices = nameof(Labels))]
    public string? Label { get; init; }

    [SpreadsheetColumn(Choices = nameof(Platforms), StrictChoices = true)]
    public string? Platform { get; init; }
}

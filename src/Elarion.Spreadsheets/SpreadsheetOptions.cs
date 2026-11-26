namespace Elarion.Spreadsheets;

/// <summary>
/// The application's spreadsheet settings, bound by <c>AddElarionSpreadsheets(configuration)</c> from the
/// <see cref="SectionName"/> section, for example <c>Spreadsheets:ReadDefaults:MaxRows</c>.
/// </summary>
public sealed class SpreadsheetOptions {
    /// <summary>The conventional configuration section: <c>Spreadsheets</c>.</summary>
    public const string SectionName = "Spreadsheets";

    /// <summary>The read options a read uses when it passes none.</summary>
    public SpreadsheetReadOptions ReadDefaults { get; set; } = new();
}

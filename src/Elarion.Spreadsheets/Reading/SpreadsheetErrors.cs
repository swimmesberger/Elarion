using System.Globalization;

namespace Elarion.Spreadsheets.Reading;

/// <summary>The package's own errors with their English default messages (FR-050), in one place.</summary>
internal static class SpreadsheetErrors {
    public static SpreadsheetError Unreadable(string reason) {
        return new SpreadsheetError(SpreadsheetErrorCodes.Unreadable, $"The file cannot be read as a workbook: {reason}.");
    }

    public static SpreadsheetError TooLarge(long limit) {
        return new SpreadsheetError(
            SpreadsheetErrorCodes.TooLarge,
            $"The workbook is too large: it unpacks to more than {limit.ToString("N0", CultureInfo.InvariantCulture)} bytes.",
            limit: limit);
    }

    public static SpreadsheetError NoVisibleWorksheet() {
        return new SpreadsheetError(SpreadsheetErrorCodes.NoVisibleWorksheet, "The workbook has no visible worksheet.");
    }

    public static SpreadsheetError WorksheetNotFound(string name) {
        return new SpreadsheetError(
            SpreadsheetErrorCodes.WorksheetNotFound, $"The workbook has no visible worksheet named '{name}'.", name);
    }

    public static SpreadsheetError TooManyRows(string worksheet, int limit, int rowNumber) {
        return new SpreadsheetError(
            SpreadsheetErrorCodes.TooManyRows,
            $"The worksheet has more than {limit.ToString("N0", CultureInfo.InvariantCulture)} data rows; row {rowNumber.ToString(CultureInfo.InvariantCulture)} and the rows after it were not read.",
            worksheet,
            rowNumber,
            limit: limit);
    }

    public static SpreadsheetError TooManyColumns(string worksheet, int limit, int rowNumber) {
        return new SpreadsheetError(
            SpreadsheetErrorCodes.TooManyColumns,
            $"The row has more than {limit.ToString("N0", CultureInfo.InvariantCulture)} columns; it and the rows after it were not read.",
            worksheet,
            rowNumber,
            limit: limit);
    }
}

namespace Elarion.Spreadsheets.Reading;

/// <summary>
/// Reads one opened worksheet as plain rows of typed cells (ADR-0108, decision 9): the same source, cell rules and row
/// limit as a record read, without header resolution, plus the column limit (FR-058, FR-059).
/// </summary>
internal static class PlainRowReader {
    public static SpreadsheetRowsResult Read(IWorkbookSource source, string worksheet, SpreadsheetReadOptions options) {
        var rows = new List<SpreadsheetCellRow>();
        var errors = new List<SpreadsheetError>();
        try {
            while (source.ReadRow()) {
                var last = -1;
                for (var ordinal = source.CellCount - 1; ordinal >= 0; ordinal--)
                    if (!IsEmpty(source.GetCell(ordinal))) {
                        last = ordinal;
                        break;
                    }

                if (last < 0) continue;

                if (last >= options.MaxColumns) {
                    errors.Add(SpreadsheetErrors.TooManyColumns(worksheet, options.MaxColumns, source.RowNumber));
                    break;
                }

                if (rows.Count >= options.MaxRows) {
                    errors.Add(SpreadsheetErrors.TooManyRows(worksheet, options.MaxRows, source.RowNumber));
                    break;
                }

                var cells = new SpreadsheetCell[last + 1];
                for (var ordinal = 0; ordinal <= last; ordinal++) cells[ordinal] = source.GetCell(ordinal);

                rows.Add(new SpreadsheetCellRow(source.RowNumber, cells));
            }
        }
        catch (UnreadableWorkbookException) {
            return new SpreadsheetRowsResult(worksheet, [], [SpreadsheetErrors.Unreadable("it is damaged or incomplete")]);
        }

        return new SpreadsheetRowsResult(worksheet, rows, errors);
    }

    private static bool IsEmpty(SpreadsheetCell cell) {
        return cell.Kind == SpreadsheetCellKind.Blank
               || (cell.Kind == SpreadsheetCellKind.Text && string.IsNullOrWhiteSpace(cell.Text));
    }
}

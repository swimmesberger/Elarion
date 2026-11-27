namespace Elarion.Spreadsheets.Writing;

/// <summary>
/// The dropdown lists of a workbook's choice columns (FR-021, FR-022): a list data validation per column, its labels
/// inline when they fit the 255 characters a spreadsheet application allows there, otherwise a range on one hidden
/// worksheet that holds every long list of the workbook.
/// </summary>
internal sealed class SpreadsheetDropdowns {
    /// <summary>The longest list a data validation carries inline, separators included.</summary>
    public const int MaxInlineLength = 255;

    private const string ListsWorksheetBaseName = "Lists";

    // The long lists in the order first met, each written once however many columns offer it.
    private readonly List<IReadOnlyList<string>> _lists = [];
    private readonly Dictionary<string, int> _listIndexes = new(StringComparer.Ordinal);

    /// <summary>The name of the hidden worksheet holding the long lists, or <see langword="null"/> when there is none.</summary>
    public string? ListsWorksheetName { get; private set; }

    /// <summary>
    /// Appends the <c>&lt;dataValidations&gt;</c> of a worksheet, after its <c>&lt;sheetData&gt;</c>, over rows
    /// <paramref name="firstRow"/> to <paramref name="lastRow"/>; appends nothing when no column offers a list or the
    /// range is empty.
    /// </summary>
    /// <param name="xml">The worksheet's buffer.</param>
    /// <param name="columns">The worksheet's columns.</param>
    /// <param name="firstRow">The first row the lists cover: the one below the header row.</param>
    /// <param name="lastRow">The last row the lists cover.</param>
    /// <param name="worksheetNames">
    /// The names of the workbook's worksheets so far; the hidden worksheet's name is chosen to differ from them and is
    /// added to them, so a later worksheet cannot take it.
    /// </param>
    public void AppendDataValidations(
        Utf8XmlBuffer xml, IReadOnlyList<WorksheetColumn> columns, int firstRow, int lastRow, HashSet<string> worksheetNames) {
        if (lastRow < firstRow) return;

        var count = 0;
        foreach (var column in columns)
            if (column.IsDropdown)
                count++;

        if (count == 0) return;

        xml.Append("<dataValidations count=\""u8);
        xml.AppendFormatted(count);
        xml.Append("\">"u8);
        for (var index = 0; index < columns.Count; index++) {
            var column = columns[index];
            if (!column.IsDropdown) continue;

            var letters = SpreadsheetCells.ColumnLetters(index);
            // A blank cell stays allowed: whether a value is required is the reader's to say, with the row's number.
            xml.Append("<dataValidation type=\"list\" allowBlank=\"1\" showErrorMessage=\"1\" sqref=\""u8);
            xml.AppendRaw(letters);
            xml.AppendFormatted(firstRow);
            xml.Append((byte)':');
            xml.AppendRaw(letters);
            xml.AppendFormatted(lastRow);
            xml.Append("\"><formula1>"u8);
            if (CanBeInline(column.Choices)) {
                xml.Append((byte)'"');
                for (var i = 0; i < column.Choices.Count; i++) {
                    if (i > 0) xml.Append((byte)',');
                    xml.AppendAttribute(column.Choices[i]);
                }

                xml.Append((byte)'"');
            }
            else {
                AppendListReference(xml, ListIndex(column.Choices, worksheetNames), column.Choices.Count);
            }

            xml.Append("</formula1></dataValidation>"u8);
        }

        xml.Append("</dataValidations>"u8);
    }

    /// <summary>Appends the hidden worksheet: one column per long list, its labels from row 1 down.</summary>
    public void AppendListsWorksheet(Utf8XmlBuffer xml) {
        xml.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>"""u8);
        xml.Append("""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>"""u8);
        var rows = 0;
        foreach (var list in _lists) rows = Math.Max(rows, list.Count);

        for (var row = 0; row < rows; row++) {
            xml.Append("<row r=\""u8);
            xml.AppendFormatted(row + 1);
            xml.Append("\">"u8);
            for (var list = 0; list < _lists.Count; list++) {
                if (row >= _lists[list].Count) continue;

                var label = _lists[list][row];
                xml.Append("<c r=\""u8);
                xml.AppendRaw(SpreadsheetCells.ColumnLetters(list));
                xml.AppendFormatted(row + 1);
                xml.Append("\" t=\"inlineStr\">"u8);
                var preserve = label.Length > 0 && (char.IsWhiteSpace(label[0]) || char.IsWhiteSpace(label[^1]));
                xml.Append(preserve ? "<is><t xml:space=\"preserve\">"u8 : "<is><t>"u8);
                xml.AppendText(label);
                xml.Append("</t></is></c>"u8);
            }

            xml.Append("</row>"u8);
        }

        xml.Append("</sheetData></worksheet>"u8);
    }

    // An inline list has no escape for its separator or its quotes, and its formula text is read without the cell
    // text's _xHHHH_ escapes; any such label goes to the hidden worksheet instead, where it is an ordinary cell.
    private static bool CanBeInline(IReadOnlyList<string> choices) {
        var length = choices.Count - 1;
        foreach (var choice in choices) {
            length += choice.Length;
            if (length > MaxInlineLength) return false;
            if (choice.AsSpan().IndexOfAny(",\"") >= 0 || choice.Contains("_x", StringComparison.Ordinal)) return false;
            if (!Utf8XmlBuffer.IsXmlCharacterData(choice)) return false;
        }

        return true;
    }

    private int ListIndex(IReadOnlyList<string> choices, HashSet<string> worksheetNames) {
        var key = string.Join('\0', choices);
        if (_listIndexes.TryGetValue(key, out var index)) return index;

        if (ListsWorksheetName is null) {
            var name = ListsWorksheetBaseName;
            for (var suffix = 2; worksheetNames.Contains(name); suffix++) name = $"{ListsWorksheetBaseName} {suffix}";

            worksheetNames.Add(name);
            ListsWorksheetName = name;
        }

        index = _lists.Count;
        _lists.Add(choices);
        _listIndexes.Add(key, index);
        return index;
    }

    private void AppendListReference(Utf8XmlBuffer xml, int list, int count) {
        var letters = SpreadsheetCells.ColumnLetters(list);
        // Quoted, so a name with a space ("Lists 2") is one reference.
        xml.Append((byte)'\'');
        xml.AppendAttribute(ListsWorksheetName!);
        xml.Append("'!$"u8);
        xml.AppendRaw(letters);
        xml.Append("$1:$"u8);
        xml.AppendRaw(letters);
        xml.Append((byte)'$');
        xml.AppendFormatted(count);
    }
}

namespace Elarion.Spreadsheets.Writing;

/// <summary>
/// The workbook's cell formats: a fixed table (default, header, the built-in date, date-and-time, time and text formats)
/// plus one entry per distinct declared <c>Format</c>, in order of first use, so the same records give the same
/// <c>styles.xml</c> (FR-017, FR-018, FR-020, FR-025, FR-063).
/// </summary>
internal sealed class SpreadsheetStyles {
    public const int Default = 0;
    public const int Header = 1;

    // Built-in number formats 14, 22 and 21 show in the viewer's own date and time notation (FR-017); 49 is "@", text.
    public const int Date = 2;
    public const int DateTime = 3;
    public const int Time = 4;
    public const int Text = 5;
    private const int FirstCustom = 6;
    private const int FirstCustomNumberFormatId = 164;

    private readonly List<string> _customFormats = [];
    private readonly Dictionary<string, int> _customIndex = new(StringComparer.Ordinal);

    /// <summary>The style of a column's data cells.</summary>
    public int StyleFor(SpreadsheetValueKind kind, string? format) {
        return kind is SpreadsheetValueKind.Integer or SpreadsheetValueKind.Number or SpreadsheetValueKind.Date
                   or SpreadsheetValueKind.DateTime or SpreadsheetValueKind.Time
               && format is not null
            ? Custom(format)
            : BuiltInStyleFor(kind);
    }

    /// <summary>
    /// The style of a cell of <paramref name="kind"/> without a declared format: the text format, the viewer's own date,
    /// date-and-time or time notation, or the default.
    /// </summary>
    public static int BuiltInStyleFor(SpreadsheetValueKind kind) {
        return kind switch {
            SpreadsheetValueKind.Text or SpreadsheetValueKind.Guid or SpreadsheetValueKind.DateTimeOffset
                or SpreadsheetValueKind.Choice => Text,
            SpreadsheetValueKind.Date => Date,
            SpreadsheetValueKind.DateTime => DateTime,
            SpreadsheetValueKind.Time => Time,
            _ => Default
        };
    }

    public void Write(Utf8XmlBuffer xml) {
        xml.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>"""u8);
        xml.Append("""<styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">"""u8);
        if (_customFormats.Count > 0) {
            xml.Append("<numFmts count=\""u8);
            xml.AppendFormatted(_customFormats.Count);
            xml.Append("\">"u8);
            for (var i = 0; i < _customFormats.Count; i++) {
                xml.Append("<numFmt numFmtId=\""u8);
                xml.AppendFormatted(FirstCustomNumberFormatId + i);
                xml.Append("\" formatCode=\""u8);
                xml.AppendAttribute(_customFormats[i]);
                xml.Append("\"/>"u8);
            }

            xml.Append("</numFmts>"u8);
        }

        xml.Append("""<fonts count="2"><font><sz val="11"/><name val="Calibri"/><family val="2"/></font><font><b/><sz val="11"/><name val="Calibri"/><family val="2"/></font></fonts>"""u8);
        xml.Append("""<fills count="3"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FFEEF2F7"/><bgColor indexed="64"/></patternFill></fill></fills>"""u8);
        xml.Append("""<borders count="2"><border><left/><right/><top/><bottom/><diagonal/></border><border><left/><right/><top/><bottom style="thin"><color auto="1"/></bottom><diagonal/></border></borders>"""u8);
        xml.Append("""<cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>"""u8);
        xml.Append("<cellXfs count=\""u8);
        xml.AppendFormatted(FirstCustom + _customFormats.Count);
        xml.Append("\">"u8);
        xml.Append("""<xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>"""u8);
        xml.Append("""<xf numFmtId="0" fontId="1" fillId="2" borderId="1" xfId="0" applyFont="1" applyFill="1" applyBorder="1"/>"""u8);
        xml.Append("""<xf numFmtId="14" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>"""u8);
        xml.Append("""<xf numFmtId="22" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>"""u8);
        xml.Append("""<xf numFmtId="21" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>"""u8);
        xml.Append("""<xf numFmtId="49" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/>"""u8);
        for (var i = 0; i < _customFormats.Count; i++) {
            xml.Append("<xf numFmtId=\""u8);
            xml.AppendFormatted(FirstCustomNumberFormatId + i);
            xml.Append("\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>"u8);
        }

        xml.Append("</cellXfs>"u8);
        xml.Append("""<cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>"""u8);
        xml.Append("</styleSheet>"u8);
    }

    private int Custom(string format) {
        if (_customIndex.TryGetValue(format, out var style)) return style;

        style = FirstCustom + _customFormats.Count;
        _customFormats.Add(format);
        _customIndex.Add(format, style);
        return style;
    }
}

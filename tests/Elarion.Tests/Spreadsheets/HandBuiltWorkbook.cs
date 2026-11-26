using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Elarion.Tests.Spreadsheets;

/// <summary>
/// Builds <c>.xlsx</c> files part by part, the way other applications write them, so the reader is tested on shapes the
/// package's own writer never produces: shared and rich-text strings, cells without references, formulas with cached
/// values, the 1904 date system, Strict OOXML, hidden worksheets, and hostile parts.
/// </summary>
internal static class HandBuiltWorkbook {
    public const string MainNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    public const string StrictMainNamespace = "http://purl.oclc.org/ooxml/spreadsheetml/main";

    private const string PackageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string TransitionalRelationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string StrictRelationships = "http://purl.oclc.org/ooxml/officeDocument/relationships";

    /// <summary>A billion-laughs DTD; its entity <c>&amp;i;</c> expands to 1,000,000,000 characters.</summary>
    public const string EntityBomb =
        """<!DOCTYPE x [<!ENTITY a "aaaaaaaaaa"><!ENTITY b "&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;"><!ENTITY c "&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;"><!ENTITY d "&c;&c;&c;&c;&c;&c;&c;&c;&c;&c;"><!ENTITY e "&d;&d;&d;&d;&d;&d;&d;&d;&d;&d;"><!ENTITY f "&e;&e;&e;&e;&e;&e;&e;&e;&e;&e;"><!ENTITY g "&f;&f;&f;&f;&f;&f;&f;&f;&f;&f;"><!ENTITY h "&g;&g;&g;&g;&g;&g;&g;&g;&g;&g;"><!ENTITY i "&h;&h;&h;&h;&h;&h;&h;&h;&h;&h;">]>""";

    /// <summary>The styles of every built workbook: <c>s="1"</c> is a date (format 14), <c>s="2"</c> a custom
    /// <c>dd.mm.yyyy</c> date, <c>s="3"</c> a time (format 21), <c>s="4"</c> the text format <c>@</c>, <c>s="5"</c> a
    /// custom date and time.</summary>
    private static string Styles(string main) {
        return $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <styleSheet xmlns="{main}"><numFmts count="2"><numFmt numFmtId="164" formatCode="dd\.mm\.yyyy"/><numFmt numFmtId="165" formatCode="yyyy\-mm\-dd\ hh:mm"/></numFmts>
                <fonts count="1"><font><sz val="11"/><name val="Calibri"/></font></fonts>
                <fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills>
                <borders count="1"><border/></borders>
                <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
                <cellXfs count="6"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="14" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/><xf numFmtId="164" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/><xf numFmtId="21" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/><xf numFmtId="49" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/><xf numFmtId="165" fontId="0" fillId="0" borderId="0" xfId="0" applyNumberFormat="1"/></cellXfs>
                </styleSheet>
                """;
    }

    /// <summary>A worksheet whose rows are given as <c>&lt;row&gt;</c> elements.</summary>
    public sealed record Sheet(string Name, string Rows, string? State = null);

    /// <summary>
    /// Builds a workbook. <paramref name="sharedStrings"/> are the <c>&lt;si&gt;</c> items; <paramref name="replace"/>
    /// replaces or adds whole parts after they are built, for hostile variants.
    /// </summary>
    public static byte[] Build(
        IReadOnlyList<Sheet> sheets,
        bool strict = false,
        bool date1904 = false,
        IReadOnlyList<string>? sharedStrings = null,
        IReadOnlyDictionary<string, string>? replace = null) {
        var main = strict ? StrictMainNamespace : MainNamespace;
        var relationships = strict ? StrictRelationships : TransitionalRelationships;
        var sheetElements = string.Concat(sheets.Select((sheet, i) =>
            $"""<sheet name="{sheet.Name}" sheetId="{i + 1}"{(sheet.State is null ? "" : $" state=\"{sheet.State}\"")} r:id="rId{i + 1}"/>"""));
        var parts = new Dictionary<string, string>(StringComparer.Ordinal) {
            ["[Content_Types].xml"] =
                """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/></Types>""",
            ["_rels/.rels"] =
                $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="{PackageRelationships}"><Relationship Id="rId1" Type="{relationships}/officeDocument" Target="xl/workbook.xml"/></Relationships>""",
            ["xl/workbook.xml"] =
                $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="{main}" xmlns:r="{relationships}">{(date1904 ? "<workbookPr date1904=\"1\"/>" : "")}<sheets>{sheetElements}</sheets></workbook>""",
            ["xl/_rels/workbook.xml.rels"] =
                $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="{PackageRelationships}">{string.Concat(sheets.Select((_, i) => $"""<Relationship Id="rId{i + 1}" Type="{relationships}/worksheet" Target="worksheets/sheet{i + 1}.xml"/>"""))}<Relationship Id="rId{sheets.Count + 1}" Type="{relationships}/styles" Target="styles.xml"/><Relationship Id="rId{sheets.Count + 2}" Type="{relationships}/sharedStrings" Target="sharedStrings.xml"/></Relationships>""",
            ["xl/styles.xml"] = Styles(main),
            ["xl/sharedStrings.xml"] =
                $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><sst xmlns="{main}" count="{sharedStrings?.Count ?? 0}" uniqueCount="{sharedStrings?.Count ?? 0}">{string.Concat(sharedStrings ?? [])}</sst>"""
        };
        for (var i = 0; i < sheets.Count; i++)
            parts[$"xl/worksheets/sheet{i + 1}.xml"] =
                $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="{main}"><sheetData>{sheets[i].Rows}</sheetData></worksheet>""";

        if (replace is not null)
            foreach (var (name, content) in replace)
                parts[name] = content;

        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true)) {
            foreach (var (name, content) in parts) {
                using var entry = archive.CreateEntry(name).Open();
                entry.Write(Encoding.UTF8.GetBytes(content));
            }
        }

        return stream.ToArray();
    }

    /// <summary>A one-worksheet workbook named "Data" with the given rows.</summary>
    public static byte[] Build(string rows, bool date1904 = false, IReadOnlyList<string>? sharedStrings = null) {
        return Build([new Sheet("Data", rows)], date1904: date1904, sharedStrings: sharedStrings);
    }

    /// <summary>An inline-string cell.</summary>
    public static string Text(string reference, string text) {
        return $"""<c r="{reference}" t="inlineStr"><is><t xml:space="preserve">{System.Security.SecurityElement.Escape(text)}</t></is></c>""";
    }

    /// <summary>A number cell with an optional style.</summary>
    public static string Number(string reference, string value, int style = 0) {
        return style == 0 ? $"""<c r="{reference}"><v>{value}</v></c>""" : $"""<c r="{reference}" s="{style}"><v>{value}</v></c>""";
    }

    /// <summary>A row of inline-string cells starting in column A.</summary>
    public static string TextRow(int row, params string[] texts) {
        return $"""<row r="{row}">{string.Concat(texts.Select((text, i) => text.Length == 0 ? "" : Text(Column(i) + row, text)))}</row>""";
    }

    public static string Column(int index) {
        var letters = "";
        for (var n = index + 1; n > 0; n = (n - 1) / 26) letters = (char)('A' + (n - 1) % 26) + letters;

        return letters;
    }

    /// <summary>
    /// Writes a zip by hand whose entry <paramref name="name"/> holds <paramref name="compressed"/> deflate data while
    /// its headers declare <paramref name="declaredSize"/> unpacked bytes: a size that lies, or an honest one too large to
    /// unpack. The other entries are <paramref name="workbook"/>'s.
    /// </summary>
    public static byte[] WithEntry(byte[] workbook, string name, byte[] compressed, long declaredSize, uint crc) {
        var entries = new List<(string Name, byte[] Data, long Size, uint Crc)>();
        using (var archive = new ZipArchive(new MemoryStream(workbook), ZipArchiveMode.Read)) {
            foreach (var entry in archive.Entries) {
                if (entry.FullName == name) continue;

                using var source = entry.Open();
                using var raw = new MemoryStream();
                source.CopyTo(raw);
                var bytes = raw.ToArray();
                entries.Add((entry.FullName, Deflate(bytes), bytes.Length, Crc32(bytes)));
            }
        }

        entries.Add((name, compressed, declaredSize, crc));
        var output = new MemoryStream();
        var central = new MemoryStream();
        foreach (var (entryName, data, size, entryCrc) in entries) {
            var nameBytes = Encoding.UTF8.GetBytes(entryName);
            var offset = output.Position;
            var zip64 = size > uint.MaxValue;
            WriteLocalHeader(output, nameBytes, data.Length, size, entryCrc, zip64);
            output.Write(data);
            WriteCentralHeader(central, nameBytes, data.Length, size, entryCrc, offset, zip64);
        }

        var centralOffset = output.Position;
        central.Position = 0;
        central.CopyTo(output);
        Span<byte> end = stackalloc byte[22];
        BinaryPrimitives.WriteUInt32LittleEndian(end, 0x06054b50);
        BinaryPrimitives.WriteUInt16LittleEndian(end[8..], (ushort)entries.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(end[10..], (ushort)entries.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(end[12..], (uint)central.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(end[16..], (uint)centralOffset);
        output.Write(end);
        return output.ToArray();
    }

    public static uint Crc32(ReadOnlySpan<byte> data) {
        var crc = uint.MaxValue;
        foreach (var b in data) {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }

        return ~crc;
    }

    public static byte[] Deflate(byte[] data) {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true)) deflate.Write(data);

        return output.ToArray();
    }

    private static void WriteLocalHeader(Stream stream, byte[] name, long compressed, long size, uint crc, bool zip64) {
        var extra = zip64 ? 20 : 0;
        var header = new byte[30 + name.Length + extra];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x04034b50);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), (ushort)(zip64 ? 45 : 20));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(14), crc);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(18), zip64 ? uint.MaxValue : (uint)compressed);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(22), zip64 ? uint.MaxValue : (uint)size);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(26), (ushort)name.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(28), (ushort)extra);
        name.CopyTo(header, 30);
        if (zip64) {
            var field = header.AsSpan(30 + name.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(field, 1);
            BinaryPrimitives.WriteUInt16LittleEndian(field[2..], 16);
            BinaryPrimitives.WriteUInt64LittleEndian(field[4..], (ulong)size);
            BinaryPrimitives.WriteUInt64LittleEndian(field[12..], (ulong)compressed);
        }

        stream.Write(header);
    }

    private static void WriteCentralHeader(Stream stream, byte[] name, long compressed, long size, uint crc, long offset, bool zip64) {
        var extra = zip64 ? 20 : 0;
        var header = new byte[46 + name.Length + extra];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x02014b50);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), (ushort)(zip64 ? 45 : 20));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), (ushort)(zip64 ? 45 : 20));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), crc);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), zip64 ? uint.MaxValue : (uint)compressed);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), zip64 ? uint.MaxValue : (uint)size);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(28), (ushort)name.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(30), (ushort)extra);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(42), (uint)offset);
        name.CopyTo(header, 46);
        if (zip64) {
            var field = header.AsSpan(46 + name.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(field, 1);
            BinaryPrimitives.WriteUInt16LittleEndian(field[2..], 16);
            BinaryPrimitives.WriteUInt64LittleEndian(field[4..], (ulong)size);
            BinaryPrimitives.WriteUInt64LittleEndian(field[12..], (ulong)compressed);
        }

        stream.Write(header);
    }

    /// <summary>
    /// A compound file of one 512-byte header and one directory sector holding a single stream entry named
    /// <paramref name="streamName"/>: enough for the format detection, not a real workbook.
    /// </summary>
    public static byte[] CompoundFile(string streamName) {
        var bytes = new byte[512 * 3];
        byte[] magic = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
        magic.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(30), 9);
        var entry = bytes.AsSpan(512 + 128);
        for (var i = 0; i < streamName.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(entry[(i * 2)..], streamName[i]);

        BinaryPrimitives.WriteUInt16LittleEndian(entry[64..], (ushort)((streamName.Length + 1) * 2));
        entry[66] = 2;
        return bytes;
    }
}

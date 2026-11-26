using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Elarion.Spreadsheets.Reading;

/// <summary>The workbook formats the reader recognizes from the content (FR-028).</summary>
internal enum WorkbookFormat {
    Xlsx,
    Xlsb,
    Xls
}

/// <summary>What the pre-pass found: the format and the worksheets the reader library would wrongly show.</summary>
internal sealed record WorkbookInspection(WorkbookFormat Format, IReadOnlySet<string> VeryHiddenWorksheets);

/// <summary>
/// The one pre-pass before the reader library sees a file (ADR-0108, decision 3 and 10): recognizes the format by its
/// magic bytes, bounds what a zip-based workbook may unpack to, refuses a document type definition in the parts the
/// library parses with a DTD-processing reader, and reads the <c>veryHidden</c> worksheets the library treats as
/// visible.
/// </summary>
/// <remarks>
/// <para>
/// The unpacked-size check sums the sizes the zip's central directory declares. That bounds inflation, because .NET's
/// zip reader ends an entry's stream at its declared size: a part whose size lies is truncated (and then fails to parse)
/// instead of inflating past the limit (FR-055).
/// </para>
/// <para>
/// A document type definition can only stand in an XML part's prolog, so the scan reads each part only up to its root
/// element. The library loads <c>xl/workbook.xml</c>, <c>xl/styles.xml</c> and the <c>.rels</c> parts with a reader that
/// processes DTDs; every XML part is scanned all the same, so no future library change opens the hole again (FR-057).
/// </para>
/// </remarks>
internal static class WorkbookInspector {
    private const int PrologScanLimit = 64 * 1024;

    private static readonly byte[] ZipMagic = "PK"u8.ToArray();
    private static readonly byte[] CompoundFileMagic = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    /// <summary>Inspects the workbook in <paramref name="content"/>, or returns the error that refuses it.</summary>
    public static WorkbookInspection? Inspect(MemoryStream content, long maxUnpackedBytes, out SpreadsheetError? error) {
        error = null;
        var bytes = content.GetBuffer().AsSpan(0, (int)content.Length);
        if (bytes.StartsWith(CompoundFileMagic)) return InspectCompoundFile(bytes, out error);
        if (bytes.StartsWith(ZipMagic)) return InspectZip(content, maxUnpackedBytes, out error);

        error = SpreadsheetErrors.Unreadable("it is not an .xlsx, .xlsb or .xls workbook");
        return null;
    }

    private static WorkbookInspection? InspectZip(MemoryStream content, long maxUnpackedBytes, out SpreadsheetError? error) {
        error = null;
        try {
            content.Position = 0;
            using var archive = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true);
            long declared = 0;
            foreach (var entry in archive.Entries) {
                declared += entry.Length;
                if (declared > maxUnpackedBytes) {
                    error = SpreadsheetErrors.TooLarge(maxUnpackedBytes);
                    return null;
                }
            }

            foreach (var entry in archive.Entries)
                if (IsXmlPart(entry.FullName) && HasDocumentTypeDefinition(entry)) {
                    error = SpreadsheetErrors.Unreadable($"its part '{entry.FullName}' declares a document type, which a workbook never needs");
                    return null;
                }

            if (archive.GetEntry("xl/workbook.bin") is not null)
                return new WorkbookInspection(WorkbookFormat.Xlsb, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            if (archive.GetEntry("xl/workbook.xml") is not { } workbook) {
                error = SpreadsheetErrors.Unreadable("it is a zip archive without a workbook");
                return null;
            }

            return new WorkbookInspection(WorkbookFormat.Xlsx, ReadVeryHiddenWorksheets(workbook));
        }
        catch (Exception exception) when (exception is InvalidDataException or XmlException or IOException
                                              or NotSupportedException or ArgumentException) {
            error = SpreadsheetErrors.Unreadable("it is damaged or incomplete");
            return null;
        }
        finally {
            content.Position = 0;
        }
    }

    private static bool IsXmlPart(string name) {
        return name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".vml", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether the part's prolog holds a <c>&lt;!DOCTYPE</c>: skips the byte-order mark, white space, the XML declaration,
    /// processing instructions and comments, and stops at the first other markup, which is the root element.
    /// </summary>
    private static bool HasDocumentTypeDefinition(ZipArchiveEntry entry) {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024);
        var read = 0;
        while (read < PrologScanLimit) {
            var next = reader.Read();
            read++;
            if (next < 0) return false;
            if (char.IsWhiteSpace((char)next)) continue;
            if (next != '<') return false;

            var marker = reader.Read();
            read++;
            switch (marker) {
                case '?':
                    if (!SkipPast(reader, "?>", ref read)) return false;

                    continue;
                case '!':
                    var head = new char[7];
                    var length = reader.ReadBlock(head, 0, 2);
                    read += length;
                    if (length == 2 && head[0] == '-' && head[1] == '-') {
                        if (!SkipPast(reader, "-->", ref read)) return false;

                        continue;
                    }

                    length += reader.ReadBlock(head, 2, 5);
                    return length == 7 && new string(head).Equals("DOCTYPE", StringComparison.OrdinalIgnoreCase);
                default:
                    return false;
            }
        }

        // A prolog longer than any real part needs is refused as if it declared one.
        return true;
    }

    private static bool SkipPast(StreamReader reader, string terminator, ref int read) {
        var matched = 0;
        while (read < PrologScanLimit) {
            var next = reader.Read();
            read++;
            if (next < 0) return false;

            matched = next == terminator[matched] ? matched + 1 : next == terminator[0] ? 1 : 0;
            if (matched == terminator.Length) return true;
        }

        return false;
    }

    /// <summary>
    /// The names of the worksheets <c>xl/workbook.xml</c> marks <c>veryHidden</c>. The library shows them as visible;
    /// <c>hidden</c> ones it already skips. Elements are matched by local name, so Strict OOXML reads the same.
    /// </summary>
    private static HashSet<string> ReadVeryHiddenWorksheets(ZipArchiveEntry workbook) {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var stream = workbook.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true
        });
        while (reader.Read()) {
            if (reader is { NodeType: XmlNodeType.EndElement, LocalName: "sheets" }) break;
            if (reader is not { NodeType: XmlNodeType.Element, LocalName: "sheet" }) continue;

            if (reader.GetAttribute("state") == "veryHidden" && reader.GetAttribute("name") is { } name) names.Add(name);
        }

        return names;
    }

    /// <summary>
    /// A compound file is an <c>.xls</c> workbook, unless it carries an <c>EncryptedPackage</c> stream (a
    /// password-protected <c>.xlsx</c>) or its workbook stream opens with a <c>FILEPASS</c> record (a password-protected
    /// <c>.xls</c>).
    /// </summary>
    private static WorkbookInspection? InspectCompoundFile(ReadOnlySpan<byte> bytes, out SpreadsheetError? error) {
        error = null;
        if (bytes.Length < 512) {
            error = SpreadsheetErrors.Unreadable("it is damaged or incomplete");
            return null;
        }

        var sectorSize = 1 << BinaryPrimitives.ReadUInt16LittleEndian(bytes[30..]);
        if (sectorSize is not (512 or 4096)) {
            error = SpreadsheetErrors.Unreadable("it is damaged or incomplete");
            return null;
        }

        // Directory entries are 128 bytes, aligned to 128 within sectors that are themselves aligned in the file; reading
        // every aligned slot finds them without following the allocation table.
        for (var offset = sectorSize; offset + 128 <= bytes.Length; offset += 128) {
            var entry = bytes.Slice(offset, 128);
            if (entry[66] != 2) continue;

            if (IsEntryNamed(entry, "EncryptedPackage")) {
                error = SpreadsheetErrors.Unreadable("it is password-protected");
                return null;
            }

            if ((IsEntryNamed(entry, "Workbook") || IsEntryNamed(entry, "Book")) && IsBiffEncrypted(bytes, entry, sectorSize)) {
                error = SpreadsheetErrors.Unreadable("it is password-protected");
                return null;
            }
        }

        return new WorkbookInspection(WorkbookFormat.Xls, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    private static bool IsEntryNamed(ReadOnlySpan<byte> entry, string name) {
        var nameBytes = BinaryPrimitives.ReadUInt16LittleEndian(entry[64..]);
        if (nameBytes != (name.Length + 1) * 2) return false;

        for (var i = 0; i < name.Length; i++)
            if (BinaryPrimitives.ReadUInt16LittleEndian(entry[(i * 2)..]) != name[i])
                return false;

        return true;
    }

    /// <summary>
    /// Whether the BIFF workbook stream's first record after <c>BOF</c> is <c>FILEPASS</c> (0x002F). A stream below
    /// 4,096 bytes lives in the mini stream and is not checked; the library then fails on it, which is the same error.
    /// </summary>
    private static bool IsBiffEncrypted(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> entry, int sectorSize) {
        var startSector = BinaryPrimitives.ReadUInt32LittleEndian(entry[116..]);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(entry[120..]);
        if (size < 4096 || startSector >= 0xFFFFFFFA) return false;

        var start = ((long)startSector + 1) * sectorSize;
        if (start + 8 > bytes.Length) return false;

        var record = bytes[(int)start..];
        if (BinaryPrimitives.ReadUInt16LittleEndian(record) != 0x0809) return false;

        var next = 4 + BinaryPrimitives.ReadUInt16LittleEndian(record[2..]);
        return next + 2 <= record.Length && BinaryPrimitives.ReadUInt16LittleEndian(record[next..]) == 0x002F;
    }
}

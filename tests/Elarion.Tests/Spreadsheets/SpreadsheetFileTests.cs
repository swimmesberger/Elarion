using System.Runtime.CompilerServices;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.AspNetCore;
using Elarion.Spreadsheets;
using Xunit;

namespace Elarion.Tests.Spreadsheets;

public sealed class SpreadsheetFileTests {
    private const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static readonly ContactRow[] Contacts = [
        new() { Name = "Anna", Kind = ContactKind.Organization },
        new() { Name = "Bert", Kind = ContactKind.Authority, Active = true }
    ];

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ToFile_GivesAnXlsxFileWithTheContentTypeAndTheGivenFileName() {
        var file = await SpreadsheetWorkbook.ToFileAsync(Contacts, "contacts.xlsx", cancellationToken: CancellationToken);

        file.ContentType.Should().Be(XlsxContentType);
        SpreadsheetWorkbook.ContentType.Should().Be(XlsxContentType);
        file.FileName.Should().Be("contacts.xlsx");
        using var workbook = WrittenWorkbook.From(file.Bytes.ToArray());
        workbook.Rows().Should().HaveCount(3);
        WrittenWorkbook.Text(workbook.Cell("A3")).Should().Be("Bert");
    }

    [Fact]
    public async Task ToFile_FromRecordsProducedOneAtATime_GivesTheSameFile() {
        var buffered = await SpreadsheetWorkbook.ToFileAsync(Contacts, "contacts.xlsx", cancellationToken: CancellationToken);

        var produced = await SpreadsheetWorkbook.ToFileAsync(
            Produce(Contacts, CancellationToken), "contacts.xlsx", cancellationToken: CancellationToken);

        produced.ContentType.Should().Be(XlsxContentType);
        produced.FileName.Should().Be("contacts.xlsx");
        produced.Bytes.ToArray().Should().Equal(buffered.Bytes.ToArray());
    }

    [Fact]
    public async Task ToFile_WithoutAFileName_IsRefused() {
        var act = () => SpreadsheetWorkbook.ToFileAsync(Contacts, "", cancellationToken: CancellationToken).AsTask();

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task StreamedFile_CarriesTheContentTypeAndFileName_AndWritesThroughABodyThatRefusesSynchronousWrites() {
        await using var file = SpreadsheetWorkbook.ToStreamedFile(Produce(Contacts, CancellationToken), "contacts.xlsx");

        file.ContentType.Should().Be(XlsxContentType);
        file.FileName.Should().Be("contacts.xlsx");
        file.Disposition.Should().Be(FileDisposition.Attachment);
        var bytes = await WriteThroughResponseBodyAsync(file);

        using var workbook = WrittenWorkbook.From(bytes);
        workbook.Rows().Should().HaveCount(3);
        workbook.DataValidation("H").SequenceOfReferences!.InnerText.Should().Be("H2:H2001");
    }

    [Fact]
    public async Task StreamedFile_OfSeveralWorksheets_WritesEachInTheOrderAdded() {
        await using var file = SpreadsheetWorkbook.ToStreamedFile("customers.xlsx", async (workbook, ct) => {
            await workbook.AddWorksheetAsync(Contacts, cancellationToken: ct);
            await workbook.AddWorksheetAsync([new BankAccountRow { Iban = "AT61 1904 3002 3457 3201" }], cancellationToken: ct);
        });

        file.ContentType.Should().Be(XlsxContentType);
        file.FileName.Should().Be("customers.xlsx");
        var bytes = await WriteThroughResponseBodyAsync(file);

        using var workbook = WrittenWorkbook.From(bytes);
        workbook.WorksheetNames.Should().Equal("Contacts", "Bank accounts");
        workbook.Rows(worksheet: 1).Should().HaveCount(2);
    }

    [Fact]
    public async Task StreamedFile_WhoseWriterFails_FailsWithTheWritersException() {
        await using var file = SpreadsheetWorkbook.ToStreamedFile(
            "customers.xlsx", (_, _) => throw new InvalidOperationException("the export broke"));

        var act = () => file.WriteToAsync(new MemoryStream(), CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("the export broke");
    }

    // The body a streamed file's writer receives on a server: synchronous writes are held up to the tolerated limit
    // (the zip trailer), every other write must be asynchronous, and the stream is forward-only.
    private static async Task<byte[]> WriteThroughResponseBodyAsync(ElarionStreamedFile file) {
        var server = new AsyncOnlyStream();
        var body = new SynchronousWriteBufferingStream(server, 1024 * 1024);
        await file.WriteToAsync(body, CancellationToken);
        await body.FlushPendingAsync(CancellationToken);
        server.AsynchronousWrites.Should().BeGreaterThan(0);
        return server.ToArray();
    }

    private static async IAsyncEnumerable<T> Produce<T>(
        IEnumerable<T> records, [EnumeratorCancellation] CancellationToken cancellationToken = default) {
        foreach (var record in records) {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return record;
        }
    }

    /// <summary>A non-seekable server body that refuses synchronous writes, as Kestrel's does by default.</summary>
    private sealed class AsyncOnlyStream : Stream {
        private readonly MemoryStream _inner = new();

        public int AsynchronousWrites { get; private set; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public byte[] ToArray() {
            return _inner.ToArray();
        }

        public override void Write(byte[] buffer, int offset, int count) {
            throw new InvalidOperationException("Synchronous operations are disallowed.");
        }

        public override void Write(ReadOnlySpan<byte> buffer) {
            throw new InvalidOperationException("Synchronous operations are disallowed.");
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) {
            _inner.Write(buffer.Span);
            AsynchronousWrites++;
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) {
            return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override void Flush() {
            throw new InvalidOperationException("Synchronous operations are disallowed.");
        }

        public override Task FlushAsync(CancellationToken cancellationToken) {
            return Task.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count) {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin) {
            throw new NotSupportedException();
        }

        public override void SetLength(long value) {
            throw new NotSupportedException();
        }
    }
}

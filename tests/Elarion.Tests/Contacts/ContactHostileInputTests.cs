using System.Diagnostics;
using System.Text;
using AwesomeAssertions;
using Elarion.Contacts;
using Xunit;
using static Elarion.Tests.Contacts.VCardFiles;

namespace Elarion.Tests.Contacts;

public sealed class ContactHostileInputTests {
    // Generous: the contract is linear time, and these inputs take well under a second when it holds.
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task AnUploadOverTheLimit_IsOneTooLargeErrorAndReadsNoMoreThanTheLimit() {
        var source = new EndlessStream();
        var options = new ContactReadOptions { MaxBytes = 64 * 1024 };

        var result = await new ContactReader().ReadAsync(source, options, TestCancellation);

        result.Entries.Should().BeEmpty();
        var error = Assert.Single(result.FileErrors);
        error.Code.Should().Be(ContactErrorCodes.TooLarge);
        error.Limit.Should().Be(64 * 1024);
        source.BytesRead.Should().BeLessThanOrEqualTo(64 * 1024 + 16 * 1024);
    }

    [Fact]
    public async Task ASeekableUploadOverTheLimit_IsRefusedWithoutReadingIt() {
        var bytes = Encoding.UTF8.GetBytes(Card("FN:A"));

        var result = await ReadAsync(bytes, new ContactReadOptions { MaxBytes = bytes.Length - 1 });

        Assert.Single(result.FileErrors).Code.Should().Be(ContactErrorCodes.TooLarge);
    }

    [Fact]
    public async Task TwentyThousandContacts_KeepTheFirstTenThousandWithOneError() {
        var builder = new StringBuilder();
        for (var i = 0; i < 20_000; i++) builder.Append("BEGIN:VCARD\r\nFN:P\r\nEND:VCARD\r\n");

        var result = await ReadAsync(builder.ToString());

        result.Entries.Should().HaveCount(10_000);
        var error = Assert.Single(result.Errors);
        error.Code.Should().Be(ContactErrorCodes.TooManyContacts);
        error.Limit.Should().Be(10_000);
    }

    [Fact]
    public async Task AContactWithFiveThousandProperties_HasNoContactAndTheNextOneReads() {
        var builder = new StringBuilder("BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Many\r\n");
        for (var i = 0; i < 5_000; i++) builder.Append("X-P:").Append(i).Append("\r\n");
        builder.Append("END:VCARD\r\n").Append(Card("FN:Next"));

        var result = await ReadAsync(builder.ToString());

        result.Entries.Should().HaveCount(2);
        result.Entries[0].HasValue.Should().BeFalse();
        var error = Assert.Single(result.Entries[0].Errors);
        error.Code.Should().Be(ContactErrorCodes.TooManyProperties);
        error.Limit.Should().Be(1_000);
        result.Entries[0].Properties.Should().HaveCount(1_000);
        result.Entries[1].Value!.FormattedName.Should().Be("Next");
    }

    [Fact]
    public async Task AFiveMebibyteNote_IsDroppedWithALineTooLongError() {
        var text = Card("FN:A", "NOTE:" + new string('x', 5 * 1024 * 1024), "TEL:1");

        var entry = Assert.Single((await ReadAsync(text)).Entries);

        entry.Value!.Note.Should().BeNull();
        entry.Value.Phones.Should().ContainSingle();
        var error = Assert.Single(entry.Errors);
        error.Code.Should().Be(ContactErrorCodes.LineTooLong);
        error.Property.Should().Be("NOTE");
        error.Limit.Should().Be(ContactReadOptions.DefaultMaxLineBytes);
    }

    [Fact]
    public async Task ATooLongQuotedPrintableLine_StillConsumesItsSoftBreaks() {
        var builder = new StringBuilder("BEGIN:VCARD\r\nVERSION:2.1\r\nFN:A\r\nNOTE;ENCODING=QUOTED-PRINTABLE:");
        for (var i = 0; i < 2_000; i++) builder.Append(new string('a', 70)).Append("=\r\n");
        builder.Append("end\r\nTEL:1\r\nEND:VCARD\r\n");

        var entry = Assert.Single((await ReadAsync(builder.ToString(), new ContactReadOptions { MaxLineBytes = 10_000 })).Entries);

        Assert.Single(entry.Errors).Code.Should().Be(ContactErrorCodes.LineTooLong);
        entry.Value!.Phones.Should().ContainSingle();
    }

    [Fact]
    public async Task AnAgentNestedAThousandTimes_ReadsInLinearTimeAsAnAdditionalProperty() {
        var value = "FN:Inner";
        for (var i = 0; i < 1_000; i++) value = "BEGIN:VCARD\\nAGENT:" + value + "\\nEND:VCARD";
        var text = Card("FN:Outer", "AGENT:" + value);

        var watch = Stopwatch.StartNew();
        var result = await ReadAsync(text);

        watch.Elapsed.Should().BeLessThan(Bound);
        var contact = result.Contacts.Single();
        contact.FormattedName.Should().Be("Outer");
        contact.AdditionalProperties.Single().Name.Should().Be("AGENT");
    }

    [Fact]
    public async Task AMillionContinuationLines_ReadInLinearTime() {
        var builder = new StringBuilder("BEGIN:VCARD\r\nVERSION:3.0\r\nFN:A\r\nNOTE:x", 11 * 1024 * 1024);
        for (var i = 0; i < 1_000_000; i++) builder.Append("\r\n abcdefg");
        builder.Append("\r\nEND:VCARD\r\n");

        var watch = Stopwatch.StartNew();
        var entry = Assert.Single((await ReadAsync(builder.ToString())).Entries);

        watch.Elapsed.Should().BeLessThan(Bound);
        Assert.Single(entry.Errors).Code.Should().Be(ContactErrorCodes.LineTooLong);
        entry.Value!.FormattedName.Should().Be("A");
    }

    [Fact]
    public async Task AMillionSoftBreaks_ReadInLinearTime() {
        var builder = new StringBuilder("BEGIN:VCARD\r\nVERSION:2.1\r\nFN:A\r\nNOTE;ENCODING=QUOTED-PRINTABLE:x", 4 * 1024 * 1024);
        for (var i = 0; i < 1_000_000; i++) builder.Append("=\r\n");
        builder.Append("y\r\nEND:VCARD\r\n");

        var watch = Stopwatch.StartNew();
        var contact = (await ReadAsync(builder.ToString())).Contacts.Single();

        watch.Elapsed.Should().BeLessThan(Bound);
        contact.Note.Should().Be("xy");
    }

    [Fact]
    public async Task RandomBytesAndMutations_NeverThrow() {
        var random = new Random(20261127);
        var corpus = Encoding.UTF8.GetBytes(Apple);
        for (var round = 0; round < 300; round++) {
            byte[] input;
            if (round % 3 == 0) {
                input = new byte[random.Next(0, 4096)];
                random.NextBytes(input);
            }
            else {
                input = (byte[])corpus.Clone();
                for (var m = 0; m < 1 + round % 20; m++) input[random.Next(input.Length)] = (byte)random.Next(256);
            }

            var read = async () => await ReadAsync(input);
            await read.Should().NotThrowAsync();
        }
    }

    [Fact]
    public async Task Cancellation_Throws() {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Apple));

        var read = async () => await new ContactReader().ReadAsync(stream, cancellationToken: cancelled.Token);

        await read.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>A non-seekable stream of endless vCard text that counts what was read from it.</summary>
    private sealed class EndlessStream : Stream {
        private static readonly byte[] Pattern = Encoding.ASCII.GetBytes("BEGIN:VCARD\r\nFN:Endless\r\nEND:VCARD\r\n");
        private long _position;

        public long BytesRead => _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer) {
            for (var i = 0; i < buffer.Length; i++) buffer[i] = Pattern[(int)((_position + i) % Pattern.Length)];
            _position += buffer.Length;
            return buffer.Length;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override void Flush() {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

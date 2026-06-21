using AwesomeAssertions;
using Elarion.Blobs;
using Elarion.Blobs.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Elarion.Tests.Blobs;

/// <summary>
/// Round-trip integration tests for <see cref="PostgreSqlBlobStore{TDbContext}"/> against a real
/// PostgreSQL instance. Each test uses a unique container name so they stay isolated on the shared
/// database, and skips when Docker is unavailable.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgreSqlBlobStoreIntegrationTests(PostgreSqlBlobStoreFixture fixture)
    : IClassFixture<PostgreSqlBlobStoreFixture> {
    [Fact]
    public async Task SaveAsync_FromStream_RoundTripsContentAndMetadata() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var context = fixture.CreateContext();
        var store = CreateStore(context);
        var request = NewRequest();
        var content = Bytes(2048);

        var blobRef = await store.SaveAsync(request, new MemoryStream(content), ct);

        (await store.ExistsAsync(blobRef, ct)).Should().BeTrue();
        await using var download = await store.OpenReadAsync(blobRef, ct);
        download.Should().NotBeNull();
        download!.Metadata.Container.Should().Be(request.Container);
        download.Metadata.Name.Should().Be(request.Name);
        download.Metadata.ContentType.Should().Be(request.ContentType);
        download.Metadata.Size.Should().Be(content.Length);
        (await ReadAllAsync(download, ct)).Should().Equal(content);
    }

    [Fact]
    public async Task SaveAsync_Overwrite_ReplacesContentAndKeepsBlobId() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var context = fixture.CreateContext();
        var store = CreateStore(context);
        var request = NewRequest();

        var first = await store.SaveAsync(request, new MemoryStream(Bytes(512)), ct);
        var updated = Bytes(4096);
        var second = await store.SaveAsync(request, new MemoryStream(updated), ct);

        second.Value.Should().Be(first.Value);
        (await store.ReadAllBytesAsync(second, ct)).Should().Equal(updated);
        (await store.GetMetadataAsync(second, ct))!.Size.Should().Be(updated.Length);
    }

    [Fact]
    public async Task SaveFromFileAsync_RoundTrips() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var context = fixture.CreateContext();
        var store = CreateStore(context);
        var request = NewRequest();
        var content = Bytes(3000);
        var path = Path.GetTempFileName();
        try {
            await File.WriteAllBytesAsync(path, content, ct);

            var blobRef = await store.SaveFromFileAsync(request, path, ct);

            (await store.ReadAllBytesAsync(blobRef, ct)).Should().Equal(content);
            (await store.GetMetadataAsync(blobRef, ct))!.Size.Should().Be(content.Length);
        }
        finally {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SaveAsync_FromNonSeekableStream_MeasuresActualSize() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var context = fixture.CreateContext();
        var store = CreateStore(context);
        var request = NewRequest();
        var content = Bytes(5000);

        var blobRef = await store.SaveAsync(request, new NonSeekableStream(new MemoryStream(content)), ct);

        (await store.GetMetadataAsync(blobRef, ct))!.Size.Should().Be(content.Length);
        (await store.ReadAllBytesAsync(blobRef, ct)).Should().Equal(content);
    }

    [Fact]
    public async Task SaveAsync_FromNonSeekableStream_WithContentLengthHint_StreamsAndRoundTrips() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var context = fixture.CreateContext();
        var store = CreateStore(context);
        var content = Bytes(5000);
        var request = NewRequest() with { ContentLength = content.Length };

        var blobRef = await store.SaveAsync(request, new NonSeekableStream(new MemoryStream(content)), ct);

        (await store.GetMetadataAsync(blobRef, ct))!.Size.Should().Be(content.Length);
        (await store.ReadAllBytesAsync(blobRef, ct)).Should().Equal(content);
    }

    [Fact]
    public async Task SaveAsync_FromNonSeekableStream_WithTooSmallHint_FailsAndPersistsNothing() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var context = fixture.CreateContext();
        var store = CreateStore(context);
        var content = Bytes(5000);
        var request = NewRequest() with { ContentLength = content.Length - 100 };

        var act = async () =>
            await store.SaveAsync(request, new NonSeekableStream(new MemoryStream(content)), ct);

        await act.Should().ThrowAsync<Exception>();
        await using var freshContext = fixture.CreateContext();
        (await BlobExistsByName(freshContext, request, ct)).Should().BeFalse();
    }

    [Fact]
    public async Task SaveAsync_FromNonSeekableStream_WithTooLargeHint_FailsAndPersistsNothing() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var context = fixture.CreateContext();
        var store = CreateStore(context);
        var content = Bytes(5000);
        var request = NewRequest() with { ContentLength = content.Length + 100 };

        var act = async () =>
            await store.SaveAsync(request, new NonSeekableStream(new MemoryStream(content)), ct);

        await act.Should().ThrowAsync<Exception>();
        await using var freshContext = fixture.CreateContext();
        (await BlobExistsByName(freshContext, request, ct)).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_RemovesBlobAndContent() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        await using var context = fixture.CreateContext();
        var store = CreateStore(context);
        var request = NewRequest();
        var blobRef = await store.SaveAsync(request, new MemoryStream(Bytes(256)), ct);

        (await store.DeleteAsync(blobRef, ct)).Should().BeTrue();
        (await store.ExistsAsync(blobRef, ct)).Should().BeFalse();
        (await store.OpenReadAsync(blobRef, ct)).Should().BeNull();
        (await store.DeleteAsync(blobRef, ct)).Should().BeFalse();
    }

    private static PostgreSqlBlobStore<IntegrationBlobDbContext> CreateStore(IntegrationBlobDbContext context) =>
        new(context, NullLogger<PostgreSqlBlobStore<IntegrationBlobDbContext>>.Instance, TimeProvider.System);

    private static Task<bool> BlobExistsByName(
        IntegrationBlobDbContext context,
        BlobUploadRequest request,
        CancellationToken cancellationToken) =>
        context.Set<StoredBlob>()
            .AsNoTracking()
            .AnyAsync(blob => blob.Container == request.Container && blob.Name == request.Name, cancellationToken);

    private static BlobUploadRequest NewRequest() =>
        new() {
            Container = $"c-{Guid.NewGuid():N}",
            Name = "blob.bin",
            ContentType = "application/octet-stream"
        };

    private static byte[] Bytes(int count) {
        var bytes = new byte[count];
        for (var i = 0; i < count; i++) {
            bytes[i] = (byte)(i % 251);
        }

        return bytes;
    }

    private static async Task<byte[]> ReadAllAsync(BlobDownload download, CancellationToken cancellationToken) {
        using var memory = new MemoryStream();
        await download.Content.CopyToAsync(memory, cancellationToken);
        return memory.ToArray();
    }

    /// <summary>A forward-only, non-seekable wrapper so the store exercises its buffer-to-measure path.</summary>
    private sealed class NonSeekableStream(Stream inner) : Stream {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

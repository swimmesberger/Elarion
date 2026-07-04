using AwesomeAssertions;
using Elarion.Blobs;
using Xunit;

namespace Elarion.Tests.Blobs;

public sealed class BlobStoreExtensionsTests {
    private static readonly BlobUploadRequest Request = new() {
        Container = "documents",
        Name = "report.bin",
        ContentType = "application/octet-stream"
    };

    [Fact]
    public async Task SaveAsync_FromBytes_ForwardsReadableStreamAndFillsContentLength() {
        var store = new FakeBlobStore();
        var content = new byte[] { 1, 2, 3, 4, 5 };

        await store.SaveAsync(Request, content, TestContext.Current.CancellationToken);

        store.LastContentStreamWasReadable.Should().BeTrue();
        store.LastContent.Should().Equal(content);
        store.LastRequest.Should().NotBeNull();
        store.LastRequest!.ContentLength.Should().Be(content.Length);
        store.LastRequest.Container.Should().Be(Request.Container);
        store.LastRequest.Name.Should().Be(Request.Name);
        store.LastRequest.ContentType.Should().Be(Request.ContentType);
    }

    [Fact]
    public async Task SaveAsync_FromBytes_KeepsExplicitContentLength() {
        var store = new FakeBlobStore();
        var request = Request with { ContentLength = 99 };

        await store.SaveAsync(request, new byte[] { 1, 2, 3 }, TestContext.Current.CancellationToken);

        store.LastRequest!.ContentLength.Should().Be(99);
    }

    [Fact]
    public async Task SaveFromFileAsync_ReadsFileAndFillsContentLength() {
        var store = new FakeBlobStore();
        var content = new byte[] { 9, 8, 7, 6 };
        var path = Path.GetTempFileName();
        try {
            await File.WriteAllBytesAsync(path, content, TestContext.Current.CancellationToken);

            await store.SaveFromFileAsync(Request, path, TestContext.Current.CancellationToken);

            store.LastContent.Should().Equal(content);
            store.LastRequest!.ContentLength.Should().Be(content.Length);
        }
        finally {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DownloadContentAsync_ReturnsBufferedMetadataAndData() {
        var store = new FakeBlobStore();
        var blobRef = new BlobRef { Value = "documents/report.bin" };
        var data = new byte[] { 10, 20, 30 };
        store.Seed(blobRef, Metadata(blobRef, data.Length), data);

        var content = await store.DownloadContentAsync(blobRef, TestContext.Current.CancellationToken);

        content.Should().NotBeNull();
        content!.Id.Should().Be(blobRef.Value);
        content.Container.Should().Be("documents");
        content.ContentType.Should().Be("application/octet-stream");
        content.Size.Should().Be(data.Length);
        content.Data.Should().Equal(data);
    }

    [Fact]
    public async Task DownloadContentAsync_ReturnsNull_WhenMissing() {
        var store = new FakeBlobStore();

        var content = await store.DownloadContentAsync(
            new BlobRef { Value = "missing" },
            TestContext.Current.CancellationToken);

        content.Should().BeNull();
    }

    [Fact]
    public async Task ReadAllBytesAsync_ReturnsContent() {
        var store = new FakeBlobStore();
        var blobRef = new BlobRef { Value = "documents/report.bin" };
        var data = new byte[] { 4, 5, 6, 7 };
        store.Seed(blobRef, Metadata(blobRef, data.Length), data);

        var bytes = await store.ReadAllBytesAsync(blobRef, TestContext.Current.CancellationToken);

        bytes.Should().Equal(data);
    }

    [Fact]
    public async Task ReadAllBytesAsync_ReturnsNull_WhenMissing() {
        var store = new FakeBlobStore();

        var bytes = await store.ReadAllBytesAsync(
            new BlobRef { Value = "missing" },
            TestContext.Current.CancellationToken);

        bytes.Should().BeNull();
    }

    [Fact]
    public async Task DownloadToAsync_CopiesContent_AndReturnsTrue() {
        var store = new FakeBlobStore();
        var blobRef = new BlobRef { Value = "documents/report.bin" };
        var data = new byte[] { 42, 43, 44 };
        store.Seed(blobRef, Metadata(blobRef, data.Length), data);

        using var destination = new MemoryStream();
        var copied = await store.DownloadToAsync(blobRef, destination, TestContext.Current.CancellationToken);

        copied.Should().BeTrue();
        destination.ToArray().Should().Equal(data);
    }

    [Fact]
    public async Task DownloadToAsync_ReturnsFalse_WhenMissing() {
        var store = new FakeBlobStore();
        using var destination = new MemoryStream();

        var copied = await store.DownloadToAsync(
            new BlobRef { Value = "missing" },
            destination,
            TestContext.Current.CancellationToken);

        copied.Should().BeFalse();
        destination.Length.Should().Be(0);
    }

    [Fact]
    public async Task ListAllAsync_WalksEveryPageInOrder() {
        var store = new FakeBlobStore();
        foreach (var name in new[] { "f1.bin", "f2.bin", "f3.bin", "f4.bin", "f5.bin" }) {
            var blobRef = new BlobRef { Value = name };
            store.Seed(blobRef, Metadata(blobRef, size: 1) with { Name = name }, [1]);
        }

        var names = new List<string>();
        await foreach (var blob in store.ListAllAsync(
            "documents", pageSize: 2, cancellationToken: TestContext.Current.CancellationToken)) {
            names.Add(blob.Name);
        }

        names.Should().Equal("f1.bin", "f2.bin", "f3.bin", "f4.bin", "f5.bin");
    }

    private static BlobMetadata Metadata(BlobRef blobRef, long size) =>
        new() {
            Id = blobRef.Value,
            Container = "documents",
            Name = "report.bin",
            ContentType = "application/octet-stream",
            Size = size,
            CreatedAt = DateTimeOffset.UnixEpoch,
            State = BlobLifecycleState.Committed
        };
}

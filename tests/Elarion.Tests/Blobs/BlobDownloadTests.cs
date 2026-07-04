using AwesomeAssertions;
using Elarion.Blobs;
using Xunit;

namespace Elarion.Tests.Blobs;

public sealed class BlobDownloadTests {
    [Fact]
    public async Task DisposeAsync_DisposesContentThenOwnedResource() {
        var order = new List<string>();
        var content = new TrackingStream(() => order.Add("content"));
        var owned = new TrackingDisposable(() => order.Add("owned"));
        var download = new BlobDownload(Metadata(), content, owned);

        await download.DisposeAsync();

        order.Should().Equal("content", "owned");
    }

    [Fact]
    public async Task DisposeAsync_WithoutOwnedResource_DisposesContent() {
        var content = new TrackingStream(() => { });
        var download = new BlobDownload(Metadata(), content);

        await download.DisposeAsync();

        content.Disposed.Should().BeTrue();
    }

    [Fact]
    public void Constructor_Rejects_NullArguments() {
        var act = () => new BlobDownload(Metadata(), null!);

        act.Should().Throw<ArgumentNullException>();
    }

    private static BlobMetadata Metadata() =>
        new() {
            Id = "id",
            Container = "container",
            Name = "name",
            ContentType = "application/octet-stream",
            Size = 0,
            CreatedAt = DateTimeOffset.UnixEpoch,
            State = BlobLifecycleState.Committed
        };

    private sealed class TrackingStream(Action onDispose) : MemoryStream {
        public bool Disposed { get; private set; }

        public override ValueTask DisposeAsync() {
            Disposed = true;
            onDispose();
            return base.DisposeAsync();
        }
    }

    private sealed class TrackingDisposable(Action onDispose) : IAsyncDisposable {
        public ValueTask DisposeAsync() {
            onDispose();
            return ValueTask.CompletedTask;
        }
    }
}

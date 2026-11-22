using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Identity;
using Elarion.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.Blobs;

/// <summary>
/// The handler-side reader of staged uploads: it answers only a pending upload of the current user in an upload
/// container, refuses everything else with one indistinguishable error, applies the handler's constraints, and keeps
/// or discards an upload on request.
/// </summary>
public sealed class UploadedFilesTests {
    private const string Owner = "user-1";
    private static readonly byte[] Csv = "date;amount\n2026-11-01;12.50\n"u8.ToArray();

    [Fact]
    public async Task Open_PendingUploadOfCaller_StreamsContentWithItsDescription() {
        var ct = TestContext.Current.CancellationToken;
        var store = new UploadStore();
        var file = store.Seed("blob-1", Csv, contentType: "text/csv", sha256: new string('a', 64));
        await using var scope = CreateScope(store);

        var opened = await scope.Uploads.OpenAsync(file, cancellationToken: ct);

        opened.IsSuccess.Should().BeTrue();
        await using var content = opened.Value;
        using var buffer = new MemoryStream();
        await content.Content.CopyToAsync(buffer, ct);
        buffer.ToArray().Should().Equal(Csv);
        content.ContentType.Should().Be("text/csv");
        content.FileName.Should().Be("statement.csv");
        content.Length.Should().Be(Csv.Length);
        content.ContentSha256.Should().Be(new string('a', 64));
    }

    [Fact]
    public async Task Open_SameUploadTwice_ReadsItAgain_BecauseItStaysPending() {
        // A wizard (analyze, preview, commit) passes one reference to every step.
        var ct = TestContext.Current.CancellationToken;
        var store = new UploadStore();
        var file = store.Seed("blob-1", Csv);
        await using var scope = CreateScope(store);

        for (var step = 0; step < 3; step++) {
            var opened = await scope.Uploads.OpenAsync(file, cancellationToken: ct);
            opened.IsSuccess.Should().BeTrue();
            await opened.Value.DisposeAsync();
        }

        store.StateOf("blob-1").Should().Be(BlobLifecycleState.Pending);
        store.OpenStreams.Should().Be(0);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("other-owner")]
    [InlineData("no-owner")]
    [InlineData("committed")]
    [InlineData("other-container")]
    [InlineData("anonymous-caller")]
    [InlineData("empty-id")]
    [InlineData("whitespace-id")]
    [InlineData("control-character-id")]
    [InlineData("overlong-id")]
    public async Task Open_AnythingButAPendingUploadOfCaller_AnswersTheSameNotFound(string scenario) {
        var ct = TestContext.Current.CancellationToken;
        var store = new UploadStore();
        var file = scenario switch {
            "missing" => new UploadedFile { Id = "never-uploaded" },
            "other-owner" => store.Seed("blob-1", Csv, owner: "user-2"),
            "no-owner" => store.Seed("blob-1", Csv, owner: null),
            "committed" => store.Seed("blob-1", Csv, state: BlobLifecycleState.Committed),
            "other-container" => store.Seed("blob-1", Csv, container: "documents"),
            "anonymous-caller" => store.Seed("blob-1", Csv),
            "empty-id" => new UploadedFile { Id = "" },
            "whitespace-id" => new UploadedFile { Id = "  " },
            "control-character-id" => new UploadedFile { Id = "blob\n1" },
            "overlong-id" => new UploadedFile { Id = new string('x', 1025) },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        await using var scope = CreateScope(store, authenticated: scenario != "anonymous-caller");

        var opened = await scope.Uploads.OpenAsync(file, cancellationToken: ct);
        var missing = await scope.Uploads.OpenAsync(new UploadedFile { Id = "never-uploaded" }, cancellationToken: ct);

        opened.IsSuccess.Should().BeFalse();
        opened.Error.Code.Should().Be(ErrorCodes.UploadNotFound);
        opened.Error.Kind.Should().Be(ErrorKind.Validation);
        // Indistinguishable from an id that never existed, so ids cannot be probed.
        opened.Error.Should().Be(missing.Error);
        store.OpenStreams.Should().Be(0);
    }

    [Fact]
    public async Task Open_LargerThanMaxBytes_AnswersTooLarge() {
        var ct = TestContext.Current.CancellationToken;
        var store = new UploadStore();
        var file = store.Seed("blob-1", Csv);
        await using var scope = CreateScope(store);

        var opened = await scope.Uploads.OpenAsync(
            file, new UploadConstraints { MaxBytes = Csv.Length - 1 }, ct);
        var exact = await scope.Uploads.OpenAsync(file, new UploadConstraints { MaxBytes = Csv.Length }, ct);

        opened.Error.Code.Should().Be(ErrorCodes.UploadTooLarge);
        opened.Error.Kind.Should().Be(ErrorKind.Validation);
        exact.IsSuccess.Should().BeTrue();
        await exact.Value.DisposeAsync();
        store.OpenStreams.Should().Be(0);
    }

    [Fact]
    public async Task Open_ContentTypeNotAccepted_AnswersContentType() {
        var ct = TestContext.Current.CancellationToken;
        var store = new UploadStore();
        var file = store.Seed("blob-1", Csv, contentType: "application/pdf");
        await using var scope = CreateScope(store);

        var opened = await scope.Uploads.OpenAsync(
            file, new UploadConstraints { ContentTypes = ["text/csv", "application/xml"] }, ct);

        opened.Error.Code.Should().Be(ErrorCodes.UploadContentType);
        opened.Error.Kind.Should().Be(ErrorKind.Validation);
        opened.Error.Message.Should().Contain("application/pdf").And.Contain("text/csv");
    }

    [Fact]
    public async Task Open_ContentTypeComparison_IgnoresCaseAndParameters() {
        var ct = TestContext.Current.CancellationToken;
        var store = new UploadStore();
        var file = store.Seed("blob-1", Csv, contentType: "Text/CSV; charset=utf-8");
        await using var scope = CreateScope(store);

        var opened = await scope.Uploads.OpenAsync(
            file, new UploadConstraints { ContentTypes = ["text/csv; charset=iso-8859-1"] }, ct);

        opened.IsSuccess.Should().BeTrue();
        await opened.Value.DisposeAsync();
    }

    [Fact]
    public async Task Open_ConfiguredContainer_IsAnUploadContainer() {
        var ct = TestContext.Current.CancellationToken;
        var store = new UploadStore();
        var file = store.Seed("blob-1", Csv, container: "imports");
        await using var scope = CreateScope(store, configure: o => o.Containers = ["uploads", "imports"]);

        var opened = await scope.Uploads.OpenAsync(file, cancellationToken: ct);

        opened.IsSuccess.Should().BeTrue();
        await opened.Value.DisposeAsync();
    }

    [Fact]
    public async Task Commit_PendingUploadOfCaller_KeepsItAndStopsTreatingItAsAnUpload() {
        var ct = TestContext.Current.CancellationToken;
        var store = new UploadStore();
        var file = store.Seed("blob-1", Csv, sha256: new string('b', 64));
        await using var scope = CreateScope(store);

        var committed = await scope.Uploads.CommitAsync(file, new UploadConstraints { ContentTypes = ["text/csv"] }, ct);

        committed.IsSuccess.Should().BeTrue();
        committed.Value.Should().Be(new CommittedUpload {
            BlobId = "blob-1",
            FileName = "statement.csv",
            ContentType = "text/csv",
            Length = Csv.Length,
            ContentSha256 = new string('b', 64)
        });
        store.StateOf("blob-1").Should().Be(BlobLifecycleState.Committed);
        // A kept blob belongs to its record: the reference no longer opens, keeps or deletes it.
        (await scope.Uploads.OpenAsync(file, cancellationToken: ct)).Error.Code.Should().Be(ErrorCodes.UploadNotFound);
        (await scope.Uploads.CommitAsync(file, cancellationToken: ct)).Error.Code.Should().Be(ErrorCodes.UploadNotFound);
        (await scope.Uploads.DeleteAsync(file, ct)).Should().BeFalse();
        store.StateOf("blob-1").Should().Be(BlobLifecycleState.Committed);
    }

    [Fact]
    public async Task Commit_UploadOfAnotherUser_IsRefusedAndStaysPending() {
        var ct = TestContext.Current.CancellationToken;
        var store = new UploadStore();
        var file = store.Seed("blob-1", Csv, owner: "user-2");
        await using var scope = CreateScope(store);

        var committed = await scope.Uploads.CommitAsync(file, cancellationToken: ct);

        committed.Error.Code.Should().Be(ErrorCodes.UploadNotFound);
        store.StateOf("blob-1").Should().Be(BlobLifecycleState.Pending);
    }

    [Fact]
    public async Task Commit_ViolatedConstraint_IsRefusedAndStaysPending() {
        var ct = TestContext.Current.CancellationToken;
        var store = new UploadStore();
        var file = store.Seed("blob-1", Csv);
        await using var scope = CreateScope(store);

        var committed = await scope.Uploads.CommitAsync(file, new UploadConstraints { MaxBytes = 1 }, ct);

        committed.Error.Code.Should().Be(ErrorCodes.UploadTooLarge);
        store.StateOf("blob-1").Should().Be(BlobLifecycleState.Pending);
    }

    [Fact]
    public async Task Commit_WithoutBlobLifecycle_FailsLoudly() {
        var ct = TestContext.Current.CancellationToken;
        var store = new UploadStore();
        var file = store.Seed("blob-1", Csv);
        await using var scope = CreateScope(store, registerLifecycle: false);

        var act = async () => await scope.Uploads.CommitAsync(file, cancellationToken: ct);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*IBlobLifecycle*");
    }

    [Fact]
    public async Task Delete_OwnPendingUpload_DeletesItOnce() {
        var ct = TestContext.Current.CancellationToken;
        var store = new UploadStore();
        var file = store.Seed("blob-1", Csv);
        await using var scope = CreateScope(store);

        (await scope.Uploads.DeleteAsync(file, ct)).Should().BeTrue();
        (await scope.Uploads.DeleteAsync(file, ct)).Should().BeFalse();
        store.Contains("blob-1").Should().BeFalse();
    }

    [Fact]
    public async Task Delete_UploadOfAnotherUser_LeavesIt() {
        var ct = TestContext.Current.CancellationToken;
        var store = new UploadStore();
        var file = store.Seed("blob-1", Csv, owner: "user-2");
        await using var scope = CreateScope(store);

        (await scope.Uploads.DeleteAsync(file, ct)).Should().BeFalse();
        store.Contains("blob-1").Should().BeTrue();
    }

    private static TestScope CreateScope(
        UploadStore store,
        bool authenticated = true,
        bool registerLifecycle = true,
        Action<UploadedFilesOptions>? configure = null) {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICurrentUser>(new FakeCurrentUser(authenticated ? Owner : "", authenticated));
        services.AddSingleton<IBlobStore>(store);
        if (registerLifecycle) services.AddSingleton<IBlobLifecycle>(store);

        services.AddElarionUploadedFiles(configure);
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var scope = provider.CreateAsyncScope();
        return new TestScope(provider, scope, scope.ServiceProvider.GetRequiredService<IUploadedFiles>());
    }

    private sealed class TestScope(ServiceProvider provider, AsyncServiceScope scope, IUploadedFiles uploads)
        : IAsyncDisposable {
        public IUploadedFiles Uploads { get; } = uploads;

        public async ValueTask DisposeAsync() {
            await scope.DisposeAsync();
            await provider.DisposeAsync();
        }
    }

    /// <summary>
    /// An in-memory blob store with the pending/committed lifecycle, counting the content streams still open so a
    /// test can prove a refused read released the one it opened.
    /// </summary>
    private sealed class UploadStore : IBlobStore, IBlobLifecycle {
        private readonly Dictionary<string, (BlobMetadata Metadata, byte[] Data)> _blobs = new(StringComparer.Ordinal);
        private int _openStreams;

        public int OpenStreams => Volatile.Read(ref _openStreams);

        public UploadedFile Seed(
            string id,
            byte[] data,
            string? owner = Owner,
            BlobLifecycleState state = BlobLifecycleState.Pending,
            string container = "uploads",
            string contentType = "text/csv",
            string? sha256 = null) {
            _blobs[id] = (new BlobMetadata {
                Id = id,
                Container = container,
                // The shape the upload endpoints store ("{owner}/{guid}/{fileName}"), always under the caller's
                // prefix: a refusal then proves ownership is read from OwnerId, never parsed from the name.
                Name = $"{Owner}/{Guid.CreateVersion7():N}/statement.csv",
                ContentType = contentType,
                Size = data.Length,
                CreatedAt = DateTimeOffset.UnixEpoch,
                State = state,
                OwnerId = owner,
                ContentSha256 = sha256
            }, data);
            return new UploadedFile { Id = id };
        }

        public BlobLifecycleState StateOf(string id) {
            return _blobs[id].Metadata.State;
        }

        public bool Contains(string id) {
            return _blobs.ContainsKey(id);
        }

        public Task<BlobDownload?> OpenReadAsync(BlobRef blobRef, CancellationToken cancellationToken) {
            if (!_blobs.TryGetValue(blobRef.Value, out var entry)) return Task.FromResult<BlobDownload?>(null);

            Interlocked.Increment(ref _openStreams);
            var download = new BlobDownload(entry.Metadata, new MemoryStream(entry.Data, false), new Release(this));
            return Task.FromResult<BlobDownload?>(download);
        }

        public Task<BlobMetadata?> GetMetadataAsync(BlobRef blobRef, CancellationToken cancellationToken) {
            return Task.FromResult(_blobs.TryGetValue(blobRef.Value, out var entry) ? entry.Metadata : null);
        }

        public Task<bool> DeleteAsync(BlobRef blobRef, CancellationToken cancellationToken) {
            return Task.FromResult(_blobs.Remove(blobRef.Value));
        }

        public Task<bool> ExistsAsync(BlobRef blobRef, CancellationToken cancellationToken) {
            return Task.FromResult(_blobs.ContainsKey(blobRef.Value));
        }

        public Task<bool> CommitAsync(BlobRef blobRef, CancellationToken cancellationToken) {
            if (!_blobs.TryGetValue(blobRef.Value, out var entry)) return Task.FromResult(false);

            _blobs[blobRef.Value] = (entry.Metadata with { State = BlobLifecycleState.Committed }, entry.Data);
            return Task.FromResult(true);
        }

        public Task<int> DeleteExpiredPendingAsync(
            DateTimeOffset olderThanUtc, int batchSize, CancellationToken cancellationToken) {
            throw new NotSupportedException();
        }

        public Task<BlobRef> SaveAsync(BlobUploadRequest request, Stream content, CancellationToken cancellationToken) {
            throw new NotSupportedException();
        }

        public Task<BlobListing> ListAsync(BlobListRequest request, CancellationToken cancellationToken) {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<string>> ListContainersAsync(CancellationToken cancellationToken) {
            throw new NotSupportedException();
        }

        private sealed class Release(UploadStore store) : IAsyncDisposable {
            private int _released;

            public ValueTask DisposeAsync() {
                if (Interlocked.Exchange(ref _released, 1) == 0) Interlocked.Decrement(ref store._openStreams);

                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class FakeCurrentUser(string userId, bool isAuthenticated) : ICurrentUser {
        public string UserId { get; } = userId;

        public string? Email => null;

        public IReadOnlyList<string> Roles => [];

        public bool IsAuthenticated { get; } = isAuthenticated;

        public bool IsInRole(string role) {
            return false;
        }
    }
}

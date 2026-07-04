using System.Net;
using System.Net.Http.Headers;
using AwesomeAssertions;
using Elarion.Abstractions.Identity;
using Elarion.Blobs;
using Elarion.Blobs.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Elarion.Tests.Blobs;

/// <summary>
/// End-to-end tests for the direct blob-upload endpoints over a real Kestrel host: multipart and raw
/// uploads produce a pending, owner-scoped blob and return its reference; auth, content-type, and size
/// rules are enforced; cancel is owner-scoped.
/// </summary>
public sealed class BlobUploadEndpointsTests {
    [Fact]
    public async Task UploadMultipart_StoresPendingOwnerScopedBlob_AndReturnsId() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct);

        using var content = new MultipartFormDataContent();
        var filePart = new ByteArrayContent([1, 2, 3, 4, 5]);
        filePart.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(filePart, "file", "doc.pdf");

        var response = await host.Client.PostAsync("/_elarion/blobs", content, ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var id = await response.Content.ReadAsStringAsync(ct);
        id.Should().NotBeNullOrWhiteSpace();

        var request = host.Store.LastRequest!;
        request.Container.Should().Be("uploads");
        request.Name.Should().StartWith("user-1/").And.EndWith("doc.pdf");
        request.ContentType.Should().Be("application/pdf");
        request.InitialState.Should().Be(BlobLifecycleState.Pending);
        request.ExpiresAt.Should().NotBeNull();
        host.Store.SavedContent(id).Should().Equal([1, 2, 3, 4, 5]);
    }

    [Fact]
    public async Task UploadRaw_StoresPendingBlob() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct);

        var content = new ByteArrayContent([9, 8, 7]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var request = new HttpRequestMessage(HttpMethod.Post, "/_elarion/blobs?fileName=raw.bin") {
            Content = content
        };

        var response = await host.Client.SendAsync(request, ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.Store.LastRequest!.Name.Should().StartWith("user-1/").And.EndWith("raw.bin");
        host.Store.LastRequest!.InitialState.Should().Be(BlobLifecycleState.Pending);
    }

    [Fact]
    public async Task Upload_Unauthenticated_Returns401() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct, user: new FakeCurrentUser("user-1", isAuthenticated: false));

        var content = new ByteArrayContent([1]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var response = await host.Client.PostAsync("/_elarion/blobs", content, ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        host.Store.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task Upload_DisallowedContentType_Returns400() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct, configure: options =>
            options.AllowedContentTypes = ["application/pdf"]);

        var content = new ByteArrayContent([1]);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        var response = await host.Client.PostAsync("/_elarion/blobs", content, ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        host.Store.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task Upload_OversizeMultipart_Returns413() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct, configure: options => options.MaxContentLength = 4);

        using var content = new MultipartFormDataContent();
        var filePart = new ByteArrayContent([1, 2, 3, 4, 5, 6]);
        filePart.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(filePart, "file", "big.bin");

        var response = await host.Client.PostAsync("/_elarion/blobs", content, ct);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        host.Store.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task Upload_OversizeRaw_WithContentLength_Returns413() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct, configure: options => options.MaxContentLength = 4);

        var content = new ByteArrayContent([1, 2, 3, 4, 5, 6]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var response = await host.Client.PostAsync("/_elarion/blobs", content, ct);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        host.Store.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task Upload_OversizeRaw_Chunked_Returns413() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct, configure: options => options.MaxContentLength = 4);

        var content = new StreamContent(new MemoryStream(new byte[16]));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var request = new HttpRequestMessage(HttpMethod.Post, "/_elarion/blobs") { Content = content };
        request.Headers.TransferEncodingChunked = true;

        var response = await host.Client.SendAsync(request, ct);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task Cancel_OwnersPendingBlob_Returns204AndDeletes() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct);

        var upload = new ByteArrayContent([1, 2, 3]);
        upload.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var id = await (await host.Client.PostAsync("/_elarion/blobs?fileName=a.bin", upload, ct))
            .Content.ReadAsStringAsync(ct);

        var response = await host.Client.DeleteAsync($"/_elarion/blobs/{id}", ct);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        host.Store.Deleted.Should().Contain(id);
    }

    [Fact]
    public async Task Cancel_SeparatorForgingUser_CannotCancelPrefixMatchedBlob_Returns404() {
        // Regression: ownership is compared against the recorded owner id exactly, not by a "starts with
        // {ownerId}/" test over the storage name. A user "a" must not be able to cancel a blob owned by
        // "a/b" just because the name "a/b/…" begins with "a/".
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct, user: new FakeCurrentUser("a", isAuthenticated: true));
        host.Store.Seed(new BlobMetadata {
            Id = "victim",
            Container = "uploads",
            Name = "a/b/abc/secret.bin",
            ContentType = "application/octet-stream",
            Size = 3,
            CreatedAt = DateTimeOffset.UnixEpoch,
            State = BlobLifecycleState.Pending,
            OwnerId = "a/b"
        });

        var response = await host.Client.DeleteAsync("/_elarion/blobs/victim", ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        host.Store.Deleted.Should().NotContain("victim");
    }

    [Fact]
    public async Task Cancel_BlobWithNoOwner_Returns404() {
        // Fail closed: an unowned blob is not cancellable by anyone.
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct);
        host.Store.Seed(new BlobMetadata {
            Id = "unowned",
            Container = "uploads",
            Name = "user-1/abc/file.bin",
            ContentType = "application/octet-stream",
            Size = 1,
            CreatedAt = DateTimeOffset.UnixEpoch,
            State = BlobLifecycleState.Pending,
            OwnerId = null
        });

        var response = await host.Client.DeleteAsync("/_elarion/blobs/unowned", ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        host.Store.Deleted.Should().NotContain("unowned");
    }

    [Fact]
    public async Task Upload_RecordsOwnerIdInMetadata() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct);

        var content = new ByteArrayContent([1, 2, 3]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        await host.Client.PostAsync("/_elarion/blobs?fileName=a.bin", content, ct);

        host.Store.LastRequest!.OwnerId.Should().Be("user-1");
    }

    [Fact]
    public async Task Cancel_OtherUsersBlob_Returns404() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct);
        host.Store.Seed(new BlobMetadata {
            Id = "blob-x",
            Container = "uploads",
            Name = "user-2/abc/secret.bin",
            ContentType = "application/octet-stream",
            Size = 3,
            CreatedAt = DateTimeOffset.UnixEpoch,
            State = BlobLifecycleState.Pending
        });

        var response = await host.Client.DeleteAsync("/_elarion/blobs/blob-x", ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        host.Store.Deleted.Should().NotContain("blob-x");
    }

    private static async Task<TestHost> StartAsync(
        CancellationToken cancellationToken,
        Action<BlobUploadEndpointOptions>? configure = null,
        FakeCurrentUser? user = null) {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        var store = new RecordingBlobStore();
        builder.Services.AddSingleton<IBlobStore>(store);
        builder.Services.AddScoped<ICurrentUser>(_ => user ?? new FakeCurrentUser("user-1", isAuthenticated: true));
        builder.Services.AddProblemDetails();
        builder.Services.AddElarionBlobUploads(configure);

        var app = builder.Build();
        app.MapElarionBlobUploads();
        await app.StartAsync(cancellationToken);

        var baseAddress = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var client = new HttpClient { BaseAddress = new Uri(baseAddress) };
        return new TestHost(app, client, store);
    }

    private sealed class TestHost(WebApplication app, HttpClient client, RecordingBlobStore store) : IAsyncDisposable {
        public HttpClient Client { get; } = client;

        public RecordingBlobStore Store { get; } = store;

        public async ValueTask DisposeAsync() {
            Client.Dispose();
            await app.DisposeAsync();
        }
    }

    private sealed class RecordingBlobStore : IBlobStore {
        private readonly Dictionary<string, (BlobMetadata Metadata, byte[] Data)> _blobs = new(StringComparer.Ordinal);

        public List<BlobUploadRequest> Requests { get; } = [];

        public BlobUploadRequest? LastRequest => Requests.Count > 0 ? Requests[^1] : null;

        public List<string> Deleted { get; } = [];

        public void Seed(BlobMetadata metadata) => _blobs[metadata.Id] = (metadata, []);

        public byte[] SavedContent(string id) => _blobs[id].Data;

        public async Task<BlobRef> SaveAsync(BlobUploadRequest request, Stream content, CancellationToken cancellationToken) {
            Requests.Add(request);
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            var id = Guid.NewGuid().ToString();
            _blobs[id] = (
                new BlobMetadata {
                    Id = id,
                    Container = request.Container,
                    Name = request.Name,
                    ContentType = request.ContentType,
                    Size = buffer.Length,
                    CreatedAt = DateTimeOffset.UnixEpoch,
                    State = request.InitialState,
                    OwnerId = request.OwnerId
                },
                buffer.ToArray());
            return new BlobRef { Value = id };
        }

        public Task<BlobMetadata?> GetMetadataAsync(BlobRef blobRef, CancellationToken cancellationToken) =>
            Task.FromResult(_blobs.TryGetValue(blobRef.Value, out var entry) ? entry.Metadata : null);

        public Task<bool> DeleteAsync(BlobRef blobRef, CancellationToken cancellationToken) {
            Deleted.Add(blobRef.Value);
            return Task.FromResult(_blobs.Remove(blobRef.Value));
        }

        public Task<bool> ExistsAsync(BlobRef blobRef, CancellationToken cancellationToken) =>
            Task.FromResult(_blobs.ContainsKey(blobRef.Value));

        public Task<BlobDownload?> OpenReadAsync(BlobRef blobRef, CancellationToken cancellationToken) =>
            Task.FromResult<BlobDownload?>(null);

        public Task<BlobListing> ListAsync(BlobListRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ListContainersAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeCurrentUser(string userId, bool isAuthenticated) : ICurrentUser {
        public string UserId { get; } = userId;

        public string? Email => null;

        public IReadOnlyList<string> Roles => [];

        public bool IsAuthenticated { get; } = isAuthenticated;

        public bool IsInRole(string role) => false;
    }
}

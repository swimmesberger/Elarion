using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AwesomeAssertions;
using Elarion.Abstractions.Identity;
using Elarion.Blobs;
using Elarion.Blobs.Tus;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Elarion.Tests.Blobs;

/// <summary>
/// End-to-end tests for the tus 1.0 resumable-upload endpoints over a real Kestrel host with the in-memory
/// store: capability discovery, single- and multi-chunk uploads producing a pending blob reference,
/// offset/content-type validation, ownership, and termination.
/// </summary>
public sealed class TusEndpointsTests {
    [Fact]
    public async Task Options_AdvertisesCapabilities() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct);

        var response = await host.Client.SendAsync(
            new HttpRequestMessage(HttpMethod.Options, "/_elarion/blobs/tus"), ct);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        Header(response, "Tus-Resumable").Should().Be("1.0.0");
        Header(response, "Tus-Version").Should().Be("1.0.0");
        Header(response, "Tus-Extension").Should().Contain("creation").And.Contain("expiration");
        Header(response, "Tus-Max-Size").Should().NotBeNull();
    }

    [Fact]
    public async Task CreateThenPatch_Completes_ProducesPendingBlob() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct);
        var payload = new byte[] { 1, 2, 3, 4, 5 };

        var uploadPath = await CreateAsync(host.Client, payload.Length, "doc.pdf", "application/pdf", ct);

        var patch = await host.Client.SendAsync(Patch(uploadPath, 0, payload), ct);

        patch.StatusCode.Should().Be(HttpStatusCode.NoContent);
        Header(patch, "Upload-Offset").Should().Be("5");
        var blobRef = Header(patch, "Elarion-Blob-Ref");
        blobRef.Should().NotBeNullOrEmpty();

        var saved = host.Store.LastRequest!;
        saved.InitialState.Should().Be(BlobLifecycleState.Pending);
        saved.ContentType.Should().Be("application/pdf");
        saved.Name.Should().StartWith("user-1/").And.EndWith("doc.pdf");
        saved.ExpiresAt.Should().NotBeNull();
        host.Store.Content(blobRef!).Should().Equal(payload);
    }

    [Fact]
    public async Task Create_ZeroLength_CompletesOnCreation() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct);

        var request = new HttpRequestMessage(HttpMethod.Post, "/_elarion/blobs/tus");
        request.Headers.Add("Upload-Length", "0");
        request.Headers.Add("Upload-Metadata", $"filename {Base64("empty.bin")}");

        var response = await host.Client.SendAsync(request, ct);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        Header(response, "Upload-Offset").Should().Be("0");
        var blobRef = Header(response, "Elarion-Blob-Ref");
        blobRef.Should().NotBeNullOrEmpty();
        host.Store.LastRequest!.InitialState.Should().Be(BlobLifecycleState.Pending);
        host.Store.Content(blobRef!).Should().BeEmpty();

        // HEAD reflects completion (offset == length == 0, reference available).
        var head = await host.Client.SendAsync(
            new HttpRequestMessage(HttpMethod.Head, response.Headers.Location!.AbsolutePath), ct);
        Header(head, "Upload-Offset").Should().Be("0");
        Header(head, "Upload-Length").Should().Be("0");
        Header(head, "Elarion-Blob-Ref").Should().Be(blobRef);
    }

    [Fact]
    public async Task ResumableUpload_InTwoChunks_Completes() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct);

        var uploadPath = await CreateAsync(host.Client, 6, "data.bin", "application/octet-stream", ct);

        var first = await host.Client.SendAsync(Patch(uploadPath, 0, [1, 2, 3]), ct);
        first.StatusCode.Should().Be(HttpStatusCode.NoContent);
        Header(first, "Upload-Offset").Should().Be("3");
        Header(first, "Elarion-Blob-Ref").Should().BeNull();

        var head = await host.Client.SendAsync(new HttpRequestMessage(HttpMethod.Head, uploadPath), ct);
        head.StatusCode.Should().Be(HttpStatusCode.OK);
        Header(head, "Upload-Offset").Should().Be("3");
        Header(head, "Upload-Length").Should().Be("6");

        var second = await host.Client.SendAsync(Patch(uploadPath, 3, [4, 5, 6]), ct);
        second.StatusCode.Should().Be(HttpStatusCode.NoContent);
        Header(second, "Upload-Offset").Should().Be("6");
        var blobRef = Header(second, "Elarion-Blob-Ref");
        blobRef.Should().NotBeNullOrEmpty();
        host.Store.Content(blobRef!).Should().Equal([1, 2, 3, 4, 5, 6]);
    }

    [Fact]
    public async Task Patch_WrongOffset_Returns409() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct);
        var uploadPath = await CreateAsync(host.Client, 4, "a.bin", "application/octet-stream", ct);

        var response = await host.Client.SendAsync(Patch(uploadPath, 2, [1, 2]), ct);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Patch_WrongContentType_Returns415() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct);
        var uploadPath = await CreateAsync(host.Client, 4, "a.bin", "application/octet-stream", ct);

        var request = new HttpRequestMessage(HttpMethod.Patch, uploadPath) {
            Content = new ByteArrayContent([1, 2, 3, 4])
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Headers.Add("Upload-Offset", "0");

        var response = await host.Client.SendAsync(request, ct);

        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task Create_OverMaxSize_Returns413() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct, configure: options => options.MaxSize = 4);

        var request = new HttpRequestMessage(HttpMethod.Post, "/_elarion/blobs/tus");
        request.Headers.Add("Upload-Length", "100");

        var response = await host.Client.SendAsync(request, ct);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task Create_Unauthenticated_Returns401() {
        var ct = TestContext.Current.CancellationToken;
        var user = new MutableCurrentUser { UserId = "user-1", IsAuthenticated = false };
        await using var host = await StartAsync(ct, user: user);

        var request = new HttpRequestMessage(HttpMethod.Post, "/_elarion/blobs/tus");
        request.Headers.Add("Upload-Length", "4");

        var response = await host.Client.SendAsync(request, ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Patch_OtherUsersUpload_Returns404() {
        var ct = TestContext.Current.CancellationToken;
        var user = new MutableCurrentUser { UserId = "user-1", IsAuthenticated = true };
        await using var host = await StartAsync(ct, user: user);
        var uploadPath = await CreateAsync(host.Client, 4, "a.bin", "application/octet-stream", ct);

        user.UserId = "user-2";
        var response = await host.Client.SendAsync(Patch(uploadPath, 0, [1, 2, 3, 4]), ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Head_UnauthenticatedCallerWithMatchingId_Returns404() {
        // Regression (fail closed): an owner-scoped operation requires an authenticated caller. Even a
        // caller whose id equals the recorded owner is denied while unauthenticated, so a null/empty owner
        // id can never be matched.
        var ct = TestContext.Current.CancellationToken;
        var user = new MutableCurrentUser { UserId = "user-1", IsAuthenticated = true };
        await using var host = await StartAsync(ct, user: user);
        var uploadPath = await CreateAsync(host.Client, 4, "a.bin", "application/octet-stream", ct);

        user.IsAuthenticated = false;
        var head = await host.Client.SendAsync(new HttpRequestMessage(HttpMethod.Head, uploadPath), ct);

        head.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_RemovesSession() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct);
        var uploadPath = await CreateAsync(host.Client, 4, "a.bin", "application/octet-stream", ct);

        var delete = await host.Client.DeleteAsync(uploadPath, ct);
        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var head = await host.Client.SendAsync(new HttpRequestMessage(HttpMethod.Head, uploadPath), ct);
        head.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static async Task<string> CreateAsync(
        HttpClient client,
        long length,
        string fileName,
        string contentType,
        CancellationToken ct) {
        var request = new HttpRequestMessage(HttpMethod.Post, "/_elarion/blobs/tus");
        request.Headers.Add("Upload-Length", length.ToString());
        request.Headers.Add("Upload-Metadata", $"filename {Base64(fileName)},filetype {Base64(contentType)}");

        var response = await client.SendAsync(request, ct);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return response.Headers.Location!.AbsolutePath;
    }

    private static HttpRequestMessage Patch(string uploadPath, long offset, byte[] body) {
        var request = new HttpRequestMessage(HttpMethod.Patch, uploadPath) {
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/offset+octet-stream");
        request.Headers.Add("Upload-Offset", offset.ToString());
        return request;
    }

    private static string Base64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static string? Header(HttpResponseMessage response, string name) {
        if (response.Headers.TryGetValues(name, out var values)) {
            return string.Join(",", values);
        }

        return name == "Location" && response.Headers.Location is { } location ? location.ToString() : null;
    }

    private static async Task<TestHost> StartAsync(
        CancellationToken cancellationToken,
        Action<TusOptions>? configure = null,
        MutableCurrentUser? user = null) {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        var store = new RecordingBlobStore();
        builder.Services.AddSingleton<IBlobStore>(store);
        builder.Services.AddSingleton<ICurrentUser>(user ?? new MutableCurrentUser { UserId = "user-1", IsAuthenticated = true });
        builder.Services.AddElarionTus(configure);

        var app = builder.Build();
        app.MapElarionTus();
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
        private readonly Dictionary<string, byte[]> _content = new(StringComparer.Ordinal);

        public BlobUploadRequest? LastRequest { get; private set; }

        public byte[] Content(string id) => _content[id];

        public async Task<BlobRef> SaveAsync(BlobUploadRequest request, Stream content, CancellationToken cancellationToken) {
            LastRequest = request;
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            var id = Guid.NewGuid().ToString();
            _content[id] = buffer.ToArray();
            return new BlobRef { Value = id };
        }

        public Task<BlobMetadata?> GetMetadataAsync(BlobRef blobRef, CancellationToken cancellationToken) =>
            Task.FromResult<BlobMetadata?>(null);

        public Task<bool> DeleteAsync(BlobRef blobRef, CancellationToken cancellationToken) =>
            Task.FromResult(_content.Remove(blobRef.Value));

        public Task<bool> ExistsAsync(BlobRef blobRef, CancellationToken cancellationToken) =>
            Task.FromResult(_content.ContainsKey(blobRef.Value));

        public Task<BlobDownload?> OpenReadAsync(BlobRef blobRef, CancellationToken cancellationToken) =>
            Task.FromResult<BlobDownload?>(null);
    }

    private sealed class MutableCurrentUser : ICurrentUser {
        public required string UserId { get; set; }

        public required bool IsAuthenticated { get; set; }

        public string? Email => null;

        public IReadOnlyList<string> Roles => [];

        public bool IsInRole(string role) => false;
    }
}

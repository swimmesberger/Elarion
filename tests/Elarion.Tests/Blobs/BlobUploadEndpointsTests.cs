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
using Microsoft.Extensions.Options;
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
        response.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
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
    public async Task UploadRaw_PassesContentLengthHintToStore() {
        // The declared length lets the store stream without buffering the whole body in memory; the
        // store verifies the actual bytes against the hint.
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct);

        var content = new ByteArrayContent([9, 8, 7]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var response = await host.Client.PostAsync("/_elarion/blobs?fileName=raw.bin", content, ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        host.Store.LastRequest!.ContentLength.Should().Be(3);
    }

    [Fact]
    public async Task Upload_FileNameWithControlCharsAndQuotes_IsSanitized() {
        // CR/LF or quotes in the stored name would later be rejected when rendered into the download
        // Content-Disposition header, making the blob permanently undownloadable.
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct);

        var content = new ByteArrayContent([1, 2, 3]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var response = await host.Client.PostAsync(
            "/_elarion/blobs?fileName=evil%0D%0Aname%22.bin", content, ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var name = host.Store.LastRequest!.Name;
        name.Should().EndWith("evilname.bin");
        name.Should().NotContainAny("\r", "\n", "\"");
    }

    [Fact]
    public async Task Upload_Unauthenticated_Returns401() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct, user: new FakeCurrentUser("user-1", false));

        var content = new ByteArrayContent([1]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var response = await host.Client.PostAsync("/_elarion/blobs", content, ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        host.Store.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task Cancel_Unauthenticated_Returns401() {
        // The auth guard runs before the ownership check, so an anonymous DELETE is a clean 401 — even for
        // the recorded owner — rather than reaching (and, with the shipped ICurrentUser, throwing on) the id.
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct, user: new FakeCurrentUser("user-1", false));
        host.Store.Seed(new BlobMetadata {
            Id = "owned",
            Container = "uploads",
            Name = "user-1/abc/file.bin",
            ContentType = "application/octet-stream",
            Size = 1,
            CreatedAt = DateTimeOffset.UnixEpoch,
            State = BlobLifecycleState.Pending,
            OwnerId = "user-1"
        });

        var response = await host.Client.DeleteAsync("/_elarion/blobs/owned", ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        host.Store.Deleted.Should().NotContain("owned");
    }

    [Fact]
    public async Task Upload_DisallowedContentType_Returns400() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct, options =>
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
        await using var host = await StartAsync(ct, options => options.MaxContentLength = 4);

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
        await using var host = await StartAsync(ct, options => options.MaxContentLength = 4);

        var content = new ByteArrayContent([1, 2, 3, 4, 5, 6]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var response = await host.Client.PostAsync("/_elarion/blobs", content, ct);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        host.Store.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task Upload_OversizeRaw_Chunked_Returns413() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct, options => options.MaxContentLength = 4);

        var content = new StreamContent(new MemoryStream(new byte[16]));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var request = new HttpRequestMessage(HttpMethod.Post, "/_elarion/blobs") { Content = content };
        request.Headers.TransferEncodingChunked = true;

        var response = await host.Client.SendAsync(request, ct);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task Cancel_OwnersCommittedBlob_Returns404AndKeepsIt() {
        // A committed blob belongs to the record that kept it; cancelling it would break that record.
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct);
        host.Store.Seed(new BlobMetadata {
            Id = "kept",
            Container = "uploads",
            Name = "user-1/abc/file.bin",
            ContentType = "application/octet-stream",
            Size = 1,
            CreatedAt = DateTimeOffset.UnixEpoch,
            State = BlobLifecycleState.Committed,
            OwnerId = "user-1"
        });

        var response = await host.Client.DeleteAsync("/_elarion/blobs/kept", ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        host.Store.Deleted.Should().NotContain("kept");
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
        await using var host = await StartAsync(ct, user: new FakeCurrentUser("a", true));
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

    [Fact]
    public void Options_AreOneValueForTheEndpointsAndTheOptionsPattern() {
        var services = new ServiceCollection();
        services.AddElarionBlobUploads(options => options.RoutePrefix = "/files");
        services.Configure<BlobUploadEndpointOptions>(options => options.Container = "inbox");
        using var provider = services.BuildServiceProvider();

        var fromOptions = provider.GetRequiredService<IOptions<BlobUploadEndpointOptions>>().Value;
        fromOptions.RoutePrefix.Should().Be("/files");
        fromOptions.Container.Should().Be("inbox");
        provider.GetRequiredService<BlobUploadEndpointOptions>().Should().BeSameAs(fromOptions);
    }

    [Fact]
    public async Task Upload_HonoursOptionsConfiguredThroughTheOptionsPattern() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct, services: services =>
            services.Configure<BlobUploadEndpointOptions>(options => options.RoutePrefix = "/files"));

        var response = await host.Client.PostAsync("/files", new ByteArrayContent([1]), ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Upload_UnderAPolicy_TakesItsCap_AndRaisesTheBodyLimitForThatRequestOnly() {
        var ct = TestContext.Current.CancellationToken;
        // The server accepts 1,000-byte bodies, the endpoint 500 bytes; the policy 4,000 bytes.
        await using var host = await StartAsync(ct, options => options.MaxContentLength = 500,
            services: services => services.AddElarionUploadPolicy("large", policy => policy.MaxBytes = 4000),
            serverBodyLimit: 1000);

        var plain = await host.Client.PostAsync("/_elarion/blobs", Raw(2000), ct);
        var underPolicy = await host.Client.PostAsync("/_elarion/blobs?policy=large", Raw(2000), ct);
        var chunkedOverPolicy = await host.Client.SendAsync(Chunked("/_elarion/blobs?policy=large", 5000), ct);

        plain.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        underPolicy.StatusCode.Should().Be(HttpStatusCode.OK);
        host.Store.SavedContent(await underPolicy.Content.ReadAsStringAsync(ct)).Should().HaveCount(2000);
        chunkedOverPolicy.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task Upload_UnderAPolicy_Multipart_IsCappedByThePolicy() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct, options => options.MaxContentLength = 500,
            services: services => services.AddElarionUploadPolicy("large", policy => policy.MaxBytes = 4000),
            serverBodyLimit: 1000);

        var fits = await host.Client.PostAsync("/_elarion/blobs?policy=large", Form(3000), ct);
        var tooLarge = await host.Client.PostAsync("/_elarion/blobs?policy=large", Form(5000), ct);

        fits.StatusCode.Should().Be(HttpStatusCode.OK);
        tooLarge.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        host.Store.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task Upload_UnderAPolicy_RefusedByItsCheck_Returns404_BeforeTheBodyReachesTheStore() {
        var ct = TestContext.Current.CancellationToken;
        var asked = new List<UploadPolicyContext>();
        await using var host = await StartAsync(ct, services: services =>
            services.AddElarionUploadPolicy("mail.eml", policy => policy.Authorize = (context, _) => {
                asked.Add(context);
                return ValueTask.FromResult(context.ResourceId == "draft-1" && context.User.UserId == "user-1");
            }));

        var refused = await host.Client.PostAsync("/_elarion/blobs?policy=mail.eml&resourceId=draft-2", Raw(10), ct);
        var anonymousRecord = await host.Client.PostAsync("/_elarion/blobs?policy=mail.eml", Raw(10), ct);
        var admitted = await host.Client.PostAsync("/_elarion/blobs?policy=mail.eml&resourceId=draft-1", Raw(10), ct);

        refused.StatusCode.Should().Be(HttpStatusCode.NotFound);
        anonymousRecord.StatusCode.Should().Be(HttpStatusCode.NotFound);
        admitted.StatusCode.Should().Be(HttpStatusCode.OK);
        host.Store.Requests.Should().HaveCount(1, "a refused upload never reaches the store");
        asked.Select(context => context.ResourceId).Should().Equal("draft-2", null, "draft-1");
        asked.Should().AllSatisfy(context => {
            context.Policy.Should().Be("mail.eml");
            context.Length.Should().Be(10);
        });
    }

    [Fact]
    public async Task Upload_NamingAnUnknownPolicy_Returns400() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct);

        var response = await host.Client.PostAsync("/_elarion/blobs?policy=missing", Raw(10), ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        host.Store.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task Upload_UnderAPolicy_ItsContentTypesReplaceTheEndpoints() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct, options => options.AllowedContentTypes = ["application/pdf"],
            services: services => services.AddElarionUploadPolicy("mail.eml",
                policy => policy.ContentTypes = ["message/rfc822"]));

        var message = await host.Client.PostAsync("/_elarion/blobs?policy=mail.eml", Raw(10, "message/rfc822"), ct);
        var pdfUnderPolicy = await host.Client.PostAsync("/_elarion/blobs?policy=mail.eml", Raw(10, "application/pdf"), ct);
        var messageWithoutPolicy = await host.Client.PostAsync("/_elarion/blobs", Raw(10, "message/rfc822"), ct);

        message.StatusCode.Should().Be(HttpStatusCode.OK);
        pdfUnderPolicy.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        messageWithoutPolicy.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public void AddElarionUploadPolicy_ConfiguresARegisteredPolicyAgain_AndRefusesNamesAClientCannotSend() {
        var services = new ServiceCollection();
        services.AddElarionUploadPolicy("mail.eml", policy => policy.MaxBytes = 10);
        services.AddElarionUploadPolicy("mail.eml", policy => policy.ContentTypes = ["message/rfc822"]);
        services.AddElarionUploadPolicy("avatar", policy => policy.MaxBytes = 1);
        using var provider = services.BuildServiceProvider();

        var policies = provider.GetRequiredService<UploadPolicies>();
        policies.Names.Should().BeEquivalentTo("mail.eml", "avatar");
        policies.TryGet("mail.eml", out var policy).Should().BeTrue();
        policy!.MaxBytes.Should().Be(10);
        policy.ContentTypes.Should().Equal("message/rfc822");
        services.Count(descriptor => descriptor.ServiceType == typeof(UploadPolicies)).Should().Be(1);

        foreach (var invalid in new[] { "", "with space", "ümlaut", "tab\t" }) {
            var add = () => services.AddElarionUploadPolicy(invalid, _ => { });
            add.Should().Throw<ArgumentException>();
        }
    }

    private static ByteArrayContent Raw(int length, string contentType = "application/octet-stream") {
        var content = new ByteArrayContent(new byte[length]);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return content;
    }

    private static MultipartFormDataContent Form(int length) {
        var content = new MultipartFormDataContent();
        var filePart = new ByteArrayContent(new byte[length]);
        filePart.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(filePart, "file", "message.eml");
        return content;
    }

    private static HttpRequestMessage Chunked(string url, int length) {
        var content = new StreamContent(new MemoryStream(new byte[length]));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.TransferEncodingChunked = true;
        return request;
    }

    private static async Task<TestHost> StartAsync(
        CancellationToken cancellationToken,
        Action<BlobUploadEndpointOptions>? configure = null,
        FakeCurrentUser? user = null,
        Action<IServiceCollection>? services = null,
        long? serverBodyLimit = null) {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        if (serverBodyLimit is long limit)
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = limit);
        builder.Logging.ClearProviders();

        var store = new RecordingBlobStore();
        builder.Services.AddSingleton<IBlobStore>(store);
        builder.Services.AddScoped<ICurrentUser>(_ => user ?? new FakeCurrentUser("user-1", true));
        builder.Services.AddProblemDetails();
        builder.Services.AddElarionBlobUploads(configure);
        services?.Invoke(builder.Services);

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

        public void Seed(BlobMetadata metadata) {
            _blobs[metadata.Id] = (metadata, []);
        }

        public byte[] SavedContent(string id) {
            return _blobs[id].Data;
        }

        public async Task<BlobRef> SaveAsync(BlobUploadRequest request, Stream content,
            CancellationToken cancellationToken) {
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

        public Task<BlobMetadata?> GetMetadataAsync(BlobRef blobRef, CancellationToken cancellationToken) {
            return Task.FromResult(_blobs.TryGetValue(blobRef.Value, out var entry) ? entry.Metadata : null);
        }

        public Task<bool> DeleteAsync(BlobRef blobRef, CancellationToken cancellationToken) {
            Deleted.Add(blobRef.Value);
            return Task.FromResult(_blobs.Remove(blobRef.Value));
        }

        public Task<bool> ExistsAsync(BlobRef blobRef, CancellationToken cancellationToken) {
            return Task.FromResult(_blobs.ContainsKey(blobRef.Value));
        }

        public Task<BlobDownload?> OpenReadAsync(BlobRef blobRef, CancellationToken cancellationToken) {
            return Task.FromResult<BlobDownload?>(null);
        }

        public Task<BlobListing> ListAsync(BlobListRequest request, CancellationToken cancellationToken) {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<string>> ListContainersAsync(CancellationToken cancellationToken) {
            throw new NotSupportedException();
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

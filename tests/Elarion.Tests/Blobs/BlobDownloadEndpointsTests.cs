using System.Net;
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
/// End-to-end tests for the owner-scoped streaming download endpoint over a real Kestrel host — the download
/// leg of the staged-blob file tier: only the exact recorded owner can read a blob, everything else is 404
/// (never leaking ownership), and the payload streams with its content type and leaf file name.
/// </summary>
public sealed class BlobDownloadEndpointsTests {
    private static readonly BlobRef ExportRef = new() { Value = "export-1" };

    private static BlobMetadata ExportMetadata(string? ownerId = "user-1", string container = "uploads") => new() {
        Id = ExportRef.Value,
        Container = container,
        Name = $"{ownerId ?? "nobody"}/abc123/clients.xlsx",
        ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        Size = 3,
        CreatedAt = DateTimeOffset.UnixEpoch,
        State = BlobLifecycleState.Pending,
        OwnerId = ownerId,
    };

    [Fact]
    public async Task Download_OwnersBlob_StreamsContentWithTypeAndLeafName() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct);
        host.Store.Seed(ExportRef, ExportMetadata(), [1, 2, 3]);

        var response = await host.Client.GetAsync($"/_elarion/blobs/{ExportRef.Value}", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType
            .Should().Be("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        response.Content.Headers.ContentDisposition!.ToString()
            .Should().Contain("attachment").And.Contain("clients.xlsx");
        (await response.Content.ReadAsByteArrayAsync(ct)).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task Download_OtherUsersBlob_Returns404() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct);
        host.Store.Seed(ExportRef, ExportMetadata(ownerId: "user-2"), [1]);

        var response = await host.Client.GetAsync($"/_elarion/blobs/{ExportRef.Value}", ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Download_UnownedBlob_Returns404() {
        // Fail closed: a blob with no recorded owner is readable by no one.
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct);
        host.Store.Seed(ExportRef, ExportMetadata(ownerId: null), [1]);

        var response = await host.Client.GetAsync($"/_elarion/blobs/{ExportRef.Value}", ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Download_ForeignContainer_Returns404() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct);
        host.Store.Seed(ExportRef, ExportMetadata(container: "avatars"), [1]);

        var response = await host.Client.GetAsync($"/_elarion/blobs/{ExportRef.Value}", ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Download_MissingBlob_Returns404() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct);

        var response = await host.Client.GetAsync("/_elarion/blobs/nope", ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Download_Unauthenticated_Returns401() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct, user: new FakeCurrentUser("user-1", isAuthenticated: false));
        host.Store.Seed(ExportRef, ExportMetadata(), [1]);

        var response = await host.Client.GetAsync($"/_elarion/blobs/{ExportRef.Value}", ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static async Task<TestHost> StartAsync(
        CancellationToken cancellationToken,
        FakeCurrentUser? user = null) {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        var store = new FakeBlobStore();
        builder.Services.AddSingleton<IBlobStore>(store);
        builder.Services.AddScoped<ICurrentUser>(_ => user ?? new FakeCurrentUser("user-1", isAuthenticated: true));
        builder.Services.AddProblemDetails();
        builder.Services.AddElarionBlobUploads();

        var app = builder.Build();
        app.MapElarionBlobDownloads();
        await app.StartAsync(cancellationToken);

        var baseAddress = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var client = new HttpClient { BaseAddress = new Uri(baseAddress) };
        return new TestHost(app, client, store);
    }

    private sealed class TestHost(WebApplication app, HttpClient client, FakeBlobStore store) : IAsyncDisposable {
        public HttpClient Client { get; } = client;

        public FakeBlobStore Store { get; } = store;

        public async ValueTask DisposeAsync() {
            Client.Dispose();
            await app.DisposeAsync();
        }
    }

    private sealed class FakeCurrentUser(string userId, bool isAuthenticated) : ICurrentUser {
        public string UserId { get; } = userId;

        public string? Email => null;

        public IReadOnlyList<string> Roles => [];

        public bool IsAuthenticated { get; } = isAuthenticated;

        public bool IsInRole(string role) => false;
    }
}

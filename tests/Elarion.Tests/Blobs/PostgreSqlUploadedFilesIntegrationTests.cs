using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Identity;
using Elarion.Blobs;
using Elarion.Blobs.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Elarion.Tests.Blobs;

/// <summary>
/// <see cref="IUploadedFiles"/> over the real PostgreSQL blob store: it reads what an upload endpoint stored (owner,
/// state, name, content hash), and keeping an upload joins the caller's transaction, so a rollback leaves it pending.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgreSqlUploadedFilesIntegrationTests(PostgreSqlBlobStoreFixture fixture)
    : IClassFixture<PostgreSqlBlobStoreFixture> {
    private static readonly byte[] Csv = "date;amount\n2026-11-01;12.50\n"u8.ToArray();

    [Fact]
    public async Task Open_PendingUploadOfCaller_StreamsTheStoredContent_AndRefusesAnotherUser() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        var (container, file) = await StageAsync("user-1", ct);

        await using var context = fixture.CreateContext();
        await using var provider = CreateServices(context, container, "user-1");
        await using var scope = provider.CreateAsyncScope();
        var uploads = scope.ServiceProvider.GetRequiredService<IUploadedFiles>();

        var opened = await uploads.OpenAsync(file, new UploadConstraints { ContentTypes = ["text/csv"] }, ct);

        opened.IsSuccess.Should().BeTrue();
        await using (var content = opened.Value) {
            using var buffer = new MemoryStream();
            await content.Content.CopyToAsync(buffer, ct);
            buffer.ToArray().Should().Equal(Csv);
            content.FileName.Should().Be("statement.csv");
            content.Length.Should().Be(Csv.Length);
            content.ContentSha256.Should().Be(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Csv)));
        }

        await using var otherContext = fixture.CreateContext();
        await using var otherProvider = CreateServices(otherContext, container, "user-2");
        await using var otherScope = otherProvider.CreateAsyncScope();
        var foreign = await otherScope.ServiceProvider.GetRequiredService<IUploadedFiles>()
            .OpenAsync(file, cancellationToken: ct);
        foreign.Error.Code.Should().Be(ErrorCodes.UploadNotFound);
    }

    [Fact]
    public async Task Commit_JoinsTheCallersTransaction() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var ct = TestContext.Current.CancellationToken;
        var (container, file) = await StageAsync("user-1", ct);

        await using (var context = fixture.CreateContext()) {
            await using var provider = CreateServices(context, container, "user-1");
            await using var scope = provider.CreateAsyncScope();
            var uploads = scope.ServiceProvider.GetRequiredService<IUploadedFiles>();

            await using var rolledBack = await context.Database.BeginTransactionAsync(ct);
            (await uploads.CommitAsync(file, cancellationToken: ct)).IsSuccess.Should().BeTrue();
            await rolledBack.RollbackAsync(ct);
        }

        (await StateAsync(file, ct)).Should().Be(BlobLifecycleState.Pending);

        await using (var context = fixture.CreateContext()) {
            await using var provider = CreateServices(context, container, "user-1");
            await using var scope = provider.CreateAsyncScope();
            var uploads = scope.ServiceProvider.GetRequiredService<IUploadedFiles>();

            await using var committed = await context.Database.BeginTransactionAsync(ct);
            var kept = await uploads.CommitAsync(file, cancellationToken: ct);
            await context.SaveChangesAsync(ct);
            await committed.CommitAsync(ct);

            kept.Value.BlobId.Should().Be(file.Id);
        }

        (await StateAsync(file, ct)).Should().Be(BlobLifecycleState.Committed);
    }

    // Stages an upload the way the endpoints do: pending, owned, named "{owner}/{guid}/{fileName}", in a container of
    // the test's own so tests stay isolated.
    private async Task<(string Container, UploadedFile File)> StageAsync(string owner, CancellationToken ct) {
        var container = $"uploads-{Guid.NewGuid():N}";
        await using var context = fixture.CreateContext();
        var blobRef = await CreateStore(context).SaveAsync(new BlobUploadRequest {
            Container = container,
            Name = $"{owner}/{Guid.CreateVersion7():N}/statement.csv",
            ContentType = "text/csv",
            InitialState = BlobLifecycleState.Pending,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
            OwnerId = owner
        }, new MemoryStream(Csv), ct);
        return (container, new UploadedFile { Id = blobRef.Value });
    }

    private async Task<BlobLifecycleState> StateAsync(UploadedFile file, CancellationToken ct) {
        await using var context = fixture.CreateContext();
        var row = await context.Set<StoredBlob>().AsNoTracking().SingleAsync(blob => blob.Id == file.Id, ct);
        return row.State;
    }

    private static ServiceProvider CreateServices(IntegrationBlobDbContext context, string container, string userId) {
        var store = CreateStore(context);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICurrentUser>(new FakeCurrentUser(userId));
        services.AddSingleton<IBlobStore>(store);
        services.AddSingleton<IBlobLifecycle>(store);
        services.AddElarionUploadedFiles(options => options.Containers = [container]);
        return services.BuildServiceProvider();
    }

    private static PostgreSqlBlobStore<IntegrationBlobDbContext> CreateStore(IntegrationBlobDbContext context) {
        return new PostgreSqlBlobStore<IntegrationBlobDbContext>(context,
            NullLogger<PostgreSqlBlobStore<IntegrationBlobDbContext>>.Instance, TimeProvider.System);
    }

    private sealed class FakeCurrentUser(string userId) : ICurrentUser {
        public string UserId { get; } = userId;

        public string? Email => null;

        public IReadOnlyList<string> Roles => [];

        public bool IsAuthenticated => true;

        public bool IsInRole(string role) {
            return false;
        }
    }
}

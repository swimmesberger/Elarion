using AwesomeAssertions;
using Elarion.Blobs;
using Elarion.Blobs.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Elarion.Tests.Blobs;

/// <summary>
/// Verifies that <see cref="PostgreSqlBlobStore{TDbContext}"/> enlists in the caller's ambient EF Core
/// transaction. A save rolls back both the metadata row and the raw-Npgsql <c>bytea</c> content together; a
/// delete inside a committed transaction removes the metadata row and the content row cascades with it. Skips
/// when Docker is unavailable.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgreSqlBlobStoreTransactionTests(PostgreSqlBlobStoreFixture fixture)
    : IClassFixture<PostgreSqlBlobStoreFixture> {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SaveAsync_RolledBackWithCallerTransaction_PersistsNeitherMetadataNorContent() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var request = NewRequest();
        BlobRef blobRef;

        await using (var context = fixture.CreateContext()) {
            var store = CreateStore(context);
            await using var transaction = await context.Database.BeginTransactionAsync(Ct);
            blobRef = await store.SaveAsync(request, new MemoryStream(Bytes(256)), Ct);
            await transaction.RollbackAsync(Ct);
        }

        await using var verifyContext = fixture.CreateContext();
        (await CreateStore(verifyContext).ExistsAsync(blobRef, Ct)).Should().BeFalse();
        (await ContentRowCount(verifyContext, blobRef.Value, Ct)).Should().Be(0);
    }

    [Fact]
    public async Task SaveAsync_CommittedWithCallerTransaction_PersistsMetadataAndContent() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var request = NewRequest();
        BlobRef blobRef;

        await using (var context = fixture.CreateContext()) {
            var store = CreateStore(context);
            await using var transaction = await context.Database.BeginTransactionAsync(Ct);
            blobRef = await store.SaveAsync(request, new MemoryStream(Bytes(256)), Ct);
            await transaction.CommitAsync(Ct);
        }

        await using var verifyContext = fixture.CreateContext();
        (await CreateStore(verifyContext).ExistsAsync(blobRef, Ct)).Should().BeTrue();
        (await ContentRowCount(verifyContext, blobRef.Value, Ct)).Should().Be(1);
    }

    [Fact]
    public async Task DeleteAsync_CommittedWithCallerTransaction_RemovesMetadataAndCascadesContent() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        BlobRef blobRef;
        await using (var seedContext = fixture.CreateContext()) {
            blobRef = await CreateStore(seedContext).SaveAsync(NewRequest(), new MemoryStream(Bytes(256)), Ct);
        }

        await using (var context = fixture.CreateContext()) {
            var store = CreateStore(context);
            await using var transaction = await context.Database.BeginTransactionAsync(Ct);
            (await store.DeleteAsync(blobRef, Ct)).Should().BeTrue();
            await transaction.CommitAsync(Ct);
        }

        await using var verifyContext = fixture.CreateContext();
        (await CreateStore(verifyContext).ExistsAsync(blobRef, Ct)).Should().BeFalse();
        (await ContentRowCount(verifyContext, blobRef.Value, Ct)).Should().Be(0);
    }

    [Fact]
    public async Task DeleteAsync_RolledBackWithCallerTransaction_KeepsMetadataAndContent() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        BlobRef blobRef;
        await using (var seedContext = fixture.CreateContext()) {
            blobRef = await CreateStore(seedContext).SaveAsync(NewRequest(), new MemoryStream(Bytes(256)), Ct);
        }

        await using (var context = fixture.CreateContext()) {
            var store = CreateStore(context);
            await using var transaction = await context.Database.BeginTransactionAsync(Ct);
            (await store.DeleteAsync(blobRef, Ct)).Should().BeTrue();
            await transaction.RollbackAsync(Ct);
        }

        await using var verifyContext = fixture.CreateContext();
        (await CreateStore(verifyContext).ExistsAsync(blobRef, Ct)).Should().BeTrue();
        (await ContentRowCount(verifyContext, blobRef.Value, Ct)).Should().Be(1);
    }

    private static PostgreSqlBlobStore<IntegrationBlobDbContext> CreateStore(IntegrationBlobDbContext context) =>
        new(context, NullLogger<PostgreSqlBlobStore<IntegrationBlobDbContext>>.Instance, TimeProvider.System);

    // Probes the raw content table directly, so the assertion is independent of the store's read path.
    private static async Task<long> ContentRowCount(
        IntegrationBlobDbContext context,
        string blobId,
        CancellationToken cancellationToken) =>
        await context.Database
            .SqlQueryRaw<long>("SELECT count(*) AS \"Value\" FROM blob_contents WHERE blob_id = {0}", blobId)
            .SingleAsync(cancellationToken);

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
}

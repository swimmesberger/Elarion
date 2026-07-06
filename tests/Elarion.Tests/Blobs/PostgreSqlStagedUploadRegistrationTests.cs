using AwesomeAssertions;
using Elarion.Blobs;
using Elarion.Blobs.PostgreSql;
using Elarion.Blobs.Tus;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Elarion.Tests.Blobs;

/// <summary>
/// Registration tests for the durable PostgreSQL staged-upload store. In particular, verifies that
/// <c>AddElarionPostgreSqlStagedUploads</c> also wires the pending-blob lifecycle and its garbage
/// collector, so a doc-following host does not leak the pending blobs that completed uploads produce —
/// and that repeated wiring never registers duplicate collectors.
/// </summary>
public sealed class PostgreSqlStagedUploadRegistrationTests {
    [Fact]
    public void AddElarionPostgreSqlStagedUploads_ReplacesStore_AndWiresBothCollectorsOnce() {
        var services = new ServiceCollection();
        services.AddScoped(_ => new TestStagedDbContext(new DbContextOptionsBuilder<TestStagedDbContext>().Options));
        services.AddSingleton<ILogger<PostgreSqlBlobStore<TestStagedDbContext>>>(
            NullLogger<PostgreSqlBlobStore<TestStagedDbContext>>.Instance);
        // AddElarionResumableBlobUploads registers the in-memory staging default (and its collector) first.
        services.AddElarionResumableBlobUploads();

        services.AddElarionPostgreSqlStagedUploads<TestStagedDbContext>("Host=localhost;Database=elarion;Username=elarion;Password=elarion");

        // The session collector is registered exactly once even though both registrations wire it.
        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType == typeof(StagedUploadGarbageCollector));

        // The pending-blob lifecycle and its collector are wired too, so abandoned pending blobs
        // produced by completed uploads are reclaimed rather than leaked forever.
        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType == typeof(BlobGarbageCollector));

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IStagedUploadStore>()
            .Should().BeOfType<PostgreSqlStagedUploadStore<TestStagedDbContext>>();
        provider.GetRequiredService<IBlobStore>().Should().BeOfType<PostgreSqlBlobStore<TestStagedDbContext>>();
        provider.GetRequiredService<IBlobLifecycle>().Should().BeOfType<PostgreSqlBlobStore<TestStagedDbContext>>();
    }

    [Fact]
    public void AddElarionPostgreSqlStagedUploads_HonorsGcConfiguration() {
        var services = new ServiceCollection();
        services.AddScoped(_ => new TestStagedDbContext(new DbContextOptionsBuilder<TestStagedDbContext>().Options));
        services.AddSingleton<ILogger<PostgreSqlBlobStore<TestStagedDbContext>>>(
            NullLogger<PostgreSqlBlobStore<TestStagedDbContext>>.Instance);
        services.AddElarionResumableBlobUploads();

        services.AddElarionPostgreSqlStagedUploads<TestStagedDbContext>(
            gc => gc.BatchSize = 9,
            blobGc => blobGc.BatchSize = 11);

        using var provider = services.BuildServiceProvider();
        // The explicit configuration wins over the defaults AddElarionResumableBlobUploads registered earlier.
        provider.GetRequiredService<StagedUploadGcOptions>().BatchSize.Should().Be(9);
        provider.GetRequiredService<BlobGcOptions>().BatchSize.Should().Be(11);
    }

    private sealed class TestStagedDbContext(DbContextOptions<TestStagedDbContext> options) : DbContext(options);
}

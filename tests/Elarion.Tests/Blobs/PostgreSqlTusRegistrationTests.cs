using AwesomeAssertions;
using Elarion.Blobs;
using Elarion.Blobs.PostgreSql;
using Elarion.Blobs.Tus;
using Elarion.Blobs.Tus.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Elarion.Tests.Blobs;

/// <summary>
/// Registration tests for the durable PostgreSQL tus staging store. In particular, verifies that
/// <c>AddElarionTusPostgreSql</c> also wires the pending-blob lifecycle and its garbage collector, so a
/// doc-following host does not leak the pending blobs that completed tus uploads produce (regression H15).
/// </summary>
public sealed class PostgreSqlTusRegistrationTests {
    [Fact]
    public void AddElarionTusPostgreSql_ReplacesStore_AndWiresBothCollectors() {
        var services = new ServiceCollection();
        services.AddScoped(_ => new TestTusDbContext(new DbContextOptionsBuilder<TestTusDbContext>().Options));
        services.AddSingleton<ILogger<PostgreSqlBlobStore<TestTusDbContext>>>(
            NullLogger<PostgreSqlBlobStore<TestTusDbContext>>.Instance);
        services.AddElarionTus();

        services.AddElarionTusPostgreSql<TestTusDbContext>("Host=localhost;Database=elarion;Username=elarion;Password=elarion");

        // The tus session collector is registered.
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType == typeof(TusUploadGarbageCollector));

        // H15: the pending-blob lifecycle and its collector are wired too, so abandoned pending blobs
        // produced by completed uploads are reclaimed rather than leaked forever.
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType == typeof(BlobGarbageCollector));

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ITusUploadStore>().Should().BeOfType<PostgreSqlTusUploadStore<TestTusDbContext>>();
        provider.GetRequiredService<IBlobStore>().Should().BeOfType<PostgreSqlBlobStore<TestTusDbContext>>();
        provider.GetRequiredService<IBlobLifecycle>().Should().BeOfType<PostgreSqlBlobStore<TestTusDbContext>>();
    }

    [Fact]
    public void AddElarionTusPostgreSql_HonorsBlobGcConfiguration() {
        var services = new ServiceCollection();
        services.AddScoped(_ => new TestTusDbContext(new DbContextOptionsBuilder<TestTusDbContext>().Options));
        services.AddSingleton<ILogger<PostgreSqlBlobStore<TestTusDbContext>>>(
            NullLogger<PostgreSqlBlobStore<TestTusDbContext>>.Instance);
        services.AddElarionTus();

        services.AddElarionTusPostgreSql<TestTusDbContext>(
            gc => gc.CompletedRetention = TimeSpan.FromMinutes(9),
            blobGc => blobGc.BatchSize = 11);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<TusGcOptions>().CompletedRetention.Should().Be(TimeSpan.FromMinutes(9));
        provider.GetRequiredService<BlobGcOptions>().BatchSize.Should().Be(11);
    }

    private sealed class TestTusDbContext(DbContextOptions<TestTusDbContext> options) : DbContext(options);
}

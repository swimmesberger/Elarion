using AwesomeAssertions;
using Elarion.Blobs;
using Elarion.Blobs.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Elarion.Tests.Blobs;

public sealed class PostgreSqlBlobStoreRegistrationTests {
    [Fact]
    public void AddPostgreSqlBlobStore_RegistersBlobStore() {
        var services = new ServiceCollection();
        services.AddScoped(_ => new TestBlobDbContext(new DbContextOptionsBuilder<TestBlobDbContext>().Options));
        services.AddSingleton<ILogger<PostgreSqlBlobStore<TestBlobDbContext>>>(
            NullLogger<PostgreSqlBlobStore<TestBlobDbContext>>.Instance);

        services.AddPostgreSqlBlobStore<TestBlobDbContext>();

        using var provider = services.BuildServiceProvider();
        var blobStore = provider.GetRequiredService<IBlobStore>();

        blobStore.Should().BeOfType<PostgreSqlBlobStore<TestBlobDbContext>>();
        provider.GetRequiredService<TimeProvider>().Should().Be(TimeProvider.System);
    }

    [Fact]
    public void AddPostgreSqlBlobLifecycle_RegistersLifecycleAndCollector() {
        var services = new ServiceCollection();
        services.AddScoped(_ => new TestBlobDbContext(new DbContextOptionsBuilder<TestBlobDbContext>().Options));
        services.AddSingleton<ILogger<PostgreSqlBlobStore<TestBlobDbContext>>>(
            NullLogger<PostgreSqlBlobStore<TestBlobDbContext>>.Instance);

        services.AddPostgreSqlBlobLifecycle<TestBlobDbContext>(options => options.BatchSize = 7);

        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType == typeof(BlobGarbageCollector));

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IBlobStore>().Should().BeOfType<PostgreSqlBlobStore<TestBlobDbContext>>();
        provider.GetRequiredService<IBlobLifecycle>().Should().BeOfType<PostgreSqlBlobStore<TestBlobDbContext>>();
        provider.GetRequiredService<BlobGcOptions>().BatchSize.Should().Be(7);
    }

    private sealed class TestBlobDbContext(DbContextOptions<TestBlobDbContext> options) : DbContext(options);
}

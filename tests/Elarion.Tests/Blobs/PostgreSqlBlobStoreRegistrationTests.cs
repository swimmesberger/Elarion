using AwesomeAssertions;
using Elarion.Blobs;
using Elarion.Blobs.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Elarion.Tests.Blobs;

public sealed class PostgreSqlBlobStoreRegistrationTests {
    private const string ConnectionString = "Host=localhost;Database=elarion;Username=elarion;Password=elarion";

    [Fact]
    public void AddElarionPostgreSqlBlobStore_RegistersBlobStore() {
        var services = new ServiceCollection();
        services.AddScoped(_ => new TestBlobDbContext(new DbContextOptionsBuilder<TestBlobDbContext>().Options));
        services.AddSingleton<ILogger<PostgreSqlBlobStore<TestBlobDbContext>>>(
            NullLogger<PostgreSqlBlobStore<TestBlobDbContext>>.Instance);
        services.AddSingleton(NpgsqlDataSource.Create(ConnectionString));

        services.AddElarionPostgreSqlBlobStore<TestBlobDbContext>();

        using var provider = services.BuildServiceProvider();
        var blobStore = provider.GetRequiredService<IBlobStore>();

        blobStore.Should().BeOfType<PostgreSqlBlobStore<TestBlobDbContext>>();
        provider.GetRequiredService<TimeProvider>().Should().Be(TimeProvider.System);
    }

    [Fact]
    public void AddElarionPostgreSqlBlobLifecycle_RegistersLifecycleAndCollector() {
        var services = new ServiceCollection();
        services.AddScoped(_ => new TestBlobDbContext(new DbContextOptionsBuilder<TestBlobDbContext>().Options));
        services.AddSingleton<ILogger<PostgreSqlBlobStore<TestBlobDbContext>>>(
            NullLogger<PostgreSqlBlobStore<TestBlobDbContext>>.Instance);
        services.AddSingleton(NpgsqlDataSource.Create(ConnectionString));

        services.AddElarionPostgreSqlBlobLifecycle<TestBlobDbContext>(options => options.BatchSize = 7);

        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType == typeof(BlobGarbageCollector));

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IBlobStore>().Should().BeOfType<PostgreSqlBlobStore<TestBlobDbContext>>();
        provider.GetRequiredService<IBlobLifecycle>().Should().BeOfType<PostgreSqlBlobStore<TestBlobDbContext>>();
        provider.GetRequiredService<BlobGcOptions>().BatchSize.Should().Be(7);
    }

    [Fact]
    public void AddElarionPostgreSqlBlobStore_ConnectionStringOverload_RegistersDataSource() {
        var services = new ServiceCollection();
        services.AddScoped(_ => new TestBlobDbContext(new DbContextOptionsBuilder<TestBlobDbContext>().Options));
        services.AddSingleton<ILogger<PostgreSqlBlobStore<TestBlobDbContext>>>(
            NullLogger<PostgreSqlBlobStore<TestBlobDbContext>>.Instance);

        services.AddElarionPostgreSqlBlobStore<TestBlobDbContext>(ConnectionString);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<NpgsqlDataSource>().Should().NotBeNull();
        provider.GetRequiredService<IBlobStore>().Should().BeOfType<PostgreSqlBlobStore<TestBlobDbContext>>();
    }

    [Fact]
    public void AddElarionPostgreSqlBlobStore_ConnectionStringOverload_HostDataSourceWins() {
        var services = new ServiceCollection();
        var hostDataSource = NpgsqlDataSource.Create(ConnectionString);
        services.AddSingleton(hostDataSource);

        services.AddElarionPostgreSqlBlobStore<TestBlobDbContext>(ConnectionString);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<NpgsqlDataSource>().Should().BeSameAs(hostDataSource);
    }

    private sealed class TestBlobDbContext(DbContextOptions<TestBlobDbContext> options) : DbContext(options);
}

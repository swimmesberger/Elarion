using AwesomeAssertions;
using Elarion.Abstractions.Caching;
using Elarion.Caching;
using Elarion.Caching.PostgreSql;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Elarion.Tests.Caching;

public sealed class PostgreSqlHandlerCacheRegistrationTests {
    private const string ConnectionString = "Host=localhost;Database=cache;Username=postgres;Password=postgres";

    [Fact]
    public void AddElarionPostgreSqlHandlerCaching_RegistersPostgresL2AndHandlerCache() {
        var services = new ServiceCollection();

        services.AddElarionPostgreSqlHandlerCaching(ConnectionString);

        services.Should().Contain(d => d.ServiceType == typeof(IDistributedCache));
        services.Should().Contain(d =>
            d.ServiceType == typeof(IHandlerCache) && d.ImplementationType == typeof(HybridHandlerCache));
    }

    [Fact]
    public void AddElarionPostgreSqlHandlerCaching_AppliesUnloggedDefaults() {
        var services = new ServiceCollection();

        services.AddElarionPostgreSqlHandlerCaching(ConnectionString);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<PostgresCacheOptions>>().Value;
        options.ConnectionString.Should().Be(ConnectionString);
        options.SchemaName.Should().Be("public");
        options.TableName.Should().Be("elarion_cache");
        options.CreateIfNotExists.Should().BeTrue();
        // UseWAL = false is what makes the cache table UNLOGGED.
        options.UseWAL.Should().BeFalse();
    }

    [Fact]
    public void AddElarionPostgreSqlHandlerCaching_CallerOverridesDefaults() {
        var services = new ServiceCollection();

        services.AddElarionPostgreSqlHandlerCaching(ConnectionString, options => {
            options.SchemaName = "cache";
            options.TableName = "entries";
            options.CreateIfNotExists = false;
            options.UseWAL = true;
        });

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<PostgresCacheOptions>>().Value;
        options.SchemaName.Should().Be("cache");
        options.TableName.Should().Be("entries");
        options.CreateIfNotExists.Should().BeFalse();
        options.UseWAL.Should().BeTrue();
    }

    [Fact]
    public void AddElarionPostgreSqlHandlerCaching_ActionOverload_TakesConnectionFromCaller() {
        var services = new ServiceCollection();

        services.AddElarionPostgreSqlHandlerCaching(options => options.ConnectionString = ConnectionString);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<PostgresCacheOptions>>().Value;
        options.ConnectionString.Should().Be(ConnectionString);
        options.TableName.Should().Be("elarion_cache");
        options.UseWAL.Should().BeFalse();
    }
}

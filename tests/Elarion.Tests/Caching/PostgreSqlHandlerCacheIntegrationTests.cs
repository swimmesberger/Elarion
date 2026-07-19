using AwesomeAssertions;
using Elarion.Caching.PostgreSql;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Elarion.Tests.Caching;

/// <summary>
/// Round-trip integration tests for <c>AddElarionPostgreSqlHandlerCaching</c> against a real PostgreSQL
/// instance. They prove the cache table is auto-created as an <c>UNLOGGED</c> table (the headline of the
/// recommendation) and that the wired L2 actually stores and serves values. The container is started once
/// per class and the tests skip when Docker is unavailable, so the suite stays green without Docker.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgreSqlHandlerCacheIntegrationTests : IAsyncLifetime {
    private PostgreSqlContainer? _container;
    private bool _available;
    private string _skipReason = "";
    private string _connectionString = "";

    public async ValueTask InitializeAsync() {
        PostgreSqlContainer container;
        try {
            // Build() validates the Docker endpoint, so it must run inside the guard too.
            container = new PostgreSqlBuilder("postgres:17-alpine").Build();
            await container.StartAsync();
        }
        catch (Exception ex) {
            // The only expected failure here is Docker being unavailable; surface it as a skip.
            _skipReason = $"PostgreSQL Testcontainer unavailable (Docker required): {ex.Message}";
            return;
        }

        _container = container;
        _connectionString = container.GetConnectionString();
        _available = true;
    }

    public async ValueTask DisposeAsync() {
        if (_container is not null) await _container.DisposeAsync();
    }

    [Fact]
    public async Task AddElarionPostgreSqlHandlerCaching_CreatesUnloggedTableAndRoundTrips() {
        Assert.SkipUnless(_available, _skipReason);
        var ct = TestContext.Current.CancellationToken;

        var services = new ServiceCollection();
        services.AddElarionPostgreSqlHandlerCaching(_connectionString);
        await using var provider = services.BuildServiceProvider();

        var cache = provider.GetRequiredService<IDistributedCache>();
        // The first operation triggers the lazy DDL (CreateIfNotExists), then round-trips through the L2.
        await cache.SetStringAsync("greeting", "hello", ct);
        (await cache.GetStringAsync("greeting", ct)).Should().Be("hello");

        // The headline guarantee: the cache table is UNLOGGED (relpersistence 'u'), proving UseWAL = false.
        (await GetRelpersistenceAsync("elarion_cache", ct)).Should().Be('u');
    }

    private async Task<char> GetRelpersistenceAsync(string tableName, CancellationToken ct) {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT relpersistence FROM pg_class WHERE relname = @name AND relnamespace = 'public'::regnamespace";
        command.Parameters.AddWithValue("name", tableName);
        var result = await command.ExecuteScalarAsync(ct);
        result.Should().NotBeNull("the cache table should have been created");
        return (char)result!;
    }
}

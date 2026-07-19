using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Elarion.Tests.Migrations;

/// <summary>
/// Docker-gated PostgreSQL fixture for the migration runner integration tests: starts a real PostgreSQL
/// container when Docker is available and skips (never fails) when not. Each test gets its own database
/// so scenarios never see each other's schema or history.
/// </summary>
public sealed class PostgreSqlMigrationsFixture : IAsyncLifetime {
    private PostgreSqlContainer? _container;
    private int _databaseCounter;

    public bool IsAvailable { get; private set; }

    public string SkipReason { get; private set; } = "";

    public async ValueTask InitializeAsync() {
        try {
            var container = new PostgreSqlBuilder("postgres:17-alpine").Build();
            await container.StartAsync();
            _container = container;
            IsAvailable = true;
        }
        catch (Exception ex) {
            SkipReason = $"PostgreSQL Testcontainer unavailable (Docker required): {ex.Message}";
        }
    }

    public async ValueTask DisposeAsync() {
        if (_container is not null) await _container.DisposeAsync();
    }

    /// <summary>Creates a fresh database and returns a connection string targeting it.</summary>
    public async Task<string> CreateDatabaseAsync(CancellationToken cancellationToken) {
        var name = $"mig_test_{Interlocked.Increment(ref _databaseCounter)}";
        await using var connection = new NpgsqlConnection(_container!.GetConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"CREATE DATABASE {name}", connection);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ConnectionString;
    }
}

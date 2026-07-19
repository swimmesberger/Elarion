using Elarion.Sql;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Elarion.Tests.SqlMapping;

/// <summary>
/// Docker-gated PostgreSQL fixture for the SQL-tier <see cref="ISqlSession"/>/<c>SqlUnitOfWork</c> integration
/// tests: starts a real container when Docker is available and skips (never fails) when not. Creates a small
/// widget table once; tests use unique ids so they never collide.
/// </summary>
public sealed class PostgreSqlSqlSessionFixture : IAsyncLifetime {
    private PostgreSqlContainer? _container;
    private NpgsqlDataSource? _dataSource;

    public bool IsAvailable { get; private set; }

    public string SkipReason { get; private set; } = "";

    public string ConnectionString { get; private set; } = "";

    public NpgsqlDataSource DataSource => _dataSource ?? throw new InvalidOperationException("Fixture unavailable.");

    public async ValueTask InitializeAsync() {
        try {
            var container = new PostgreSqlBuilder("postgres:17-alpine").Build();
            await container.StartAsync();
            _container = container;
            ConnectionString = container.GetConnectionString();

            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                """
                CREATE TABLE sql_widgets (
                    id uuid PRIMARY KEY,
                    name text NOT NULL
                );
                """, connection);
            await command.ExecuteNonQueryAsync();

            _dataSource = NpgsqlDataSource.Create(ConnectionString);
            IsAvailable = true;
        }
        catch (Exception ex) {
            SkipReason = $"PostgreSQL Testcontainer unavailable (Docker required): {ex.Message}";
        }
    }

    public async ValueTask DisposeAsync() {
        if (_dataSource is not null) await _dataSource.DisposeAsync();
        if (_container is not null) await _container.DisposeAsync();
    }

    public NpgsqlConnection CreateConnection() {
        return new NpgsqlConnection(ConnectionString);
    }
}

/// <summary>A minimal row for the session/unit-of-work integration tests.</summary>
[SqlRecord("sql_widgets")]
public sealed partial record SqlWidget {
    public required Guid Id { get; init; }

    public required string Name { get; init; }
}

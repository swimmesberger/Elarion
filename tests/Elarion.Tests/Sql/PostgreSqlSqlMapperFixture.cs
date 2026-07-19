using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Elarion.Tests.SqlMapping;

/// <summary>
/// Docker-gated PostgreSQL fixture for the generated-mapper integration tests: starts a real
/// PostgreSQL container when Docker is available and skips (never fails) when not. Creates the test
/// tables once; tests truncate what they use.
/// </summary>
public sealed class PostgreSqlSqlMapperFixture : IAsyncLifetime {
    private PostgreSqlContainer? _container;

    public bool IsAvailable { get; private set; }

    public string SkipReason { get; private set; } = "";

    public string ConnectionString { get; private set; } = "";

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
                CREATE TABLE sql_items (
                    id uuid PRIMARY KEY,
                    name text NOT NULL,
                    note_text text NULL,
                    quantity int NOT NULL,
                    sequence bigint NOT NULL,
                    price numeric(18, 2) NOT NULL,
                    active boolean NOT NULL,
                    created_at timestamptz NOT NULL,
                    status int NOT NULL,
                    previous_status int NULL,
                    payload bytea NULL,
                    due_on date NULL,
                    profile jsonb NULL
                );

                CREATE TABLE sql_positional_row (
                    id uuid PRIMARY KEY,
                    label text NOT NULL,
                    count int NOT NULL
                );
                """, connection);
            await command.ExecuteNonQueryAsync();

            IsAvailable = true;
        }
        catch (Exception ex) {
            SkipReason = $"PostgreSQL Testcontainer unavailable (Docker required): {ex.Message}";
        }
    }

    public async ValueTask DisposeAsync() {
        if (_container is not null) await _container.DisposeAsync();
    }

    public NpgsqlConnection CreateConnection() {
        return new NpgsqlConnection(ConnectionString);
    }
}

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Elarion.Sql;
using Npgsql;
using NpgsqlTypes;
using Testcontainers.PostgreSql;

// The SQL-tier benchmarks exercise the REAL generated COPY members, so the benchmarks assembly opts
// into provider-aware emission like an Npgsql host would (ADR-0068).
[assembly: UseElarionSql(Provider = SqlProvider.Npgsql)]

namespace Elarion.Benchmarks.BulkInsert;

/// <summary>The shared AOT-tier row: generated mapper + COPY members, no EF involvement.</summary>
[SqlRecord("copy_rows")]
public sealed partial class SqlCopyRow {
    public Guid Id { get; set; }

    public string Name { get; set; } = "";

    public string? Description { get; set; }

    public int Quantity { get; set; }

    public long Sequence { get; set; }

    public decimal Price { get; set; }

    public DateTime CreatedAt { get; set; }

    public bool Active { get; set; }
}

// The ADR-0068 parity gate for the AOT SQL tier: the generated binary-COPY path
// (session.ExecuteInsertAsync) must sit at parity with a hand-written NpgsqlBinaryImporter loop, with
// InsertManyAsync (one prepared round trip per row) as the small-batch path it replaces at volume.
//
//   dotnet run --project tests/Elarion.Benchmarks -c Release -- --filter "*SqlCopy*"
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Monitoring, warmupCount: 1, iterationCount: 10, invocationCount: 1)]
public class PostgreSqlSqlCopyInsertBenchmarks {
    [Params(1_000, 10_000, 100_000)] public int Rows { get; set; }

    private PostgreSqlContainer _container = null!;
    private string _connectionString = "";
    private SqlCopyRow[] _rows = [];

    [GlobalSetup]
    public void Setup() {
        _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
        _container.StartAsync().GetAwaiter().GetResult();
        _connectionString = _container.GetConnectionString();

        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        using var command = new NpgsqlCommand(SqlCopyBenchSchema.CreateTable, connection);
        command.ExecuteNonQuery();

        _rows = SqlCopyBenchSchema.NewRows(Rows, 0);
    }

    [GlobalCleanup]
    public void Cleanup() {
        _container.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [IterationSetup]
    public void TruncateTable() {
        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        using var command = new NpgsqlCommand("TRUNCATE TABLE copy_rows", connection);
        command.ExecuteNonQuery();
    }

    [Benchmark(Baseline = true)]
    public async Task<ulong> RawNpgsqlBinaryCopy() {
        // The ceiling: hand-written typed writes, no generated code in the loop.
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var importer = await connection.BeginBinaryImportAsync(
            "COPY copy_rows (id, name, description, quantity, sequence, price, created_at, active) FROM STDIN (FORMAT BINARY)");
        foreach (var row in _rows) {
            await importer.StartRowAsync();
            await importer.WriteAsync(row.Id, NpgsqlDbType.Uuid);
            await importer.WriteAsync(row.Name, NpgsqlDbType.Text);
            if (row.Description is null)
                await importer.WriteNullAsync();
            else
                await importer.WriteAsync(row.Description, NpgsqlDbType.Text);

            await importer.WriteAsync(row.Quantity, NpgsqlDbType.Integer);
            await importer.WriteAsync(row.Sequence, NpgsqlDbType.Bigint);
            await importer.WriteAsync(row.Price, NpgsqlDbType.Numeric);
            await importer.WriteAsync(row.CreatedAt, NpgsqlDbType.TimestampTz);
            await importer.WriteAsync(row.Active, NpgsqlDbType.Boolean);
        }

        return await importer.CompleteAsync();
    }

    [Benchmark]
    public async Task<long> SqlExecuteInsertCopy() {
        await using var connection = new NpgsqlConnection(_connectionString);
        var session = connection.AsSqlSession();
        return await session.ExecuteInsertAsync(_rows);
    }

    [Benchmark]
    public async Task<int> SqlInsertMany() {
        // The tier's previous batch ceiling: one reused prepared command, one round trip per row.
        await using var connection = new NpgsqlConnection(_connectionString);
        var session = connection.AsSqlSession();
        return await session.InsertManyAsync(_rows);
    }
}

// The upsert-shaped gate: the motivating workload (dirty-flag-and-sweep persistence) merges into
// EXISTING rows, so the staged temp-table + ON CONFLICT path is measured against a hand-written
// staged loop and the prepared-command ON CONFLICT alternative, into a pre-populated table.
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Monitoring, warmupCount: 1, iterationCount: 10, invocationCount: 1)]
public class PostgreSqlSqlCopyUpsertBenchmarks {
    [Params(1_000, 10_000, 50_000)] public int Rows { get; set; }

    private PostgreSqlContainer _container = null!;
    private string _connectionString = "";
    private SqlCopyRow[] _baseline = [];
    private SqlCopyRow[] _updated = [];

    private static readonly SqlBulkInsertOptions UpsertOptions = new() {
        OnConflict = SqlBulkInsertConflictBehavior.Update,
        ConflictColumns = ["id"],
    };

    [GlobalSetup]
    public void Setup() {
        _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
        _container.StartAsync().GetAwaiter().GetResult();
        _connectionString = _container.GetConnectionString();

        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        using var command = new NpgsqlCommand(SqlCopyBenchSchema.CreateTable, connection);
        command.ExecuteNonQuery();

        _baseline = SqlCopyBenchSchema.NewRows(Rows, 0);
        _updated = [
            .. _baseline.Select(row => new SqlCopyRow {
                Id = row.Id,
                Name = row.Name + "-v2",
                Description = row.Description,
                Quantity = row.Quantity + 100,
                Sequence = row.Sequence + 1,
                Price = row.Price + 0.5m,
                CreatedAt = row.CreatedAt.AddSeconds(30),
                Active = !row.Active
            })
        ];
    }

    [GlobalCleanup]
    public void Cleanup() {
        _container.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [IterationSetup]
    public void RepopulateTable() {
        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        using (var truncate = new NpgsqlCommand("TRUNCATE TABLE copy_rows", connection)) {
            truncate.ExecuteNonQuery();
        }

        var session = connection.AsSqlSession();
        session.ExecuteInsertAsync(_baseline).GetAwaiter().GetResult();
    }

    [Benchmark(Baseline = true)]
    public async Task<long> RawStagedUpsert() {
        // The ceiling: hand-written staging — CREATE TEMP, binary COPY into it, one ON CONFLICT merge.
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using (var create = new NpgsqlCommand(
                         "CREATE TEMP TABLE stage AS SELECT id, name, description, quantity, sequence, price, created_at, active FROM copy_rows WITH NO DATA",
                         connection)) {
            await create.ExecuteNonQueryAsync();
        }

        await using (var importer = await connection.BeginBinaryImportAsync(
                         "COPY stage (id, name, description, quantity, sequence, price, created_at, active) FROM STDIN (FORMAT BINARY)")) {
            foreach (var row in _updated) {
                await importer.StartRowAsync();
                await importer.WriteAsync(row.Id, NpgsqlDbType.Uuid);
                await importer.WriteAsync(row.Name, NpgsqlDbType.Text);
                if (row.Description is null)
                    await importer.WriteNullAsync();
                else
                    await importer.WriteAsync(row.Description, NpgsqlDbType.Text);

                await importer.WriteAsync(row.Quantity, NpgsqlDbType.Integer);
                await importer.WriteAsync(row.Sequence, NpgsqlDbType.Bigint);
                await importer.WriteAsync(row.Price, NpgsqlDbType.Numeric);
                await importer.WriteAsync(row.CreatedAt, NpgsqlDbType.TimestampTz);
                await importer.WriteAsync(row.Active, NpgsqlDbType.Boolean);
            }

            await importer.CompleteAsync();
        }

        await using var merge = new NpgsqlCommand(
            "INSERT INTO copy_rows (id, name, description, quantity, sequence, price, created_at, active) "
            + "SELECT id, name, description, quantity, sequence, price, created_at, active FROM stage "
            + "ON CONFLICT (id) DO UPDATE SET name = EXCLUDED.name, description = EXCLUDED.description, "
            + "quantity = EXCLUDED.quantity, sequence = EXCLUDED.sequence, price = EXCLUDED.price, "
            + "created_at = EXCLUDED.created_at, active = EXCLUDED.active",
            connection);
        return await merge.ExecuteNonQueryAsync();
    }

    [Benchmark]
    public async Task<long> SqlExecuteInsertUpsert() {
        await using var connection = new NpgsqlConnection(_connectionString);
        var session = connection.AsSqlSession();
        return await session.ExecuteInsertAsync(_updated, UpsertOptions);
    }

    [Benchmark]
    public async Task<int> SqlInsertManyOnConflict() {
        await using var connection = new NpgsqlConnection(_connectionString);
        var session = connection.AsSqlSession();
        return await session.InsertManyAsync(_updated,
            " ON CONFLICT (id) DO UPDATE SET name = EXCLUDED.name, description = EXCLUDED.description, "
            + "quantity = EXCLUDED.quantity, sequence = EXCLUDED.sequence, price = EXCLUDED.price, "
            + "created_at = EXCLUDED.created_at, active = EXCLUDED.active");
    }
}

internal static class SqlCopyBenchSchema {
    public const string CreateTable =
        """
        CREATE TABLE copy_rows (
            id uuid PRIMARY KEY,
            name text NOT NULL,
            description text NULL,
            quantity int NOT NULL,
            sequence bigint NOT NULL,
            price numeric(18, 2) NOT NULL,
            created_at timestamptz NOT NULL,
            active boolean NOT NULL
        );
        """;

    public static SqlCopyRow[] NewRows(int count, int offset) {
        var baseInstant = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);
        return [
            .. Enumerable.Range(offset, count).Select(i => new SqlCopyRow {
                Id = Guid.CreateVersion7(),
                Name = $"row-{i}",
                Description = i % 3 == 0 ? null : $"description of row {i} with some payload text",
                Quantity = i,
                Sequence = i * 37L,
                Price = 10.25m + i % 1000,
                CreatedAt = baseInstant.AddSeconds(i),
                Active = i % 2 == 0
            })
        ];
    }
}

using BenchmarkDotNet.Attributes;
using Dapper;
using Elarion.Sql;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Testcontainers.PostgreSql;

namespace Elarion.Benchmarks.SqlMapping;

// SQL row mapping against real PostgreSQL (Testcontainers; requires a Docker-compatible runtime),
// gating ADR-0058: the generated [SqlRecord] mapper must sit at parity with a hand-written ADO.NET
// reader in time AND allocations, with Dapper, Dapper.AOT, and EF Core (no-tracking) as the
// comparison columns.
//
//   dotnet run --project tests/Elarion.Benchmarks -c Release -- --filter "*SqlMapping*"
//
// Read path: one SELECT of `Rows` rows materialized into a List<BenchReadRow> per op.
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class PostgreSqlSqlMappingReadBenchmarks {
    [Params(1_000, 100_000)]
    public int Rows { get; set; }

    private PostgreSqlContainer _container = null!;
    private NpgsqlConnection _connection = null!;
    private BenchReadContext _context = null!;

    private const string SelectSql =
        "SELECT id, name, description, quantity, sequence, price, created_at, active FROM read_rows";

    // Dapper.AOT binds by member name at the call site; the aliases make the mapping explicit
    // (classic Dapper instead uses DefaultTypeMap.MatchNamesWithUnderscores, its idiomatic setup).
    private const string AliasedSelectSql =
        "SELECT id AS Id, name AS Name, description AS Description, quantity AS Quantity, "
        + "sequence AS Sequence, price AS Price, created_at AS CreatedAt, active AS Active FROM read_rows";

    [GlobalSetup]
    public void Setup() {
        _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
        _container.StartAsync().GetAwaiter().GetResult();

        DefaultTypeMap.MatchNamesWithUnderscores = true;

        _connection = new NpgsqlConnection(_container.GetConnectionString());
        _connection.Open();
        BenchReadRows.CreateAndSeed(_connection, Rows);

        var builder = new DbContextOptionsBuilder<BenchReadContext>();
        builder.UseNpgsql(_container.GetConnectionString());
        _context = new BenchReadContext(builder.Options);
    }

    [GlobalCleanup]
    public void Cleanup() {
        _context.Dispose();
        _connection.Dispose();
        _container.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [Benchmark(Baseline = true)]
    public async Task<List<BenchReadRow>> HandWrittenAdoNet() {
        // The ceiling: hard-coded ordinals, typed synchronous reads.
        await using var command = new NpgsqlCommand(SelectSql, _connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<BenchReadRow>();
        while (await reader.ReadAsync()) {
            rows.Add(new BenchReadRow {
                Id = reader.GetFieldValue<Guid>(0),
                Name = reader.GetFieldValue<string>(1),
                Description = reader.IsDBNull(2) ? null : reader.GetFieldValue<string>(2),
                Quantity = reader.GetFieldValue<int>(3),
                Sequence = reader.GetFieldValue<long>(4),
                Price = reader.GetFieldValue<decimal>(5),
                CreatedAt = reader.GetFieldValue<DateTime>(6),
                Active = reader.GetFieldValue<bool>(7),
            });
        }

        return rows;
    }

    [Benchmark]
    public async Task<List<BenchReadRow>> ElarionSqlMapper() {
        // The ADR-0058 parity gate: generated mapper over a plain command, like the hand-written reader.
        await using var command = new NpgsqlCommand(SelectSql, _connection);
        await using var reader = await command.ExecuteReaderAsync();
        return await BenchReadRowSqlMapper.Instance.ReadAllAsync(reader);
    }

    [Benchmark]
    public Task<List<BenchReadRow>> ElarionQueryAsync() =>
        // The product path end-to-end: self-mapping (BenchReadRow resolves its own mapper) +
        // interpolated statement build + connection extension.
        _connection.QueryAsync<BenchReadRow>($"{BenchReadRow.Select}");

    [Benchmark]
    [DapperAot(false)]
    public async Task<List<BenchReadRow>> Dapper() =>
        (await _connection.QueryAsync<BenchReadRow>(SelectSql)).AsList();

    [Benchmark]
    [DapperAot]
    public async Task<List<BenchReadRow>> DapperAot() =>
        (await _connection.QueryAsync<BenchReadRow>(AliasedSelectSql)).AsList();

    [Benchmark]
    public Task<List<BenchReadRow>> EfCoreNoTracking() =>
        _context.Rows.AsNoTracking().ToListAsync();
}

// Single-row path: one query by id per op — this is where per-call abstraction overhead
// (statement build, parameter binding, command setup) shows, if any.
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class PostgreSqlSqlMappingSingleRowBenchmarks {
    private PostgreSqlContainer _container = null!;
    private NpgsqlConnection _connection = null!;
    private BenchReadContext _context = null!;
    private Guid _targetId;

    private const string SelectOneSql =
        "SELECT id, name, description, quantity, sequence, price, created_at, active FROM read_rows WHERE id = @id";

    private const string AliasedSelectOneSql =
        "SELECT id AS Id, name AS Name, description AS Description, quantity AS Quantity, "
        + "sequence AS Sequence, price AS Price, created_at AS CreatedAt, active AS Active "
        + "FROM read_rows WHERE id = @id";

    [GlobalSetup]
    public void Setup() {
        _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
        _container.StartAsync().GetAwaiter().GetResult();

        DefaultTypeMap.MatchNamesWithUnderscores = true;

        _connection = new NpgsqlConnection(_container.GetConnectionString());
        _connection.Open();
        _targetId = BenchReadRows.CreateAndSeed(_connection, 1_000);

        var builder = new DbContextOptionsBuilder<BenchReadContext>();
        builder.UseNpgsql(_container.GetConnectionString());
        _context = new BenchReadContext(builder.Options);
    }

    [GlobalCleanup]
    public void Cleanup() {
        _context.Dispose();
        _connection.Dispose();
        _container.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [Benchmark(Baseline = true)]
    public async Task<BenchReadRow?> HandWrittenAdoNet() {
        await using var command = new NpgsqlCommand(SelectOneSql, _connection);
        command.Parameters.AddWithValue("id", _targetId);
        await using var reader = await command.ExecuteReaderAsync(System.Data.CommandBehavior.SingleRow);
        if (!await reader.ReadAsync()) {
            return null;
        }

        return new BenchReadRow {
            Id = reader.GetFieldValue<Guid>(0),
            Name = reader.GetFieldValue<string>(1),
            Description = reader.IsDBNull(2) ? null : reader.GetFieldValue<string>(2),
            Quantity = reader.GetFieldValue<int>(3),
            Sequence = reader.GetFieldValue<long>(4),
            Price = reader.GetFieldValue<decimal>(5),
            CreatedAt = reader.GetFieldValue<DateTime>(6),
            Active = reader.GetFieldValue<bool>(7),
        };
    }

    [Benchmark]
    public async Task<BenchReadRow?> ElarionSqlMapper() {
        // Parity gate: plain command + generated BindParameters/Read, like the hand-written variant.
        await using var command = new NpgsqlCommand(SelectOneSql, _connection);
        var parameter = command.CreateParameter();
        parameter.ParameterName = BenchReadRowSqlMapper.Columns.Id;
        parameter.Value = _targetId;
        command.Parameters.Add(parameter);
        await using var reader = await command.ExecuteReaderAsync(System.Data.CommandBehavior.SingleRow);
        return await reader.ReadAsync() ? BenchReadRowSqlMapper.Instance.Read(reader) : null;
    }

    [Benchmark]
    public Task<BenchReadRow?> ElarionQueryAsync() =>
        // The product path end-to-end: self-mapping + interpolated statement (one parameter).
        _connection.QueryFirstOrDefaultAsync<BenchReadRow>(
            $"{BenchReadRow.Select} WHERE id = {_targetId}");

    [Benchmark]
    [DapperAot(false)]
    public Task<BenchReadRow?> Dapper() =>
        _connection.QueryFirstOrDefaultAsync<BenchReadRow>(SelectOneSql, new { id = _targetId });

    [Benchmark]
    [DapperAot]
    public Task<BenchReadRow?> DapperAot() =>
        _connection.QueryFirstOrDefaultAsync<BenchReadRow>(AliasedSelectOneSql, new { id = _targetId });

    [Benchmark]
    public Task<BenchReadRow?> EfCoreNoTracking() {
        var id = _targetId;
        return _context.Rows.AsNoTracking().FirstOrDefaultAsync(row => row.Id == id);
    }
}

/// <summary>
/// The shared row type: generated Elarion mapper, Dapper target, and EF entity at once. The timestamp
/// is a UTC <see cref="DateTime"/> rather than <see cref="DateTimeOffset"/> because Dapper.AOT's
/// generated row factory Convert-casts the provider value and cannot read Npgsql timestamptz into
/// DateTimeOffset — the column would crash (the Elarion mapper reads either via typed GetFieldValue).
/// </summary>
[SqlRecord("read_rows")]
public sealed partial class BenchReadRow {
    public Guid Id { get; set; }

    public string Name { get; set; } = "";

    public string? Description { get; set; }

    public int Quantity { get; set; }

    public long Sequence { get; set; }

    public decimal Price { get; set; }

    public DateTime CreatedAt { get; set; }

    public bool Active { get; set; }
}

public sealed class BenchReadContext(DbContextOptions<BenchReadContext> options) : DbContext(options) {
    public DbSet<BenchReadRow> Rows => Set<BenchReadRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<BenchReadRow>(builder => {
            builder.ToTable("read_rows");
            builder.HasKey(row => row.Id);
            builder.Property(row => row.Id).HasColumnName("id").ValueGeneratedNever();
            builder.Property(row => row.Name).HasColumnName("name");
            builder.Property(row => row.Description).HasColumnName("description");
            builder.Property(row => row.Quantity).HasColumnName("quantity");
            builder.Property(row => row.Sequence).HasColumnName("sequence");
            builder.Property(row => row.Price).HasColumnName("price").HasPrecision(18, 2);
            builder.Property(row => row.CreatedAt).HasColumnName("created_at");
            builder.Property(row => row.Active).HasColumnName("active");
        });
}

internal static class BenchReadRows {
    /// <summary>Creates and seeds the read table via binary COPY; returns a mid-table row id.</summary>
    public static Guid CreateAndSeed(NpgsqlConnection connection, int rows) {
        using (var create = new NpgsqlCommand(
                   """
                   DROP TABLE IF EXISTS read_rows;
                   CREATE TABLE read_rows (
                       id uuid PRIMARY KEY,
                       name text NOT NULL,
                       description text NULL,
                       quantity int NOT NULL,
                       sequence bigint NOT NULL,
                       price numeric(18, 2) NOT NULL,
                       created_at timestamptz NOT NULL,
                       active boolean NOT NULL
                   );
                   """, connection)) {
            create.ExecuteNonQuery();
        }

        var baseInstant = new DateTime(2026, 7, 13, 12, 0, 0, DateTimeKind.Utc);
        var targetId = Guid.Empty;
        using var importer = connection.BeginBinaryImport(
            "COPY read_rows (id, name, description, quantity, sequence, price, created_at, active) FROM STDIN (FORMAT BINARY)");
        for (var i = 0; i < rows; i++) {
            var id = Guid.CreateVersion7();
            if (i == rows / 2) {
                targetId = id;
            }

            importer.StartRow();
            importer.Write(id, NpgsqlDbType.Uuid);
            importer.Write($"row-{i}", NpgsqlDbType.Text);
            if (i % 3 == 0) {
                importer.WriteNull();
            }
            else {
                importer.Write($"description of row {i} with some payload text", NpgsqlDbType.Text);
            }

            importer.Write(i, NpgsqlDbType.Integer);
            importer.Write(i * 37L, NpgsqlDbType.Bigint);
            importer.Write(10.25m + (i % 1000), NpgsqlDbType.Numeric);
            importer.Write(baseInstant.AddSeconds(i), NpgsqlDbType.TimestampTz);
            importer.Write(i % 2 == 0, NpgsqlDbType.Boolean);
        }

        importer.Complete();
        return targetId;
    }
}

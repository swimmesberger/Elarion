using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using EFCore.BulkExtensions;
using Elarion.BulkOperations.PostgreSql;
using Elarion.EntityFrameworkCore.BulkOperations;
using LinqToDB.Data;
using LinqToDB.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using PhenX.EntityFrameworkCore.BulkInsert.Extensions;
using PhenX.EntityFrameworkCore.BulkInsert.PostgreSql;
using Testcontainers.PostgreSql;

namespace Elarion.Benchmarks.BulkInsert;

// Bulk insert into real PostgreSQL (Testcontainers; requires a Docker-compatible runtime), comparing
// Elarion's ExecuteInsertAsync (binary COPY) against a naive SaveChanges baseline, the comparable
// bulk libraries, and a hand-written NpgsqlBinaryImporter loop as the theoretical ceiling.
//
//   dotnet run --project tests/Elarion.Benchmarks -c Release -- --filter "*BulkInsert*"
//
// Each measurement is one whole insert of `Rows` rows into a truncated table (RunStrategy.Monitoring,
// one op per iteration — the operations are milliseconds-to-seconds scale, so per-op noise is small
// relative to the effect sizes of interest).
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Monitoring, warmupCount: 1, iterationCount: 10, invocationCount: 1)]
public class PostgreSqlBulkInsertBenchmarks {
    [Params(1_000, 10_000, 100_000)] public int Rows { get; set; }

    private PostgreSqlContainer _container = null!;
    private string _connectionString = "";
    private BenchDbContext _context = null!;
    private BenchRow[] _rows = [];

    [GlobalSetup]
    public void Setup() {
        _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
        _container.StartAsync().GetAwaiter().GetResult();
        _connectionString = _container.GetConnectionString();

        LinqToDBForEFTools.Initialize();

        _context = CreateContext();
        _context.Database.EnsureCreated();

        var baseInstant = new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
        _rows = [
            .. Enumerable.Range(0, Rows).Select(i => new BenchRow {
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

    [GlobalCleanup]
    public void Cleanup() {
        _context.Dispose();
        _container.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [IterationSetup]
    public void TruncateTable() {
        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        using var command = new NpgsqlCommand("TRUNCATE TABLE bench_rows", connection);
        command.ExecuteNonQuery();
    }

    [Benchmark(Baseline = true)]
    public async Task<int> EfCoreSaveChanges() {
        // The naive floor: change-tracked AddRange + SaveChanges on a fresh context, as an
        // application without a bulk path would write it.
        await using var context = CreateContext();
        context.Rows.AddRange(_rows);
        return await context.SaveChangesAsync();
    }

    [Benchmark]
    public Task<long> ElarionExecuteInsert() {
        return _context.Rows.ExecuteInsertAsync(_rows);
    }

    [Benchmark]
    public Task BulkExtensionsMit() {
        return _context.BulkInsertAsync(_rows);
    }

    [Benchmark]
    public Task<BulkCopyRowsCopied> Linq2DbBulkCopy() {
        return _context.BulkCopyAsync(
            new BulkCopyOptions { BulkCopyType = LinqToDB.Data.BulkCopyType.ProviderSpecific }, _rows);
    }

    [Benchmark]
    public Task PhenXBulkInsert() {
        return _context.Rows.ExecuteBulkInsertAsync(_rows);
    }

    [Benchmark]
    public async Task<ulong> RawNpgsqlBinaryCopy() {
        // The ceiling: hand-written typed writes, no EF metadata in the loop.
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var importer = await connection.BeginBinaryImportAsync(
            """COPY bench_rows ("Id", "Name", "Description", "Quantity", "Sequence", "Price", "CreatedAt", "Active") FROM STDIN (FORMAT BINARY)""");
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

    private BenchDbContext CreateContext() {
        var builder = new DbContextOptionsBuilder<BenchDbContext>();
        builder.UseNpgsql(_connectionString)
            .UseElarionPostgreSqlBulkOperations()
            .UseBulkInsertPostgreSql();
        return new BenchDbContext(builder.Options);
    }
}

public sealed class BenchDbContext(DbContextOptions<BenchDbContext> options) : DbContext(options) {
    public DbSet<BenchRow> Rows => Set<BenchRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        modelBuilder.Entity<BenchRow>(builder => {
            builder.ToTable("bench_rows");
            builder.HasKey(row => row.Id);
            builder.Property(row => row.Id).ValueGeneratedNever();
            builder.Property(row => row.Price).HasPrecision(18, 2);
        });
    }
}

public sealed class BenchRow {
    public Guid Id { get; set; }

    public string Name { get; set; } = "";

    public string? Description { get; set; }

    public int Quantity { get; set; }

    public long Sequence { get; set; }

    public decimal Price { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public bool Active { get; set; }
}

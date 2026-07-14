using BenchmarkDotNet.Attributes;
using Elarion.Sql;

namespace Elarion.Benchmarks.SqlMapping;

// DB-free microbenchmark of the statement-BUILDING path (ADR-0058 API redesign regression gate):
// interpolation → parameter binding → text/parameter materialization, with no database in the loop,
// so it isolates exactly the allocation and time the redesign touches. The full read-path parity gate
// stays in PostgreSqlSqlMappingBenchmarks (real PostgreSQL); this one is the fast, deterministic
// detector for "did the SqlStatement/SqlStatement rework regress statement building".
//
//   dotnet run --project tests/Elarion.Benchmarks -c Release -- --filter "*SqlStatementBuilding*"
[MemoryDiagnoser]
[SimpleJob(warmupCount: 5, iterationCount: 10)]
public class SqlStatementBuildingBenchmarks {
    private readonly Guid _id = Guid.CreateVersion7();
    private readonly string _metric = "temperature";
    private readonly Guid[] _ids = [.. Enumerable.Range(0, 8).Select(_ => Guid.CreateVersion7())];

    // The generated column-list / table fragments (before: two separate identifier splices; the
    // old {Columns.All:raw} + {TableName:raw} shape, now expressed via Verbatim since :raw was removed).
    private static readonly SqlStatement ColumnsAll =
        SqlStatement.Verbatim("device_id, metric, recorded_at, value, meta");
    private static readonly SqlStatement TableName = SqlStatement.Verbatim("readings");

    // The redesign's generated Row.Select — a cached pure-literal fragment combining SELECT-list + FROM,
    // so ONE {Row.Select} hole replaces the two column-list + table holes.
    private static readonly SqlStatement SelectPrefix =
        SqlStatement.Verbatim("SELECT device_id, metric, recorded_at, value, meta FROM readings");

    // BEFORE — the two-identifier-splice shape: two Verbatim fragments + two value holes.
    [Benchmark(Baseline = true)]
    public int TwoIdentifierSplices() {
        var sql = new SqlStatement(
            $"SELECT {ColumnsAll} FROM {TableName} WHERE device_id = {_id} AND metric = {_metric} ORDER BY recorded_at DESC LIMIT 1");
        return sql.Text.Length + sql.ParameterValues.Count;
    }

    // AFTER — one cached fragment splice + two value holes. Must be <= the two-splice path.
    [Benchmark]
    public int OneFragmentSplice() {
        var sql = new SqlStatement(
            $"{SelectPrefix} WHERE device_id = {_id} AND metric = {_metric} ORDER BY recorded_at DESC LIMIT 1");
        return sql.Text.Length + sql.ParameterValues.Count;
    }

    // IN-list expansion — the collection hole path (unchanged by the redesign; regression sentinel).
    [Benchmark]
    public int InList() {
        var sql = new SqlStatement($"{SelectPrefix} WHERE device_id IN {_ids}");
        return sql.Text.Length + sql.ParameterValues.Count;
    }
}

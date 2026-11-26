using BenchmarkDotNet.Attributes;
using Elarion.Spreadsheets;

namespace Elarion.Benchmarks.Spreadsheets;

// Spreadsheet records (ADR-0108, spec 0003): the forward-only writer and the reader. The write benchmark streams
// pre-built records to Stream.Null, so its allocation column is the writer's own: SC-005 holds when 100,000 records
// allocate about as much as 1,000 (the writer keeps only its width sample, never the rows it has written). The read
// benchmarks parse a 10,000-row workbook written once in setup, as records and as plain rows.
//
//   dotnet run --project tests/Elarion.Benchmarks -c Release -- --filter "*SpreadsheetWrite*"
//   dotnet run --project tests/Elarion.Benchmarks -c Release -- --filter "*SpreadsheetRead*"
[MemoryDiagnoser]
public class SpreadsheetWriteBenchmarks {
    private BenchmarkSheetRow[] _records = [];

    [Params(1_000, 100_000)]
    public int Records { get; set; }

    [GlobalSetup]
    public void Setup() {
        _records = BenchmarkSheetRow.Create(Records);
    }

    [Benchmark]
    public async Task WriteToNullStream() {
        await SpreadsheetWorkbook.WriteAsync(Stream.Null, _records);
    }
}

[MemoryDiagnoser]
public class SpreadsheetReadBenchmarks {
    private const int Rows = 10_000;

    private readonly SpreadsheetReader _reader = new();
    private byte[] _workbook = [];

    [GlobalSetup]
    public async Task Setup() {
        using var stream = new MemoryStream();
        await SpreadsheetWorkbook.WriteAsync(stream, BenchmarkSheetRow.Create(Rows));
        _workbook = stream.ToArray();
    }

    [Benchmark]
    public async Task<int> ReadRecords() {
        var result = await _reader.ReadAsync<BenchmarkSheetRow>(new MemoryStream(_workbook, writable: false));
        return result.Rows.Count;
    }

    [Benchmark]
    public async Task<int> ReadPlainRows() {
        var result = await _reader.ReadRowsAsync(new MemoryStream(_workbook, writable: false));
        return result.Rows.Count;
    }
}

/// <summary>A row with the usual column kinds: text, a whole number, a decimal number, a date and time, a flag.</summary>
[SpreadsheetRecord("Readings")]
public sealed partial class BenchmarkSheetRow {
    public required string Device { get; init; }

    public required int Sequence { get; init; }

    public required double Value { get; init; }

    public required DateTime RecordedAt { get; init; }

    public required bool Calibrated { get; init; }

    public static BenchmarkSheetRow[] Create(int count) {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var records = new BenchmarkSheetRow[count];
        for (var i = 0; i < count; i++) {
            records[i] = new BenchmarkSheetRow {
                Device = $"device-{i % 64:D2}",
                Sequence = i,
                Value = i * 0.25,
                RecordedAt = start.AddSeconds(i),
                Calibrated = i % 3 == 0
            };
        }

        return records;
    }
}

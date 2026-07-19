using Microsoft.Data.Sqlite;

namespace Elarion.Tests.Migrations;

/// <summary>
/// File-based SQLite fixture for the migration runner tests. SQLite runs in-process (no Docker), so these
/// tests always run. Each test gets its own database file so scenarios never see each other's schema or
/// history; a temp directory holds them and is deleted on dispose.
/// </summary>
public sealed class SqliteMigrationsFixture : IDisposable {
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "elarion_sqlite_mig_" + Guid.CreateVersion7().ToString("N"));

    private int _counter;

    public SqliteMigrationsFixture() {
        Directory.CreateDirectory(_directory);
    }

    /// <summary>Returns a connection string for a fresh, empty database file.</summary>
    public string CreateConnectionString() {
        var path = Path.Combine(_directory, $"mig_{Interlocked.Increment(ref _counter)}.db");
        return new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ConnectionString;
    }

    public void Dispose() {
        try {
            Directory.Delete(_directory, true);
        }
        catch (IOException) {
            // Best-effort temp cleanup; the OS reclaims it regardless.
        }
    }
}

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Elarion.Tests.Settings;

/// <summary>
/// File-based SQLite fixture for the EF Core settings-store test bases. SQLite runs in-process (no Docker),
/// so these tests always run; a temp database file holds the shared settings schema and is deleted on
/// dispose. Pooling is off so each context's connection closes for real instead of holding the file.
/// </summary>
public sealed class SqliteSettingsStoreFixture : IAsyncLifetime, ISettingsStoreFixture {
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "elarion_sqlite_settings_" + Guid.CreateVersion7().ToString("N") + ".db");

    private string ConnectionString =>
        new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ConnectionString;

    /// <inheritdoc />
    public bool IsAvailable => true;

    /// <inheritdoc />
    public string SkipReason => "";

    public async ValueTask InitializeAsync() {
        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync();
    }

    public ValueTask DisposeAsync() {
        try {
            File.Delete(_path);
        }
        catch (IOException) {
            // Best-effort temp cleanup; the OS reclaims it regardless.
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public SettingsIntegrationDbContext CreateContext() {
        return new SettingsIntegrationDbContext(new DbContextOptionsBuilder<SettingsIntegrationDbContext>()
            .UseSqlite(ConnectionString)
            .Options);
    }
}

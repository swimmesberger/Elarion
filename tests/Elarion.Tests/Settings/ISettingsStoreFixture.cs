namespace Elarion.Tests.Settings;

/// <summary>
/// Provider-neutral surface the shared EF Core settings-store test bases run against. One fixture per
/// database provider supplies contexts bound to a ready settings schema, or a skip reason when the backing
/// database cannot start (for example Docker missing for the PostgreSQL container).
/// </summary>
public interface ISettingsStoreFixture {
    /// <summary>Gets a value indicating whether the database started and the settings schema is ready.</summary>
    bool IsAvailable { get; }

    /// <summary>Gets the reason the tests are skipped when <see cref="IsAvailable"/> is false.</summary>
    string SkipReason { get; }

    /// <summary>Creates a fresh context bound to the database, so each test owns its own connection.</summary>
    SettingsIntegrationDbContext CreateContext();
}

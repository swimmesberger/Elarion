using Xunit;

namespace Elarion.Tests.Settings;

/// <summary>
/// Runs the shared <see cref="EfCoreSettingsStoreTestBase"/> contract against real SQLite (in-process, no
/// Docker, so this suite always runs). The store's raw statements are built from the EF model and rely on
/// <c>UPDATE … RETURNING</c>, which SQLite supports since 3.35 — this suite proves the store is genuinely
/// provider-portable rather than PostgreSQL-shaped by accident.
/// </summary>
public sealed class SqliteEfCoreSettingsStoreIntegrationTests(SqliteSettingsStoreFixture fixture)
    : EfCoreSettingsStoreTestBase(fixture), IClassFixture<SqliteSettingsStoreFixture>;

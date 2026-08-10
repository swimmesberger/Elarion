using Xunit;

namespace Elarion.Tests.Settings;

/// <summary>
/// Runs the shared <see cref="EfCoreSettingsStoreTestBase"/> contract against a real PostgreSQL instance in a
/// Testcontainers container. Skips when Docker is unavailable.
/// </summary>
[Trait("Category", "Integration")]
public sealed class EfCoreSettingsStoreIntegrationTests(PostgreSqlSettingsStoreFixture fixture)
    : EfCoreSettingsStoreTestBase(fixture), IClassFixture<PostgreSqlSettingsStoreFixture>;

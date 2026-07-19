using Microsoft.Extensions.Logging;

namespace Elarion.Migrations;

/// <summary>
/// The provider-registered factory the neutral <c>AddElarionMigrations</c> resolves to build its
/// <see cref="IMigrationDatabase"/> (ADR-0060). A provider package (for example the PostgreSQL binding in
/// <c>Elarion.Sql.PostgreSql</c>) registers one — capturing the data source and any engine-specific settings
/// chosen at provider registration — so migration wiring stays database-neutral: the host configures the
/// provider once and calls the neutral <c>AddElarionMigrations(configure)</c> with only the script sources and
/// neutral options.
/// </summary>
public interface IMigrationDatabaseFactory {
    /// <summary>
    /// Builds the migration database over the provider's captured data source and the neutral
    /// <paramref name="options"/> (history-table name, timeouts) configured at the migration registration.
    /// </summary>
    IMigrationDatabase Create(MigrationOptions options, ILogger? logger);
}

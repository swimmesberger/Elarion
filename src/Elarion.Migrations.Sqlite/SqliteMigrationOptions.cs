using Elarion.Migrations;

namespace Elarion.Migrations.Sqlite;

/// <summary>
/// Options for the SQLite migration provider (ADR-0060), extending the neutral
/// <see cref="MigrationOptions"/>. SQLite adds no engine-specific knobs: it has no advisory-lock key
/// (runners serialize on a connection-held exclusive file lock) and no server-side statement timeout, so
/// <see cref="MigrationOptions.CommandTimeout"/> does not apply — <see cref="MigrationOptions.LockTimeout"/>
/// governs how long a contended runner waits for the exclusive lock.
/// </summary>
public sealed class SqliteMigrationOptions : MigrationOptions {
}

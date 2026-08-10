using Xunit;

namespace Elarion.Tests.Settings;

/// <summary>
/// Runs the shared <see cref="EfCoreSettingsStoreTransactionTestBase"/> contract against real SQLite
/// (in-process, no Docker, so this suite always runs): the store's ambient-transaction enlistment — including
/// the raw INSERT and <c>UPDATE … RETURNING</c> paths — must commit and roll back with the caller's
/// transaction on SQLite exactly as it does on PostgreSQL.
/// </summary>
public sealed class SqliteEfCoreSettingsStoreTransactionTests(SqliteSettingsStoreFixture fixture)
    : EfCoreSettingsStoreTransactionTestBase(fixture), IClassFixture<SqliteSettingsStoreFixture>;

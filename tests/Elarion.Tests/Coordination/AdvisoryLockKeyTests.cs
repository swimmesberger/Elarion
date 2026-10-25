using AwesomeAssertions;
using Elarion.Abstractions.Coordination;
using Elarion.Devices.EntityFrameworkCore;
using Elarion.EntityFrameworkCore.UnitOfWork;
using Elarion.Tests.Pipeline;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Elarion.Tests.Coordination;

public sealed class AdvisoryLockKeyTests {
    [Fact]
    public void Text_JoinsScopeAndKeyWithTheSeparator() {
        AdvisoryLockKey.Text("billing-account", "42").Should().Be("billing-account:42");
    }

    [Theory]
    [InlineData("")]
    [InlineData("a:b")]
    [InlineData("elarion.scheduler.job")]
    [InlineData("elarion.mine")]
    public void Text_RejectsEmptyAmbiguousAndReservedScopes(string scope) {
        var act = () => AdvisoryLockKey.Text(scope, "42");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void FrameworkScopes_AreReservedAndFormatLikeApplicationScopes() {
        AdvisoryLockKey.Scopes.SchedulerJob.Should().StartWith(AdvisoryLockKey.ReservedScopePrefix);
        AdvisoryLockKey.Scopes.DevicePairing.Should().StartWith(AdvisoryLockKey.ReservedScopePrefix);
        AdvisoryLockKey.FrameworkText(AdvisoryLockKey.Scopes.SchedulerJob, "nightly")
            .Should().Be("elarion.scheduler.job:nightly");
    }

    [Fact]
    public void DevicePairingStore_LocksTheDocumentedFrameworkKey() {
        // The devices package spells the key out (it does not depend on Elarion.Abstractions); this pins it.
        EfCoreDevicePairingCodeStore<UnitOfWorkDbContext>.PairingLockKey("device-7")
            .Should().Be(AdvisoryLockKey.FrameworkText(AdvisoryLockKey.Scopes.DevicePairing, "device-7"));
    }
}

public sealed class DbContextLockProviderTests {
    [Fact]
    public async Task Locks_OnAProviderOtherThanPostgreSql_AreRefused() {
        await using var context = new UnitOfWorkDbContext(
            new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<UnitOfWorkDbContext>()
                .UseSqlite("Data Source=:memory:")
                .Options);

        var act = async () => await Elarion.EntityFrameworkCore.UnitOfWork.DbContextLockExtensions.AdvisoryLockAsync(
            context, "billing-account", "1", TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<NotSupportedException>().WithMessage("*need PostgreSQL*");
    }

    [Fact]
    public async Task UnenforcedLocks_LockNothingOutsideATransaction_ButStillValidateAndReportMissingRows() {
        await using var connection = await OpenSqliteAsync();
        await using var context = UnenforcedSqliteContext(connection);
        await context.Database.EnsureCreatedAsync(Ct);
        var id = Guid.CreateVersion7().ToString("N");
        context.Widgets.Add(new WidgetRow { Id = id, Name = "present" });
        await context.SaveChangesAsync(Ct);

        // No transaction, and SQLite: both guards are skipped for this opted-out context.
        (await context.LockRowAsync<WidgetRow>(id, RowLockMode.Update, Ct)).Should().BeTrue();
        (await context.LockRowAsync<WidgetRow>("missing", RowLockMode.Update, Ct)).Should().BeFalse();
        await context.AdvisoryLockAsync("billing-account", "1", Ct);
        (await context.TryAdvisoryLockAsync("statement-run", "2026-10", Ct)).Should().BeTrue();

        var reserved = async () => await context.AdvisoryLockAsync("elarion.scheduler.job", "nightly", Ct);
        var wrongType = async () => await context.LockRowAsync<WidgetRow>(42, RowLockMode.Update, Ct);
        await reserved.Should().ThrowAsync<ArgumentException>();
        await wrongType.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task UnenforcedLocks_HonorQueryFilters_UnlessIgnored() {
        await using var connection = await OpenSqliteAsync();
        await using var context = UnenforcedSqliteContext(connection);
        await context.Database.EnsureCreatedAsync(Ct);
        var hidden = Guid.CreateVersion7().ToString("N");
        context.ScopedWidgets.Add(new ScopedWidgetRow { Id = hidden, Tenant = "other" });
        await context.SaveChangesAsync(Ct);

        (await context.LockRowAsync<ScopedWidgetRow>(hidden, RowLockMode.Update, Ct)).Should().BeFalse();
        (await context.LockRowAsync<ScopedWidgetRow>(hidden, new RowLockOptions { IgnoreQueryFilters = true }, Ct))
            .Should().BeTrue();
    }

    [Fact]
    public async Task UnenforcedLocks_OnAPostgreSqlContext_AreRefused() {
        // A shared options helper must not leave a production (PostgreSQL) context unenforced. Refused before any
        // statement, so no database is needed.
        await using var context = new UnitOfWorkDbContext(new DbContextOptionsBuilder<UnitOfWorkDbContext>()
            .UseNpgsql("Host=unused.invalid")
            .UseUnenforcedElarionLocks()
            .Options);

        var act = async () => await context.AdvisoryLockAsync("billing-account", "1", Ct);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*always takes them*");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // An in-memory SQLite database lives as long as its one open connection.
    private static async Task<SqliteConnection> OpenSqliteAsync() {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(Ct);
        return connection;
    }

    private static UnitOfWorkDbContext UnenforcedSqliteContext(SqliteConnection connection) {
        return new UnitOfWorkDbContext(new DbContextOptionsBuilder<UnitOfWorkDbContext>()
            .UseSqlite(connection)
            .UseUnenforcedElarionLocks()
            .Options);
    }
}

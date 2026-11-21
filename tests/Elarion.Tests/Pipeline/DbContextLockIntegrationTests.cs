using AwesomeAssertions;
using Elarion.EntityFrameworkCore.UnitOfWork;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Xunit;

namespace Elarion.Tests.Pipeline;

/// <summary>
/// The transaction-scoped row and advisory locks against real PostgreSQL: a lock is held until the holder's
/// transaction ends (a second transaction waits, or fails fast under a lock timeout), the advisory key is exactly the
/// documented <c>hashtextextended('scope:key', 0)</c>, and a lock outside a transaction is refused.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DbContextLockIntegrationTests(PostgreSqlUnitOfWorkFixture fixture)
    : IClassFixture<PostgreSqlUnitOfWorkFixture> {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string> SeedWidgetAsync() {
        var id = Guid.CreateVersion7().ToString("N");
        await using var context = fixture.CreateContext();
        context.Widgets.Add(new WidgetRow { Id = id, Name = "locked" });
        await context.SaveChangesAsync(Ct);
        return id;
    }

    // A transaction that gives up on a conflicting lock after 200 ms instead of waiting forever.
    private static async Task<IDbContextTransaction> BeginImpatientAsync(UnitOfWorkDbContext context) {
        var transaction = await context.Database.BeginTransactionAsync(Ct);
        await context.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = 200", Ct);
        return transaction;
    }

    [Fact]
    public async Task LockRow_HoldsTheRowUntilTheTransactionEnds() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var id = await SeedWidgetAsync();

        await using var holder = fixture.CreateContext();
        await using var held = await holder.Database.BeginTransactionAsync(Ct);
        (await holder.LockRowAsync<WidgetRow>(id, RowLockMode.Update, Ct)).Should().BeTrue();

        await using (var contender = fixture.CreateContext()) {
            await using var waiting = await BeginImpatientAsync(contender);
            var blocked = async () => await contender.LockRowAsync<WidgetRow>(id, RowLockMode.NoKeyUpdate, Ct);
            // EF reports the lock timeout (55P03) wrapped as a possibly transient failure.
            (await blocked.Should().ThrowAsync<Exception>()).Which.GetBaseException()
                .Should().BeOfType<PostgresException>().Which.SqlState.Should().Be("55P03");
        }

        await held.CommitAsync(Ct);

        await using var after = fixture.CreateContext();
        await using var next = await BeginImpatientAsync(after);
        (await after.LockRowAsync<WidgetRow>(id, RowLockMode.NoKeyUpdate, Ct)).Should().BeTrue();
    }

    [Theory]
    [InlineData(RowLockMode.Update)]
    [InlineData(RowLockMode.NoKeyUpdate)]
    [InlineData(RowLockMode.Share)]
    [InlineData(RowLockMode.KeyShare)]
    public async Task LockRow_EveryModeLocksAnExistingRowAndReportsAMissingOne(RowLockMode mode) {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var id = await SeedWidgetAsync();

        await using var context = fixture.CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync(Ct);
        (await context.LockRowAsync<WidgetRow>(id, mode, Ct)).Should().BeTrue();
        (await context.LockRowAsync<WidgetRow>("missing", mode, Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task LockRow_ARowHiddenByAQueryFilter_IsReportedMissingAndNotLocked() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var hidden = Guid.CreateVersion7().ToString("N");
        await using (var seed = fixture.CreateContext()) {
            seed.ScopedWidgets.Add(new ScopedWidgetRow { Id = hidden, Tenant = "other" });
            await seed.SaveChangesAsync(Ct);
        }

        await using var holder = fixture.CreateContext();
        await using var held = await holder.Database.BeginTransactionAsync(Ct);
        // Another tenant's id: neither its existence is disclosed nor its row locked.
        (await holder.LockRowAsync<ScopedWidgetRow>(hidden, RowLockMode.Update, Ct)).Should().BeFalse();

        await using var contender = fixture.CreateContext();
        await using var waiting = await BeginImpatientAsync(contender);
        var locked = await contender.Database
            .SqlQueryRaw<int>("SELECT 1 AS \"Value\" FROM locks.scoped_widgets WHERE widget_id = {0} FOR UPDATE", hidden)
            .ToListAsync(Ct);
        locked.Should().ContainSingle("the hidden row was never locked, so the contender gets it at once");
    }

    [Fact]
    public async Task LockRow_IgnoringQueryFilters_FindsAndLocksAFilteredRow() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var archived = Guid.CreateVersion7().ToString("N");
        await using (var seed = fixture.CreateContext()) {
            seed.ScopedWidgets.Add(new ScopedWidgetRow { Id = archived, Tenant = "other" });
            await seed.SaveChangesAsync(Ct);
        }

        await using var holder = fixture.CreateContext();
        await using var held = await holder.Database.BeginTransactionAsync(Ct);
        // A writer that deliberately changes rows its filters hide (an archived tenant) must serialize on them too.
        (await holder.LockRowAsync<ScopedWidgetRow>(archived,
            new RowLockOptions { Mode = RowLockMode.NoKeyUpdate, IgnoreQueryFilters = true }, Ct)).Should().BeTrue();

        await using var contender = fixture.CreateContext();
        await using var waiting = await BeginImpatientAsync(contender);
        var blocked = async () => await contender.LockRowAsync<ScopedWidgetRow>(archived,
            new RowLockOptions { Mode = RowLockMode.NoKeyUpdate, IgnoreQueryFilters = true }, Ct);
        (await blocked.Should().ThrowAsync<Exception>()).Which.GetBaseException()
            .Should().BeOfType<PostgresException>().Which.SqlState.Should().Be("55P03");
    }

    [Fact]
    public async Task LockRow_ConvertsAStronglyTypedKey_AndRejectsAnIdOfTheWrongType() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var code = new WidgetCode(Guid.CreateVersion7().ToString("N"));
        await using (var seed = fixture.CreateContext()) {
            seed.CodedWidgets.Add(new CodedWidgetRow { Code = code });
            await seed.SaveChangesAsync(Ct);
        }

        await using var context = fixture.CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync(Ct);

        (await context.LockRowAsync<CodedWidgetRow>(code, RowLockMode.Update, Ct)).Should().BeTrue();
        var wrongType = async () => await context.LockRowAsync<CodedWidgetRow>(code.Value, RowLockMode.Update, Ct);
        await wrongType.Should().ThrowAsync<ArgumentException>().WithMessage("*keyed by WidgetCode*");
    }

    [Fact]
    public async Task AdvisoryLock_HoldsTheDocumentedKeyUntilTheTransactionEnds() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var key = Guid.CreateVersion7().ToString();

        await using var holder = fixture.CreateContext();
        await using var held = await holder.Database.BeginTransactionAsync(Ct);
        await holder.AdvisoryLockAsync("billing-account", key, Ct);

        // The held lock is exactly hashtextextended('scope:key', 0), so any code following the scheme meets it.
        var expected = await holder.Database
            .SqlQueryRaw<long>("SELECT hashtextextended({0}, 0) AS \"Value\"", $"billing-account:{key}")
            .SingleAsync(Ct);
        var heldKeys = await holder.Database
            .SqlQueryRaw<long>(
                "SELECT ((classid::bigint << 32) | objid::bigint) AS \"Value\" FROM pg_locks " +
                "WHERE locktype = 'advisory' AND objsubid = 1 AND pid = pg_backend_pid()")
            .ToListAsync(Ct);
        heldKeys.Should().Contain(expected);

        await using (var contender = fixture.CreateContext()) {
            await using var other = await contender.Database.BeginTransactionAsync(Ct);
            (await contender.TryAdvisoryLockAsync("billing-account", key, Ct)).Should().BeFalse();
            (await contender.TryAdvisoryLockAsync("billing-account", key + "-other", Ct)).Should().BeTrue();
        }

        await held.CommitAsync(Ct);

        await using var after = fixture.CreateContext();
        await using var next = await after.Database.BeginTransactionAsync(Ct);
        (await after.TryAdvisoryLockAsync("billing-account", key, Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task LockRows_LocksEveryVisibleRowOnceInAscendingKeyOrder() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        string[] ids = [.. (await Task.WhenAll(SeedWidgetAsync(), SeedWidgetAsync(), SeedWidgetAsync()))
            .Order(StringComparer.Ordinal)];
        var (low, middle, high) = (ids[0], ids[1], ids[2]);

        // The middle row is held elsewhere, so a contender locking all three in ascending order holds the low row and
        // waits at the middle one — it never reaches the high one, whatever order the request listed them in.
        await using var holder = fixture.CreateContext();
        var held = await holder.Database.BeginTransactionAsync(Ct);
        (await holder.LockRowAsync<WidgetRow>(middle, RowLockMode.Update, Ct)).Should().BeTrue();

        await using var contender = fixture.CreateContext();
        await using var contending = await contender.Database.BeginTransactionAsync(Ct);
        var contenderPid = await BackendPidAsync(contender);
        var locking = contender.LockRowsAsync<WidgetRow, string>([high, middle, low, high], ct: Ct);
        await WaitUntilWaitingAsync(contenderPid);

        (await IsRowLockedAsync(low)).Should().BeTrue();
        (await IsRowLockedAsync(high)).Should().BeFalse();

        await held.CommitAsync(Ct);
        await held.DisposeAsync();
        (await locking).Should().Equal(low, middle, high);
    }

    [Fact]
    public async Task LockRows_ReportsOnlyTheRowsItFound() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var first = await SeedWidgetAsync();
        var second = await SeedWidgetAsync();

        await using var context = fixture.CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync(Ct);
        var locked = await context.LockRowsAsync<WidgetRow, string>(
            [second, "missing", first, second], new RowLockOptions { Mode = RowLockMode.NoKeyUpdate }, Ct);

        locked.Should().Equal(new[] { first, second }.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AdvisoryLockAll_TakesEachKeyOnceInOrdinalOrder() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var prefix = Guid.CreateVersion7().ToString("N");
        var (a, b, c) = (prefix + "-a", prefix + "-b", prefix + "-c");

        await using var holder = fixture.CreateContext();
        var held = await holder.Database.BeginTransactionAsync(Ct);
        await holder.AdvisoryLockAsync("invoice-family", b, Ct);

        await using var contender = fixture.CreateContext();
        await using var contending = await contender.Database.BeginTransactionAsync(Ct);
        var contenderPid = await BackendPidAsync(contender);
        var locking = contender.AdvisoryLockAllAsync("invoice-family", [c, b, a, c], Ct);
        await WaitUntilWaitingAsync(contenderPid);

        await using (var observer = fixture.CreateContext()) {
            await using var observing = await observer.Database.BeginTransactionAsync(Ct);
            (await observer.TryAdvisoryLockAsync("invoice-family", a, Ct)).Should().BeFalse();
            (await observer.TryAdvisoryLockAsync("invoice-family", c, Ct)).Should().BeTrue();
        }

        await held.CommitAsync(Ct);
        await held.DisposeAsync();
        await locking;
    }

    private static async Task<int> BackendPidAsync(UnitOfWorkDbContext context) {
        return await context.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync(Ct);
    }

    // A lock that fails ends the transaction's locks at once (PostgreSQL aborts the transaction at the error), so what
    // a contender holds is observed while it waits.
    private async Task WaitUntilWaitingAsync(int pid) {
        await using var observer = fixture.CreateContext();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline) {
            var waiting = await observer.Database
                .SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM pg_locks WHERE pid = {0} AND NOT granted", pid)
                .SingleAsync(Ct);
            if (waiting > 0) return;

            await Task.Delay(20, Ct);
        }

        throw new TimeoutException($"Backend {pid} never waited for a lock.");
    }

    // Whether another transaction holds a lock on the row: NOWAIT fails at once instead of waiting.
    private async Task<bool> IsRowLockedAsync(string id) {
        await using var observer = fixture.CreateContext();
        await using var transaction = await observer.Database.BeginTransactionAsync(Ct);
        try {
            await observer.Database
                .SqlQueryRaw<int>("SELECT 1 AS \"Value\" FROM widgets WHERE id = {0} FOR UPDATE NOWAIT", id)
                .ToListAsync(Ct);
            return false;
        }
        catch (Exception exception) when (exception.GetBaseException() is PostgresException { SqlState: "55P03" }) {
            return true;
        }
    }

    [Fact]
    public async Task Locks_OutsideATransaction_AreRefused() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var context = fixture.CreateContext();

        var row = async () => await context.LockRowAsync<WidgetRow>("any", RowLockMode.Update, Ct);
        var advisory = async () => await context.AdvisoryLockAsync("billing-account", "1", Ct);

        var rows = async () => await context.LockRowsAsync<WidgetRow, string>([], ct: Ct);
        var advisories = async () => await context.AdvisoryLockAllAsync("billing-account", [], Ct);

        await row.Should().ThrowAsync<InvalidOperationException>().WithMessage("*needs a transaction*");
        await advisory.Should().ThrowAsync<InvalidOperationException>().WithMessage("*needs a transaction*");
        await rows.Should().ThrowAsync<InvalidOperationException>().WithMessage("*needs a transaction*");
        await advisories.Should().ThrowAsync<InvalidOperationException>().WithMessage("*needs a transaction*");
    }

    [Fact]
    public async Task AdvisoryLock_RejectsReservedScopes() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var context = fixture.CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync(Ct);

        var act = async () => await context.AdvisoryLockAsync("elarion.scheduler.job", "nightly", Ct);

        await act.Should().ThrowAsync<ArgumentException>();
    }
}

using AwesomeAssertions;
using Elarion.Abstractions.Pipeline;
using Elarion.EntityFrameworkCore.UnitOfWork;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Elarion.Tests.Pipeline;

/// <summary>
/// Integration tests for <see cref="EfUnitOfWork{TDbContext}"/> against real PostgreSQL: the scope flushes the
/// change tracker on commit (so a handler that forgot to save still persists), and a nested scope on an already
/// open transaction joins it via a savepoint instead of throwing, committing/rolling back only its own writes.
/// </summary>
[Trait("Category", "Integration")]
public sealed class EfUnitOfWorkIntegrationTests(PostgreSqlUnitOfWorkFixture fixture)
    : IClassFixture<PostgreSqlUnitOfWorkFixture> {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewId() => Guid.NewGuid().ToString("N");

    [Fact]
    public async Task Commit_FlushesPendingChanges_WhenHandlerForgotToSave() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var id = NewId();

        await using (var context = fixture.CreateContext()) {
            var uow = new EfUnitOfWork<UnitOfWorkDbContext>(context);
            await using var scope = await uow.BeginAsync(UnitOfWorkOptions.Default, Ct);
            // Mutate but deliberately never call SaveChangesAsync — the commit must flush it (H1).
            context.Widgets.Add(new WidgetRow { Id = id, Name = "unsaved" });
            await scope.CommitAsync(Ct);
        }

        await using var verify = fixture.CreateContext();
        (await verify.Widgets.CountAsync(w => w.Id == id, Ct)).Should().Be(1);
    }

    [Fact]
    public async Task NestedScope_JoinsAmbientTransaction_BothCommit() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var outerId = NewId();
        var innerId = NewId();

        await using (var context = fixture.CreateContext()) {
            var uow = new EfUnitOfWork<UnitOfWorkDbContext>(context);
            await using var outer = await uow.BeginAsync(UnitOfWorkOptions.Default, Ct);
            context.Widgets.Add(new WidgetRow { Id = outerId, Name = "outer" });
            await context.SaveChangesAsync(Ct);

            // A nested transactional handler on the SAME DbContext/scope (as through IHandlerSender) must not
            // throw "transaction already open" (C3): it joins with a savepoint.
            await using (var inner = await uow.BeginAsync(UnitOfWorkOptions.Default, Ct)) {
                context.Widgets.Add(new WidgetRow { Id = innerId, Name = "inner" });
                await inner.CommitAsync(Ct);
            }

            await outer.CommitAsync(Ct);
        }

        await using var verify = fixture.CreateContext();
        (await verify.Widgets.CountAsync(w => w.Id == outerId || w.Id == innerId, Ct)).Should().Be(2);
    }

    [Fact]
    public async Task NestedScope_Rollback_DiscardsOnlyInnerWrites() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var outerId = NewId();
        var innerId = NewId();

        await using (var context = fixture.CreateContext()) {
            var uow = new EfUnitOfWork<UnitOfWorkDbContext>(context);
            await using var outer = await uow.BeginAsync(UnitOfWorkOptions.Default, Ct);
            context.Widgets.Add(new WidgetRow { Id = outerId, Name = "outer" });
            await context.SaveChangesAsync(Ct);

            await using (var inner = await uow.BeginAsync(UnitOfWorkOptions.Default, Ct)) {
                context.Widgets.Add(new WidgetRow { Id = innerId, Name = "inner" });
                await context.SaveChangesAsync(Ct);
                // Inner fails: roll back to the savepoint, discarding only the inner write.
                await inner.RollbackAsync(Ct);
            }

            await outer.CommitAsync(Ct);
        }

        await using var verify = fixture.CreateContext();
        (await verify.Widgets.CountAsync(w => w.Id == outerId, Ct)).Should().Be(1);
        (await verify.Widgets.CountAsync(w => w.Id == innerId, Ct)).Should().Be(0);
    }
}

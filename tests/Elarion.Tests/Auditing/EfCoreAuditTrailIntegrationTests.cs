using System.Data.Common;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Auditing;
using Elarion.Abstractions.Pipeline;
using Elarion.Auditing;
using Elarion.Auditing.EntityFrameworkCore;
using Elarion.EntityFrameworkCore.UnitOfWork;
using Elarion.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.Auditing;

/// <summary>
/// End-to-end integration tests for the EF Core audit trail (ADR-0045) against real PostgreSQL, composed the
/// way the generated pipeline composes it: outer <see cref="AuditDecorator{TRequest,TResponse}"/> →
/// <see cref="TransactionDecorator{TRequest,TResponse}"/> → <see cref="AuditCommitDecorator{TRequest,TResponse}"/>
/// → handler, with the change-capture interceptor attached through the real DI wiring
/// (<c>AddElarionAuditingEntityFrameworkCore</c>). Proves the two write paths: a success record commits
/// atomically with the business write; a failure's business write rolls back while the detached record survives.
/// </summary>
[Trait("Category", "Integration")]
public sealed class EfCoreAuditTrailIntegrationTests(PostgreSqlAuditTrailFixture fixture)
    : IClassFixture<PostgreSqlAuditTrailFixture> {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SuccessfulCommand_CommitsTheAuditRecordWithTheBusinessWrite_AndCapturesFieldDiffs() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var id = Guid.CreateVersion7();
        await Seed(new AuditedProperty { Id = id, Name = "Liegenschaft", Street = "Old Street", Secret = "s3cret" });

        await using var host = Compose();
        var result = await host.Run(async (db, scope, ct) => {
            var property = await db.Properties.SingleAsync(p => p.Id == id, ct);
            property.Street = "New Street";
            property.Secret = "changed-secret";
            scope.SetResource("property", id.ToString());
            return Result<string>.Success("ok");
        });

        result.IsSuccess.Should().BeTrue();
        await using var verify = fixture.CreateContext();
        (await verify.Properties.SingleAsync(p => p.Id == id, Ct)).Street.Should().Be("New Street");

        var entry = await verify.AuditLog.SingleAsync(Ct);
        entry.Outcome.Should().Be(nameof(AuditOutcome.Succeeded));
        entry.Action.Should().Be("properties.update");
        entry.ResourceType.Should().Be("property");
        entry.ResourceId.Should().Be(id.ToString());
        entry.Changes.Should().Contain("\"property\":\"Street\"");
        entry.Changes.Should().Contain("\"oldValue\":\"Old Street\"");
        entry.Changes.Should().Contain("\"newValue\":\"New Street\"");
        // [AuditIgnore] keeps the sensitive column out of capture even though it changed.
        entry.Changes.Should().NotContain("Secret");
        entry.Changes.Should().NotContain("s3cret");
    }

    [Fact]
    public async Task FailedCommand_RollsBackTheBusinessWrite_ButTheDetachedFailureRecordSurvives() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var id = Guid.CreateVersion7();
        await Seed(new AuditedProperty { Id = id, Name = "Liegenschaft", Street = "Old Street", Secret = "" });

        await using var host = Compose();
        var result = await host.Run(async (db, scope, ct) => {
            var property = await db.Properties.SingleAsync(p => p.Id == id, ct);
            property.Street = "Attempted Street";
            // A mid-handler flush inside the transaction — the capture point the interceptor exists for.
            await db.SaveChangesAsync(ct);
            return Result<string>.Failure(AppError.BusinessRule("nope"));
        });

        result.IsSuccess.Should().BeFalse();
        await using var verify = fixture.CreateContext();
        // The transaction rolled the flushed business write back…
        (await verify.Properties.SingleAsync(p => p.Id == id, Ct)).Street.Should().Be("Old Street");

        // …but the detached failure record survives it, carrying the attempted change.
        var entry = await verify.AuditLog.SingleAsync(Ct);
        entry.Outcome.Should().Be(nameof(AuditOutcome.Failed));
        entry.ErrorKind.Should().Be(nameof(ErrorKind.BusinessRule));
        entry.Changes.Should().Contain("\"newValue\":\"Attempted Street\"");
    }

    [Fact]
    public async Task AddedEntity_RecordsTheCreation_WithTheFinalKey() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var id = Guid.CreateVersion7();
        await using (var reset = fixture.CreateContext()) {
            await reset.AuditLog.ExecuteDeleteAsync(Ct);
        }

        await using var host = Compose();
        var result = await host.Run((db, scope, _) => {
            db.Properties.Add(new AuditedProperty { Id = id, Name = "Neu", Street = "Street 1", Secret = "" });
            return Task.FromResult(Result<string>.Success("ok"));
        });

        result.IsSuccess.Should().BeTrue();
        await using var verify = fixture.CreateContext();
        var entry = await verify.AuditLog.SingleAsync(Ct);
        entry.Outcome.Should().Be(nameof(AuditOutcome.Succeeded));
        entry.Changes.Should().Contain($"\"entityId\":\"{id}\"");
        entry.Changes.Should().Contain("\"entity\":\"AuditedProperty\"");
    }

    [Fact]
    public async Task CommitPhaseFailure_RollsBackTheSuccessRecord_AndWritesTheDetachedFailedRecord() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var id = Guid.CreateVersion7();
        await Seed(new AuditedProperty { Id = id, Name = "Liegenschaft", Street = "Old Street", Secret = "" });

        // The unit-of-work COMMIT itself fails (the connection-drop/serialization-failure shape): the enlisted
        // success record rolls back with the business write, and "Recorded" must not have been set — the outer
        // decorator writes the detached Failed record instead of leaving no audit trace at all.
        await using var host = Compose(new FailNextCommitInterceptor());
        var act = async () => await host.Run(async (db, scope, ct) => {
            var property = await db.Properties.SingleAsync(p => p.Id == id, ct);
            property.Street = "New Street";
            scope.SetResource("property", id.ToString());
            return Result<string>.Success("ok");
        });

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("commit lost");
        await using var verify = fixture.CreateContext();
        (await verify.Properties.SingleAsync(p => p.Id == id, Ct)).Street.Should().Be("Old Street");

        var entry = await verify.AuditLog.SingleAsync(Ct);
        entry.Outcome.Should().Be(nameof(AuditOutcome.Failed));
        entry.ErrorKind.Should().Be(nameof(ErrorKind.Internal));
        entry.ResourceId.Should().Be(id.ToString());
    }

    [Fact]
    public async Task PostCommitDecoratorFailure_KeepsTheDurableSuccessRecord_WithoutASpuriousDetachedRecord() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var id = Guid.CreateVersion7();
        await Seed(new AuditedProperty { Id = id, Name = "Liegenschaft", Street = "Old Street", Secret = "" });

        // A decorator between the outer audit decorator and the transaction (the cache-invalidation position)
        // throws AFTER the successful commit: the transaction interceptor already promoted the pending mark to
        // Recorded, so exactly one Succeeded record exists — no spurious detached Failed duplicate.
        await using var host = Compose();
        var act = async () => await host.Run(async (db, scope, ct) => {
            var property = await db.Properties.SingleAsync(p => p.Id == id, ct);
            property.Street = "New Street";
            scope.SetResource("property", id.ToString());
            return Result<string>.Success("ok");
        }, static inner => new ThrowAfterCommitDecorator(inner));

        await act.Should().ThrowAsync<TimeoutException>();
        await using var verify = fixture.CreateContext();
        (await verify.Properties.SingleAsync(p => p.Id == id, Ct)).Street.Should().Be("New Street");

        var entry = await verify.AuditLog.SingleAsync(Ct);
        entry.Outcome.Should().Be(nameof(AuditOutcome.Succeeded));
    }

    private async Task Seed(AuditedProperty property) {
        await using var db = fixture.CreateContext();
        // The fixture database is shared across the class's tests — start each test from an empty log.
        await db.AuditLog.ExecuteDeleteAsync(Ct);
        db.Properties.Add(property);
        await db.SaveChangesAsync(Ct);
        db.ChangeTracker.Clear();
    }

    private ComposedHost Compose(IInterceptor? extraInterceptor = null) {
        return new ComposedHost(fixture.ConnectionString, extraInterceptor);
    }

    private sealed record DemoCommand : ICommand;

    /// <summary>Fails exactly one transaction commit — the connection-drop-at-COMMIT shape. Subsequent
    /// commits (and the detached write's autocommit save) proceed normally.</summary>
    private sealed class FailNextCommitInterceptor : DbTransactionInterceptor {
        private bool _failed;

        public override InterceptionResult TransactionCommitting(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result) {
            ThrowOnce();
            return result;
        }

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default) {
            ThrowOnce();
            return ValueTask.FromResult(result);
        }

        private void ThrowOnce() {
            if (_failed)
                return;

            _failed = true;
            throw new InvalidOperationException("commit lost");
        }
    }

    /// <summary>The post-commit failure position: cache invalidation (or any later decorator) throwing after
    /// the transaction decorator returned.</summary>
    private sealed class ThrowAfterCommitDecorator(IHandler<DemoCommand, Result<string>> inner)
        : IHandler<DemoCommand, Result<string>> {
        public async ValueTask<Result<string>> HandleAsync(DemoCommand request, CancellationToken ct) {
            await inner.HandleAsync(request, ct);
            throw new TimeoutException("cache invalidation down");
        }
    }

    /// <summary>
    /// The real DI composition (<c>AddDbContext</c> + <c>AddElarionUnitOfWork</c> +
    /// <c>AddElarionAuditingEntityFrameworkCore</c>, interceptor attached via the options-configuration seam),
    /// plus the manually assembled decorator chain in generated-pipeline order.
    /// </summary>
    private sealed class ComposedHost : IAsyncDisposable {
        private readonly ServiceProvider _provider;

        public ComposedHost(string connectionString, IInterceptor? extraInterceptor = null) {
            var services = new ServiceCollection();
            services.AddDbContext<AuditIntegrationDbContext>(options => {
                options.UseNpgsql(connectionString);
                if (extraInterceptor is not null) options.AddInterceptors(extraInterceptor);
            });
            services.AddElarionUnitOfWork<AuditIntegrationDbContext>();
            services.AddElarionAuditingEntityFrameworkCore<AuditIntegrationDbContext>();
            _provider = services.BuildServiceProvider();
        }

        public async Task<Result<string>> Run(
            Func<AuditIntegrationDbContext, IAuditScope, CancellationToken, Task<Result<string>>> body,
            Func<IHandler<DemoCommand, Result<string>>, IHandler<DemoCommand, Result<string>>>? wrapTransaction =
                null) {
            await using var scope = _provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AuditIntegrationDbContext>();
            var auditScope = scope.ServiceProvider.GetRequiredService<AuditScope>();
            var trail = scope.ServiceProvider.GetRequiredService<IAuditTrail>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var handler = new DelegateHandler(db, auditScope, body);
            var commit = new AuditCommitDecorator<DemoCommand, Result<string>>(handler, auditScope, trail);
            IHandler<DemoCommand, Result<string>> transaction =
                new TransactionDecorator<DemoCommand, Result<string>>(commit, unitOfWork);
            if (wrapTransaction is not null)
                // The position between the outer audit decorator and the transaction — where cache
                // invalidation and other post-commit decorators live in the generated chain.
                transaction = wrapTransaction(transaction);

            var metadata = new HandlerMetadata(typeof(DelegateHandler), typeof(DemoCommand), typeof(Result<string>));
            var outer = new AuditDecorator<DemoCommand, Result<string>>(
                transaction, metadata, "properties.update", "Properties", auditScope, trail, null);

            return await outer.HandleAsync(new DemoCommand(), Ct);
        }

        public ValueTask DisposeAsync() {
            return _provider.DisposeAsync();
        }
    }

    private sealed class DelegateHandler(
        AuditIntegrationDbContext db,
        IAuditScope scope,
        Func<AuditIntegrationDbContext, IAuditScope, CancellationToken, Task<Result<string>>> body
    ) : IHandler<DemoCommand, Result<string>> {
        public async ValueTask<Result<string>> HandleAsync(DemoCommand request, CancellationToken ct) {
            return await body(db, scope, ct);
        }
    }
}

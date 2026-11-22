using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Identity;
using Elarion.Abstractions.Idempotency;
using Elarion.Abstractions.Pipeline;
using Elarion.Abstractions.Serialization;
using Elarion.EntityFrameworkCore.UnitOfWork;
using Elarion.Idempotency.EntityFrameworkCore;
using Elarion.Pipeline;
using Elarion.Tests.Idempotency;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.Pipeline;

/// <summary>
/// Previewable commands against real PostgreSQL through the EF Core unit of work (<c>AddElarionUnitOfWork</c>), composed
/// the way the generated pipeline composes them: the preview decorator outside the application's transaction
/// decorator. A preview leaves no rows, an unchanged decision commits, and a decision that changed since the preview
/// refuses to commit.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PreviewDecoratorIntegrationTests(PostgreSqlUnitOfWorkFixture fixture)
    : IClassFixture<PostgreSqlUnitOfWorkFixture> {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewId() {
        return Guid.NewGuid().ToString("N");
    }

    private async Task<Result<PreviewImportOutcome>> ImportAsync(
        PreviewImportCommand command, bool withTransactionDecorator = true) {
        var services = new ServiceCollection();
        services.AddScoped(_ => fixture.CreateContext());
        services.AddElarionUnitOfWork<UnitOfWorkDbContext>();
        services.ConfigureElarionJson(options => options.TypeInfoResolvers.Add(PreviewTestJsonContext.Default));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        IHandler<PreviewImportCommand, Result<PreviewImportOutcome>> handler =
            new ImportWidgets(sp.GetRequiredService<UnitOfWorkDbContext>());
        if (withTransactionDecorator)
            handler = new TransactionDecorator<PreviewImportCommand, Result<PreviewImportOutcome>>(
                handler, sp.GetRequiredService<IUnitOfWork>());
        handler = new PreviewDecorator<PreviewImportCommand, PreviewImportOutcome>(
            handler, sp.GetService<IUnitOfWork>(), sp.GetRequiredService<IElarionJsonSerialization>());
        return await handler.HandleAsync(command, Ct);
    }

    private async Task<int> CountAsync(params string[] ids) {
        await using var verify = fixture.CreateContext();
        return await verify.Widgets.CountAsync(widget => ids.Contains(widget.Id), Ct);
    }

    [Fact]
    public async Task EfUnitOfWork_ClaimsRollback() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var context = fixture.CreateContext();

        ((IUnitOfWork)new EfUnitOfWork<UnitOfWorkDbContext>(context)).SupportsRollback.Should().BeTrue();
    }

    [Fact]
    public async Task Preview_ReportsTheOutcome_AndLeavesNoRows() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var (first, second) = (NewId(), NewId());

        var result = await ImportAsync(new PreviewImportCommand(Statement(first, second), true));

        result.IsSuccess.Should().BeTrue();
        result.Value.NewRows.Should().BeEquivalentTo(first, second);
        result.Value.PreviewFingerprint.Should().NotBeNullOrEmpty();
        (await CountAsync(first, second)).Should().Be(0);
    }

    [Fact]
    public async Task Commit_WithTheFingerprintOfAnUnchangedPreview_WritesTheRows() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var (first, second) = (NewId(), NewId());
        var statement = Statement(first, second);
        var preview = await ImportAsync(new PreviewImportCommand(statement, true));

        var result = await ImportAsync(
            new PreviewImportCommand(statement, PreviewFingerprint: preview.Value.PreviewFingerprint));

        result.IsSuccess.Should().BeTrue();
        result.Value.PreviewFingerprint.Should().Be(preview.Value.PreviewFingerprint);
        (await CountAsync(first, second)).Should().Be(2);
    }

    [Fact]
    public async Task Commit_AfterAPreviewedRowBecameADuplicate_RefusesAndWritesNothing() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var (first, second) = (NewId(), NewId());
        var statement = Statement(first, second);
        var preview = await ImportAsync(new PreviewImportCommand(statement, true));
        preview.Value.NewRows.Should().HaveCount(2);

        // Between preview and commit another import writes the second row: the commit would now hold it back.
        await using (var other = fixture.CreateContext()) {
            other.Widgets.Add(new WidgetRow { Id = second, Name = "imported elsewhere" });
            await other.SaveChangesAsync(Ct);
        }

        var result = await ImportAsync(
            new PreviewImportCommand(statement, PreviewFingerprint: preview.Value.PreviewFingerprint));

        result.IsSuccess.Should().BeFalse();
        result.Error.Code.Should().Be(ErrorCodes.PreviewChanged);
        var current = result.Error.Data.Should().BeOfType<PreviewImportOutcome>().Subject;
        current.NewRows.Should().Equal(first);
        current.HeldBack.Should().Equal(second);
        (await CountAsync(first)).Should().Be(0);
    }

    [Fact]
    public async Task Preview_FlushesTheWrites_SoAConstraintViolationSurfacesAsItWouldOnCommit() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var id = NewId();
        await using (var other = fixture.CreateContext()) {
            other.Widgets.Add(new WidgetRow { Id = id, Name = "existing" });
            await other.SaveChangesAsync(Ct);
        }

        // The handler adds an existing key without checking and without saving, and no application transaction
        // decorator flushes it: only the preview decorator's nested commit reaches the database. Without it the
        // preview would report success for a command whose commit fails.
        var act = async () => await ImportAsync(
            new PreviewImportCommand(Statement(id) + ";unchecked", true), withTransactionDecorator: false);

        await act.Should().ThrowAsync<DbUpdateException>();
        (await CountAsync(id)).Should().Be(1);
    }

    private static string Statement(params string[] ids) {
        return string.Join(',', ids);
    }

    /// <summary>
    /// Imports the statement's rows as widgets, holding back a row that already exists (a possible duplicate). With
    /// <c>;unchecked</c> it skips the check and leaves the rows unsaved, so an existing key surfaces only when flushed.
    /// </summary>
    private sealed class ImportWidgets(UnitOfWorkDbContext db)
        : IHandler<PreviewImportCommand, Result<PreviewImportOutcome>> {
        public async ValueTask<Result<PreviewImportOutcome>> HandleAsync(
            PreviewImportCommand request, CancellationToken ct) {
            var parts = request.Statement.Split(';');
            var ids = parts[0].Split(',');
            if (parts.Length > 1) {
                foreach (var id in ids) db.Widgets.Add(new WidgetRow { Id = id, Name = "unchecked" });
                return new PreviewImportOutcome(ids, []);
            }

            var existing = await db.Widgets.Where(widget => ids.Contains(widget.Id)).Select(widget => widget.Id)
                .ToListAsync(ct);
            var newRows = ids.Where(id => !existing.Contains(id)).Order(StringComparer.Ordinal).ToList();
            var heldBack = ids.Where(existing.Contains).Order(StringComparer.Ordinal).ToList();
            foreach (var id in newRows) db.Widgets.Add(new WidgetRow { Id = id, Name = "imported" });
            await db.SaveChangesAsync(ct);
            return new PreviewImportOutcome(newRows, heldBack);
        }
    }
}

/// <summary>
/// A previewable command under <c>[Idempotent]</c> against real PostgreSQL: the idempotency decorator owns the
/// transaction, the preview's scope nests in it as a savepoint and rolls back, and the key commits with the preview
/// response — without the idempotency commit's flush writing the rolled-back rows again.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PreviewIdempotencyIntegrationTests(PostgreSqlIdempotencyStoreFixture fixture)
    : IClassFixture<PostgreSqlIdempotencyStoreFixture> {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<Result<PreviewImportOutcome>> ImportAsync(PreviewImportCommand command, string key) {
        var context = fixture.CreateContext();
        var unitOfWork = new EfUnitOfWork<IdempotencyIntegrationDbContext>(context);
        var json = PreviewTestJson.Create();
        var decorator = new IdempotencyDecorator<PreviewImportCommand, Result<PreviewImportOutcome>>(
            new PreviewDecorator<PreviewImportCommand, PreviewImportOutcome>(new WriteDemoRow(context), unitOfWork, json),
            unitOfWork,
            new EfCoreIdempotencyStore<IdempotencyIntegrationDbContext>(context, TimeProvider.System),
            new FixedKeyAccessor(key),
            new OutcomePolicy(),
            new AuthenticatedUser(),
            json.Options);
        return decorator.HandleAsync(command, Ct).AsTask();
    }

    [Fact]
    public async Task Preview_RollsBackItsRows_WhileTheKeyCommits_AndTheCommitIsItsOwnCall() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var statement = $"s-{Guid.NewGuid():N}";
        var (previewKey, commitKey) = ($"k-{Guid.NewGuid():N}", $"k-{Guid.NewGuid():N}");

        var preview = await ImportAsync(new PreviewImportCommand(statement, true), previewKey);

        preview.IsSuccess.Should().BeTrue();
        await using (var verify = fixture.CreateContext()) {
            (await verify.DemoRows.CountAsync(row => row.IdempotencyKey == statement, Ct)).Should().Be(0);
            (await verify.IdempotencyKeys.CountAsync(row => row.Key == previewKey, Ct)).Should().Be(1);
        }

        var commit = await ImportAsync(
            new PreviewImportCommand(statement, PreviewFingerprint: preview.Value.PreviewFingerprint), commitKey);

        commit.IsSuccess.Should().BeTrue();
        await using (var verify = fixture.CreateContext())
            (await verify.DemoRows.CountAsync(row => row.IdempotencyKey == statement, Ct)).Should().Be(1);
    }

    // Adds one row without saving: the preview decorator's nested scope flushes it, so the idempotency decorator's
    // final SaveChanges finds nothing left to write after the preview rolled back.
    private sealed class WriteDemoRow(IdempotencyIntegrationDbContext db)
        : IHandler<PreviewImportCommand, Result<PreviewImportOutcome>> {
        public ValueTask<Result<PreviewImportOutcome>> HandleAsync(PreviewImportCommand request, CancellationToken ct) {
            db.DemoRows.Add(new DemoRow { Id = $"{request.Statement}-row", IdempotencyKey = request.Statement });
            return ValueTask.FromResult<Result<PreviewImportOutcome>>(new PreviewImportOutcome([request.Statement], []));
        }
    }

    private sealed class OutcomePolicy : IIdempotencyPayloadPolicy<PreviewImportCommand, Result<PreviewImportOutcome>> {
        public IdempotencyScope Scope => IdempotencyScope.Global;
        public bool KeyRequired => true;
        public bool Fingerprint => false;
        public IdempotencyConflictBehavior ConflictBehavior => IdempotencyConflictBehavior.Conflict;
        public IdempotencyFailureStorage StoreFailures => IdempotencyFailureStorage.None;
        public TimeSpan Retention => TimeSpan.FromHours(1);

        public string Serialize(Result<PreviewImportOutcome> response, JsonSerializerOptions options) {
            return JsonSerializer.Serialize(response.Value, PreviewTestJsonContext.Default.PreviewImportOutcome);
        }

        public Result<PreviewImportOutcome> Deserialize(string payload, JsonSerializerOptions options) {
            return JsonSerializer.Deserialize(payload, PreviewTestJsonContext.Default.PreviewImportOutcome)!;
        }
    }

    private sealed class FixedKeyAccessor(string key) : IIdempotencyKeyAccessor {
        public bool TryGetKey([NotNullWhen(true)] out string? resolvedKey) {
            resolvedKey = key;
            return true;
        }
    }

    private sealed class AuthenticatedUser : ICurrentUser {
        public string UserId => "user-1";
        public string? Email => null;
        public IReadOnlyList<string> Roles => [];
        public bool IsAuthenticated => true;

        public bool IsInRole(string role) {
            return false;
        }
    }
}

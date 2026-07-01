using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Identity;
using Elarion.Abstractions.Idempotency;
using Elarion.EntityFrameworkCore.UnitOfWork;
using Elarion.Idempotency.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Elarion.Tests.Idempotency;

/// <summary>
/// End-to-end integration tests for the durable EF Core idempotency store composed with the real unit of work
/// and the idempotency decorator, against PostgreSQL. Proves the atomic single-transaction guarantee: the key
/// row commits with the business write, a duplicate never re-runs the handler, and a retry replays the result.
/// </summary>
[Trait("Category", "Integration")]
public sealed class EfCoreIdempotencyStoreIntegrationTests(PostgreSqlIdempotencyStoreFixture fixture)
    : IClassFixture<PostgreSqlIdempotencyStoreFixture> {
    private static readonly JsonSerializerOptions Json = new() { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string UniqueKey() => $"k-{Guid.NewGuid():N}";

    private IdempotencyDecorator<DemoCommand, Result<string>> BuildDecorator(
        string key,
        IdempotencyConflictBehavior conflict = IdempotencyConflictBehavior.Conflict,
        int handlerDelayMs = 0) {
        var context = fixture.CreateContext();
        var unitOfWork = new EfUnitOfWork<IdempotencyIntegrationDbContext>(context);
        var store = new EfCoreIdempotencyStore<IdempotencyIntegrationDbContext>(context, TimeProvider.System);
        return new IdempotencyDecorator<DemoCommand, Result<string>>(
            new DemoHandler(context, key, handlerDelayMs),
            unitOfWork,
            store,
            new FixedKeyAccessor(key),
            new DemoPolicy(conflict),
            new AuthenticatedUser(),
            Json);
    }

    [Fact]
    public async Task FirstCall_Commits_SecondCall_Replays_WithoutReRunning() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var key = UniqueKey();

        var first = await BuildDecorator(key).HandleAsync(new DemoCommand(1), Ct);
        first.IsSuccess.Should().BeTrue();

        // A retry with the same key replays the stored result without a second business write.
        var second = await BuildDecorator(key).HandleAsync(new DemoCommand(2), Ct);
        second.IsSuccess.Should().BeTrue();
        second.Value.Should().Be(first.Value);

        await using var verify = fixture.CreateContext();
        (await verify.DemoRows.CountAsync(row => row.IdempotencyKey == key, Ct)).Should().Be(1);
    }

    [Fact]
    public async Task ConcurrentDuplicates_ExecuteHandlerExactlyOnce() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var key = UniqueKey();

        // Fire many requests with the same key concurrently; the handler holds its transaction briefly so the
        // losers contend on the unique-constrained key row.
        var tasks = Enumerable.Range(0, 8)
            .Select(i => BuildDecorator(key, handlerDelayMs: 150).HandleAsync(new DemoCommand(i), Ct).AsTask())
            .ToArray();
        var results = await Task.WhenAll(tasks);

        // Exactly one winner did the business write; the losers 409'd or replayed — never a second execution.
        await using var verify = fixture.CreateContext();
        (await verify.DemoRows.CountAsync(row => row.IdempotencyKey == key, Ct)).Should().Be(1);

        results.Count(r => r.IsSuccess).Should().BeGreaterThanOrEqualTo(1);
        results.Where(r => !r.IsSuccess).Should().OnlyContain(r => r.Error.Kind == ErrorKind.Conflict);
    }

    [Fact]
    public async Task FingerprintMismatch_WhenKeyReusedWithDifferentBody() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var key = UniqueKey();

        var first = await BuildDecorator(key, conflict: IdempotencyConflictBehavior.Conflict)
            .HandleAsync(new DemoCommand(1), Ct);
        first.IsSuccess.Should().BeTrue();

        // Same key, different request body → fingerprint mismatch (422 → BusinessRule).
        var reused = await BuildFingerprintingDecorator(key).HandleAsync(new DemoCommand(999), Ct);
        reused.IsSuccess.Should().BeFalse();
        reused.Error.Kind.Should().Be(ErrorKind.BusinessRule);
    }

    private IdempotencyDecorator<DemoCommand, Result<string>> BuildFingerprintingDecorator(string key) {
        var context = fixture.CreateContext();
        return new IdempotencyDecorator<DemoCommand, Result<string>>(
            new DemoHandler(context, key, 0),
            new EfUnitOfWork<IdempotencyIntegrationDbContext>(context),
            new EfCoreIdempotencyStore<IdempotencyIntegrationDbContext>(context, TimeProvider.System),
            new FixedKeyAccessor(key),
            new DemoPolicy(IdempotencyConflictBehavior.Conflict, fingerprint: true),
            new AuthenticatedUser(),
            Json);
    }

    private sealed record DemoCommand(int Id) : ICommand;

    private sealed class DemoHandler(IdempotencyIntegrationDbContext context, string key, int delayMs)
        : IHandler<DemoCommand, Result<string>> {
        public async ValueTask<Result<string>> HandleAsync(DemoCommand request, CancellationToken ct) {
            context.DemoRows.Add(new DemoRow { Id = Guid.NewGuid().ToString("N"), IdempotencyKey = key });
            await context.SaveChangesAsync(ct);
            if (delayMs > 0) {
                await Task.Delay(delayMs, ct);
            }

            return Result<string>.Success($"receipt:{key}");
        }
    }

    private sealed class DemoPolicy(IdempotencyConflictBehavior conflict, bool fingerprint = false)
        : IIdempotencyPayloadPolicy<DemoCommand, Result<string>> {
        public IdempotencyScope Scope => IdempotencyScope.Global;
        public bool KeyRequired => true;
        public bool Fingerprint => fingerprint;
        public IdempotencyConflictBehavior ConflictBehavior => conflict;
        public IdempotencyFailureStorage StoreFailures => IdempotencyFailureStorage.None;
        public TimeSpan Retention => TimeSpan.FromHours(24);

        public string Serialize(Result<string> response, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(new StoredResult<string> { Ok = response.IsSuccess, Value = response.IsSuccess ? response.Value : null, Error = response.IsSuccess ? null : response.Error }, options);

        public Result<string> Deserialize(string payload, JsonSerializerOptions options) {
            var stored = JsonSerializer.Deserialize<StoredResult<string>>(payload, options)!;
            return stored.Ok ? Result<string>.Success(stored.Value!) : Result<string>.Failure(stored.Error ?? AppError.InternalError);
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
        public bool IsInRole(string role) => false;
    }
}

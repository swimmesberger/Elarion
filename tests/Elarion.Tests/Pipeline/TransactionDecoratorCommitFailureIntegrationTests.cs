using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Pipeline;
using Elarion.EntityFrameworkCore.UnitOfWork;
using Elarion.Pipeline;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Elarion.Tests.Pipeline;

/// <summary>
/// A <c>DEFERRABLE INITIALLY DEFERRED</c> constraint is checked at <c>COMMIT</c>, outside every <c>SaveChanges</c>
/// interceptor. <see cref="TransactionDecorator{TRequest,TResponse}"/> offers that commit failure to the registered
/// <see cref="IUnitOfWorkFailureTranslator"/>s, so it answers as the command's failure instead of an unexpected error.
/// </summary>
[Trait("Category", "Integration")]
public sealed class TransactionDecoratorCommitFailureIntegrationTests(PostgreSqlUnitOfWorkFixture fixture)
    : IClassFixture<PostgreSqlUnitOfWorkFixture> {
    private const string CreateTable =
        "CREATE TABLE IF NOT EXISTS deferred_codes (code text NOT NULL, " +
        "CONSTRAINT uq_deferred_code UNIQUE (code) DEFERRABLE INITIALLY DEFERRED)";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DeferredConstraintViolatedAtCommit_IsTranslatedToTheCommandsFailure_AndNothingCommits() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var code = Guid.CreateVersion7().ToString("N");
        await using var context = fixture.CreateContext();
        await context.Database.ExecuteSqlRawAsync(CreateTable, Ct);
        var decorator = new TransactionDecorator<InsertTwice, Result<string>>(
            new InsertTwiceHandler(context), new EfUnitOfWork<UnitOfWorkDbContext>(context),
            [new NotATranslator(), new DuplicateCodeTranslator()]);

        var result = await decorator.HandleAsync(new InsertTwice(code), Ct);

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.Conflict);
        result.Error.Code.Should().Be("codes.duplicate");
        await using var verify = fixture.CreateContext();
        (await verify.Database.SqlQueryRaw<int>(
                "SELECT count(*)::int AS \"Value\" FROM deferred_codes WHERE code = {0}", code)
            .SingleAsync(Ct)).Should().Be(0);
    }

    [Fact]
    public async Task DeferredConstraintViolatedAtCommit_WithoutATranslator_StillThrows() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var context = fixture.CreateContext();
        await context.Database.ExecuteSqlRawAsync(CreateTable, Ct);
        var decorator = new TransactionDecorator<InsertTwice, Result<string>>(
            new InsertTwiceHandler(context), new EfUnitOfWork<UnitOfWorkDbContext>(context), []);

        var act = async () => await decorator.HandleAsync(new InsertTwice(Guid.CreateVersion7().ToString("N")), Ct);

        (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
    }

    private sealed record InsertTwice(string Code) : ICommand;

    // Both inserts succeed inside the transaction; the deferred unique constraint fails only at COMMIT.
    private sealed class InsertTwiceHandler(UnitOfWorkDbContext context) : IHandler<InsertTwice, Result<string>> {
        public async ValueTask<Result<string>> HandleAsync(InsertTwice request, CancellationToken ct) {
            await context.Database.ExecuteSqlRawAsync("INSERT INTO deferred_codes (code) VALUES ({0})", [request.Code], ct);
            await context.Database.ExecuteSqlRawAsync("INSERT INTO deferred_codes (code) VALUES ({0})", [request.Code], ct);
            return Result<string>.Success(request.Code);
        }
    }

    private sealed class NotATranslator : IUnitOfWorkFailureTranslator {
        public AppError? Translate(Exception exception) => null;
    }

    private sealed class DuplicateCodeTranslator : IUnitOfWorkFailureTranslator {
        public AppError? Translate(Exception exception) =>
            exception is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "uq_deferred_code" }
                ? AppError.Conflict("The code is already taken.", "codes.duplicate")
                : null;
    }
}

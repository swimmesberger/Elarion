using AwesomeAssertions;
using Elarion.Abstractions;
using Xunit;

namespace Elarion.Tests.Abstractions;

public sealed class ResultUnitTests {
    [Fact]
    public void Unit_AllValuesAreEqual() {
        var a = Unit.Value;
        var b = default(Unit);

        a.Should().Be(b);
        (a == b).Should().BeTrue();
        (a != b).Should().BeFalse();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void Result_Success_IsSuccess() {
        var result = Result.Success();

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Result_Failure_CarriesError() {
        var error = AppError.NotFound("missing");

        var result = Result.Failure(error);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().BeSameAs(error);
    }

    [Fact]
    public void Result_ImplicitFromAppError_IsFailure() {
        Result result = AppError.Validation("bad");

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.Validation);
    }

    [Fact]
    public void Result_ConvertsToResultUnit_PreservingSuccess() {
        Result<Unit> success = Result.Success();
        Result<Unit> failure = Result.Failure(AppError.Conflict("dup"));

        success.IsSuccess.Should().BeTrue();
        success.Value.Should().Be(Unit.Value);
        failure.IsSuccess.Should().BeFalse();
        failure.Error.Kind.Should().Be(ErrorKind.Conflict);
    }

    [Fact]
    public async Task IHandlerOfT_BridgesToTwoArgViaDefaultInterfaceMethod() {
        IHandler<Ping, Result<Unit>> handler = new PingHandler();

        var ok = await handler.HandleAsync(new Ping(true), CancellationToken.None);
        var fail = await handler.HandleAsync(new Ping(false), CancellationToken.None);

        ok.IsSuccess.Should().BeTrue();
        ok.Value.Should().Be(Unit.Value);
        fail.IsSuccess.Should().BeFalse();
        fail.Error.Kind.Should().Be(ErrorKind.BusinessRule);
    }

    private sealed record Ping(bool Allow);

    private sealed class PingHandler : IHandler<Ping> {
        public ValueTask<Result> HandleAsync(Ping request, CancellationToken ct) =>
            ValueTask.FromResult(
                request.Allow ? Result.Success() : Result.Failure(AppError.BusinessRule("denied")));
    }
}

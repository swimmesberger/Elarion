using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Results;
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
    public void DefaultResult_ErrorIsInternalSentinel_NotNull() {
        // default(Result) is a failure with no backing error; transports translate Error, so the getter
        // must yield a well-defined internal-shaped sentinel instead of null.
        var result = default(Result);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().NotBeNull();
        result.Error.Kind.Should().Be(ErrorKind.Internal);
        result.Error.Message.Should().Contain("Uninitialized");
    }

    [Fact]
    public void DefaultResultOfT_ErrorIsInternalSentinel_NotNull() {
        var result = default(Result<string>);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().NotBeNull();
        result.Error.Kind.Should().Be(ErrorKind.Internal);
        result.Error.Message.Should().Contain("Uninitialized");
    }

    [Fact]
    public void ConstructedResults_KeepTheirOwnError() {
        var error = AppError.NotFound("missing");

        Result.Failure(error).Error.Should().BeSameAs(error);
        Result<string>.Failure(error).Error.Should().BeSameAs(error);
        Result<string>.Success("ok").IsSuccess.Should().BeTrue();
        Result<string>.Success("ok").Value.Should().Be("ok");
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
        public ValueTask<Result> HandleAsync(Ping request, CancellationToken ct) {
            return ValueTask.FromResult(
                request.Allow ? Result.Success() : Result.Failure(AppError.BusinessRule("denied")));
        }
    }
}

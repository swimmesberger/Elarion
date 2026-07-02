using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Pipeline;
using Elarion.Abstractions.Validation;
using Xunit;

namespace Elarion.Tests.Pipeline;

public sealed class ValidationDecoratorTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ValidRequest_PassesThroughToInner() {
        var inner = new RecordingHandler(Result<string>.Success("ok"));
        var decorator = new ValidationDecorator<TestCommand, Result<string>>(inner, new StubValidator(null));

        var result = await decorator.HandleAsync(new TestCommand(), Ct);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be("ok");
        inner.Calls.Should().Be(1);
    }

    [Fact]
    public async Task InvalidRequest_FailsWithValidationError_WithoutCallingInner() {
        var inner = new RecordingHandler(Result<string>.Success("never"));
        var errors = new RequestValidationErrors {
            FieldErrors = new Dictionary<string, string[]> {
                ["name"] = ["Name is too short"],
                ["address.street"] = ["Street is required", "Street is invalid"],
            },
        };
        var decorator = new ValidationDecorator<TestCommand, Result<string>>(inner, new StubValidator(errors));

        var result = await decorator.HandleAsync(new TestCommand(), Ct);

        result.IsSuccess.Should().BeFalse();
        inner.Calls.Should().Be(0);
        result.Error.Kind.Should().Be(ErrorKind.Validation);
        var data = result.Error.Data.Should().BeOfType<ValidationErrorData>().Subject;
        data.FieldErrors.Should().BeEquivalentTo(errors.FieldErrors);
    }

    [Fact]
    public async Task InvalidRequest_FlattensErrorsAndJoinsMessage_InOrdinalKeyOrder() {
        var errors = new RequestValidationErrors {
            FieldErrors = new Dictionary<string, string[]> {
                ["name"] = ["Name is too short"],
                ["address.street"] = ["Street is required", "Street is invalid"],
            },
        };
        var decorator = new ValidationDecorator<TestCommand, Result<string>>(
            new RecordingHandler(Result<string>.Success("never")), new StubValidator(errors));

        var result = await decorator.HandleAsync(new TestCommand(), Ct);

        var data = result.Error.Data.Should().BeOfType<ValidationErrorData>().Subject;
        data.Errors.Should().Equal("Street is required", "Street is invalid", "Name is too short");
        result.Error.Message.Should().Be("Street is required; Street is invalid; Name is too short");
    }

    [Fact]
    public async Task Validator_ReceivesDeclaredRequestTypeAndInstance() {
        var validator = new StubValidator(null);
        var decorator = new ValidationDecorator<TestCommand, Result<string>>(
            new RecordingHandler(Result<string>.Success("ok")), validator);
        var request = new TestCommand();

        await decorator.HandleAsync(request, Ct);

        validator.SeenType.Should().Be(typeof(TestCommand));
        validator.SeenRequest.Should().BeSameAs(request);
    }

    private sealed record TestCommand : ICommand;

    private sealed class RecordingHandler(Result<string> response) : IHandler<TestCommand, Result<string>> {
        public int Calls { get; private set; }

        public ValueTask<Result<string>> HandleAsync(TestCommand request, CancellationToken ct) {
            Calls++;
            return ValueTask.FromResult(response);
        }
    }

    private sealed class StubValidator(RequestValidationErrors? errors) : IRequestValidator {
        public Type? SeenType { get; private set; }
        public object? SeenRequest { get; private set; }

        public ValueTask<RequestValidationErrors?> ValidateAsync(Type requestType, object request, CancellationToken cancellationToken) {
            SeenType = requestType;
            SeenRequest = request;
            return ValueTask.FromResult(errors);
        }
    }
}

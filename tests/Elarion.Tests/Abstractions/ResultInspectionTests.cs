using AwesomeAssertions;
using Elarion.Abstractions;
using Xunit;

namespace Elarion.Tests.Abstractions;

public sealed class ResultInspectionTests {
    [Fact]
    public void SuccessfulResult_IsSuccess_AndHasNoError() {
        Result<int> response = 42;

        ResultInspection.IsSuccess(response).Should().BeTrue();
        ResultInspection.IsFailure(response).Should().BeFalse();
        ResultInspection.TryGetError(response, out var error).Should().BeFalse();
        error.Should().BeNull();
    }

    [Fact]
    public void FailedResult_IsFailure_AndExposesItsError() {
        Result<int> response = AppError.NotFound("Missing.");

        ResultInspection.IsSuccess(response).Should().BeFalse();
        ResultInspection.IsFailure(response).Should().BeTrue();
        ResultInspection.TryGetError(response, out var error).Should().BeTrue();
        error!.Kind.Should().Be(ErrorKind.NotFound);
    }

    [Fact]
    public void ResponseWithoutTheResultPattern_IsNeitherSuccessNorFailure() {
        ResultInspection.IsSuccess("plain").Should().BeFalse();
        ResultInspection.IsFailure("plain").Should().BeFalse();
        ResultInspection.IsFailure<string?>(null).Should().BeFalse();
        ResultInspection.TryGetError(17, out _).Should().BeFalse();
    }
}

using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.AspNetCore;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit;

namespace Elarion.Tests.AspNetCore;

/// <summary>
/// Tests <see cref="ElarionHttpResults"/>: success values map to 200/204 (or a file response for
/// <see cref="ElarionFile"/>) and an <see cref="AppError"/> maps to an RFC 7807 ProblemDetails result with the
/// status code from <see cref="HttpAppErrorMapper"/>.
/// </summary>
public sealed class ElarionHttpResultsTests {
    private sealed record Payload(string Value);

    [Fact]
    public void ToResult_Success_ReturnsOkWithValue() {
        var payload = new Payload("ok");

        var result = ElarionHttpResults.ToResult(Result<Payload>.Success(payload));

        result.Should().BeAssignableTo<IStatusCodeHttpResult>().Which.StatusCode.Should().Be(200);
        result.Should().BeAssignableTo<IValueHttpResult>().Which.Value.Should().Be(payload);
    }

    [Fact]
    public void ToResult_Failure_ReturnsProblemWithMappedStatus() {
        var result = ElarionHttpResults.ToResult(Result<Payload>.Failure(AppError.NotFound("missing")));

        result.Should().BeAssignableTo<IStatusCodeHttpResult>().Which.StatusCode.Should().Be(404);
    }

    [Fact]
    public void ToNoContentResult_Success_Returns204() {
        var result = ElarionHttpResults.ToNoContentResult(Result<Payload>.Success(new Payload("ignored")));

        result.Should().BeAssignableTo<IStatusCodeHttpResult>().Which.StatusCode.Should().Be(204);
    }

    [Fact]
    public void ToFileResult_Success_ReturnsFileContentWithMetadata() {
        var file = new ElarionFile(new byte[] { 1, 2, 3 }, "application/pdf") { FileName = "report.pdf" };

        var result = ElarionHttpResults.ToFileResult(Result<ElarionFile>.Success(file));

        var fileResult = result.Should().BeOfType<FileContentHttpResult>().Subject;
        fileResult.ContentType.Should().Be("application/pdf");
        fileResult.FileDownloadName.Should().Be("report.pdf");
        fileResult.FileContents.ToArray().Should().Equal(1, 2, 3);
    }

    [Fact]
    public void ToFileResult_WithoutFileName_ServesInline() {
        var file = new ElarionFile(new byte[] { 1 }, "image/png");

        var result = ElarionHttpResults.ToFileResult(Result<ElarionFile>.Success(file));

        result.Should().BeOfType<FileContentHttpResult>().Which.FileDownloadName.Should().BeNull();
    }

    [Fact]
    public void ToFileResult_Failure_ReturnsProblemWithMappedStatus() {
        var result = ElarionHttpResults.ToFileResult(Result<ElarionFile>.Failure(AppError.NotFound("no export")));

        result.Should().BeAssignableTo<IStatusCodeHttpResult>().Which.StatusCode.Should().Be(404);
    }

    [Fact]
    public void ToProblem_Validation_ReturnsValidationProblemWithErrors() {
        var error = AppError.Validation("invalid", ["Name is required", "Email is invalid"]);

        var result = ElarionHttpResults.ToProblem(error);

        result.Should().BeAssignableTo<IStatusCodeHttpResult>().Which.StatusCode.Should().Be(400);
        var value = result.Should().BeAssignableTo<IValueHttpResult>().Which.Value;
        var problem = value.Should().BeOfType<HttpValidationProblemDetails>().Subject;
        problem.Errors[string.Empty].Should().BeEquivalentTo("Name is required", "Email is invalid");
    }

    [Fact]
    public void ToProblem_ValidationWithFieldErrors_SurfacesThemAsProblemDetailsErrors() {
        var error = AppError.Validation("invalid", new Dictionary<string, string[]> {
            ["name"] = ["Name is required"],
            ["address.street"] = ["Street is too long"],
        });

        var result = ElarionHttpResults.ToProblem(error);

        result.Should().BeAssignableTo<IStatusCodeHttpResult>().Which.StatusCode.Should().Be(400);
        var value = result.Should().BeAssignableTo<IValueHttpResult>().Which.Value;
        var problem = value.Should().BeOfType<HttpValidationProblemDetails>().Subject;
        problem.Errors.Keys.Should().BeEquivalentTo("name", "address.street");
        problem.Errors["address.street"].Should().BeEquivalentTo("Street is too long");
        problem.Detail.Should().Be("invalid");
    }

    [Fact]
    public void ToProblem_NonValidation_ReturnsProblemWithDetail() {
        var result = ElarionHttpResults.ToProblem(AppError.Conflict("already exists"));

        result.Should().BeAssignableTo<IStatusCodeHttpResult>().Which.StatusCode.Should().Be(409);
        var value = result.Should().BeAssignableTo<IValueHttpResult>().Which.Value;
        value.Should().BeOfType<Microsoft.AspNetCore.Mvc.ProblemDetails>()
            .Which.Detail.Should().Be("already exists");
    }
}

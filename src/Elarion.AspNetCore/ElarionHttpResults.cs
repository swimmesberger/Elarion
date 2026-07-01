using System.Text.Json.Serialization.Metadata;
using Elarion.Abstractions;
using Elarion.Abstractions.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.AspNetCore;

/// <summary>
/// Translates an Elarion <see cref="Result{T}"/> into an ASP.NET Core <see cref="IResult"/>: success values
/// become <c>200 OK</c> (or <c>204 No Content</c>), and an <see cref="AppError"/> becomes an RFC 7807
/// ProblemDetails response whose status code comes from <see cref="HttpAppErrorMapper"/>.
/// </summary>
/// <remarks>
/// These helpers are invoked by the code emitted by <c>Elarion.Generators.AppModuleDiscoveryGenerator</c>; they
/// are public so the generated endpoint lambdas can call them. Validation failures carrying
/// <see cref="ValidationErrorData"/> are surfaced through the standard ProblemDetails <c>errors</c> map.
/// <para>
/// A successful value is serialized through the canonical <see cref="IElarionJsonSerialization"/> options — the
/// same options every other Elarion transport (JSON-RPC, MCP) uses — so REST output never diverges from those
/// surfaces for the same DTO. The accessor is resolved from the request's services at execution time and the
/// value is written with its source-generated <see cref="JsonTypeInfo"/>, so the path stays AOT-safe.
/// </para>
/// </remarks>
public static class ElarionHttpResults {
    /// <summary>Returns <c>200 OK</c> with <paramref name="result"/>'s value on success, otherwise a ProblemDetails failure.</summary>
    public static IResult ToResult<T>(Result<T> result) =>
        result.IsSuccess ? new ElarionJsonResult<T>(result.Value) : ToProblem(result.Error);

    /// <summary>Returns <c>204 No Content</c> on success, otherwise a ProblemDetails failure. Used when the response type is empty.</summary>
    public static IResult ToNoContentResult<T>(Result<T> result) =>
        result.IsSuccess ? Results.NoContent() : ToProblem(result.Error);

    /// <summary>Converts an <see cref="AppError"/> into an RFC 7807 ProblemDetails <see cref="IResult"/>.</summary>
    public static IResult ToProblem(AppError error) {
        var statusCode = HttpAppErrorMapper.MapToStatusCode(error.Kind);

        if (error is { Kind: ErrorKind.Validation, Data: ValidationErrorData validation }) {
            return Results.ValidationProblem(
                new Dictionary<string, string[]> { [string.Empty] = [.. validation.Errors] },
                detail: error.Message,
                statusCode: statusCode);
        }

        // Leave the title null so ASP.NET fills the canonical reason phrase for the status code.
        return Results.Problem(detail: error.Message, statusCode: statusCode);
    }

    /// <summary>
    /// Adds the ProblemDetails response metadata (for OpenAPI) covering every status code an Elarion handler
    /// failure can produce: 400 validation, 401, 403, 404, 409, 422, and 500.
    /// </summary>
    public static RouteHandlerBuilder ProducesElarionErrors(this RouteHandlerBuilder builder) =>
        builder
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status500InternalServerError);
}

/// <summary>
/// A <c>200 OK</c> JSON result that serializes its value through the canonical <see cref="IElarionJsonSerialization"/>
/// options (resolved from the request's services), so REST responses match the JSON-RPC/MCP transports for the same
/// DTO. Exposes the value and status code so callers can introspect the result without executing it.
/// </summary>
internal sealed class ElarionJsonResult<T>(T value) : IResult, IStatusCodeHttpResult, IValueHttpResult, IValueHttpResult<T> {
    /// <inheritdoc />
    public int? StatusCode => StatusCodes.Status200OK;

    /// <inheritdoc />
    public T? Value => value;

    /// <inheritdoc cref="IValueHttpResult.Value" />
    object? IValueHttpResult.Value => value;

    /// <inheritdoc />
    public async Task ExecuteAsync(HttpContext httpContext) {
        ArgumentNullException.ThrowIfNull(httpContext);

        var serialization = httpContext.RequestServices.GetService<IElarionJsonSerialization>();
        if (serialization is null) {
            // Canonical JSON was not wired (e.g. a host that maps [HttpEndpoint] routes without AddElarionJson).
            // Fall back to the framework's default OK result so REST still works; it simply uses ASP.NET's own
            // configured JSON options in that case.
            await Results.Ok(value).ExecuteAsync(httpContext);
            return;
        }

        var typeInfo = (JsonTypeInfo<T>)serialization.GetTypeInfo(typeof(T));

        httpContext.Response.StatusCode = StatusCodes.Status200OK;
        await httpContext.Response.WriteAsJsonAsync(value, typeInfo, cancellationToken: httpContext.RequestAborted);
    }
}

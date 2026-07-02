using Elarion.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

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
/// A successful value is written with <see cref="TypedResults.Ok{TValue}(TValue)"/>, so it serializes through the
/// minimal-API <c>Microsoft.AspNetCore.Http.Json</c> options. Call
/// <see cref="ElarionHttpJsonServiceCollectionExtensions.AddElarionHttpJson"/> (which
/// <c>AddElarionOpenApi</c> also does) to align those options with the canonical
/// <c>IElarionJsonSerialization</c> configuration, so REST output matches the JSON-RPC/MCP transports for the
/// same DTO and resolves through the source-generated contexts with reflection off.
/// </para>
/// </remarks>
public static class ElarionHttpResults {
    /// <summary>Returns <c>200 OK</c> with <paramref name="result"/>'s value on success, otherwise a ProblemDetails failure.</summary>
    public static IResult ToResult<T>(Result<T> result) =>
        result.IsSuccess ? TypedResults.Ok(result.Value) : ToProblem(result.Error);

    /// <summary>Returns <c>204 No Content</c> on success, otherwise a ProblemDetails failure. Used when the response type is empty.</summary>
    public static IResult ToNoContentResult<T>(Result<T> result) =>
        result.IsSuccess ? TypedResults.NoContent() : ToProblem(result.Error);

    /// <summary>Converts an <see cref="AppError"/> into an RFC 7807 ProblemDetails <see cref="IResult"/>.</summary>
    public static IResult ToProblem(AppError error) {
        var statusCode = HttpAppErrorMapper.MapToStatusCode(error.Kind);

        if (error is { Kind: ErrorKind.Validation, Data: ValidationErrorData validation }) {
            // Field-keyed errors surface under their wire-named field paths; a flat message list (no field
            // structure) falls back to the empty, non-field-specific key.
            var errors = validation.FieldErrors is { } fieldErrors
                ? new Dictionary<string, string[]>(fieldErrors)
                : new Dictionary<string, string[]> { [string.Empty] = [.. validation.Errors] };
            return Results.ValidationProblem(errors, detail: error.Message, statusCode: statusCode);
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

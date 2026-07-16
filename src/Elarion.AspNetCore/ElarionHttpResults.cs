using System.Security.Claims;
using Elarion;
using Elarion.Abstractions;
using Elarion.Abstractions.Dispatch;
using Elarion.Abstractions.Serialization;
using Elarion.AspNetCore.Streams;
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
/// A successful value is written with <see cref="TypedResults.Ok{TValue}(TValue)"/>, so it serializes through the
/// minimal-API <c>Microsoft.AspNetCore.Http.Json</c> options. Call
/// <see cref="ElarionHttpJsonServiceCollectionExtensions.AddElarionHttpJson"/> (which
/// <c>AddElarionOpenApi</c> also does) to align those options with the canonical
/// <c>IElarionJsonSerialization</c> configuration, so REST output matches the JSON-RPC/MCP transports for the
/// same DTO and resolves through the source-generated contexts with reflection off. The same call registers
/// ASP.NET's ProblemDetails services, whose source-generated <c>ProblemDetailsJsonContext</c> is what lets the
/// <see cref="ToProblem"/> error legs serialize with reflection off.
/// </para>
/// </remarks>
public static class ElarionHttpResults {
    /// <summary>Returns <c>200 OK</c> with <paramref name="result"/>'s value on success, otherwise a ProblemDetails failure.</summary>
    public static IResult ToResult<T>(Result<T> result) =>
        result.IsSuccess ? TypedResults.Ok(result.Value) : ToProblem(result.Error);

    /// <summary>Returns <c>204 No Content</c> on success, otherwise a ProblemDetails failure. Used when the response type is empty.</summary>
    public static IResult ToNoContentResult<T>(Result<T> result) =>
        result.IsSuccess ? TypedResults.NoContent() : ToProblem(result.Error);

    /// <summary>
    /// Returns the file content of a successful <see cref="ElarionFile"/> result as a real file response (the
    /// payload's bytes with its content type, and a <c>Content-Disposition: attachment</c> download name when
    /// set), otherwise a ProblemDetails failure. Used when the handler's response type is <see cref="ElarionFile"/>.
    /// </summary>
    public static IResult ToFileResult(Result<ElarionFile> result) {
        if (!result.IsSuccess) {
            return ToProblem(result.Error);
        }

        var file = result.Value;
        return TypedResults.Bytes(file.Bytes, file.ContentType, file.FileName);
    }

    /// <summary>
    /// Returns a lazy SSE result for a request-driven <see cref="IStreamHandler{TRequest,TItem}"/>. Bind the
    /// request with a direct minimal-API <c>MapGet</c> lambda so ASP.NET Core's Request Delegate Generator owns
    /// route and query binding. The decorated stream is started only when ASP.NET executes this result; startup
    /// failures remain normal ProblemDetails responses, and an accepted invocation owns its scope until
    /// enumeration completes, faults, or is cancelled.
    /// </summary>
    /// <typeparam name="TRequest">The handler request type.</typeparam>
    /// <typeparam name="TItem">The streamed item type, present in a canonical source-generated JSON context.</typeparam>
    /// <param name="request">The request passed to the generated stream-handler pipeline.</param>
    public static IResult ToStreamResult<TRequest, TItem>(TRequest request)
        where TRequest : notnull =>
        new StreamHandlerSseResult<TRequest, TItem>(request);

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

    private sealed class StreamHandlerSseResult<TRequest, TItem>(TRequest request) : IResult
        where TRequest : notnull {
        public async Task ExecuteAsync(HttpContext context) {
            var dispatch = new DispatchScopeContext();
            dispatch.Set<ClaimsPrincipal>(context.User);
            var started = await StreamHandlerInvoker.InvokeAsync<TRequest, TItem>(
                context.RequestServices, request, dispatch, context.RequestAborted).ConfigureAwait(false);
            if (!started.IsSuccess) {
                await ToProblem(started.Error).ExecuteAsync(context).ConfigureAwait(false);
                return;
            }

            await using var invocation = started.Value;
            var typeInfo = context.RequestServices.GetRequiredService<IElarionJsonSerialization>().GetTypeInfo<TItem>();
            var timeProvider = context.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;
            try {
                await StreamEndpointRouteBuilderExtensions.CreateHandlerStreamResult(
                        invocation, typeInfo, timeProvider, context.RequestAborted)
                    .ExecuteAsync(context)
                    .ConfigureAwait(false);
            } catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) {
                // Browser disconnects are normal termination; disposing the result releases the stream scope.
            } catch {
                // SSE cannot change to an HTTP problem once items have been written. Abort to make the terminal
                // fault visible to the client instead of falsely signalling a clean completion.
                context.Abort();
            }
        }
    }
}

using System.Diagnostics;
using System.Security.Claims;
using Elarion;
using Elarion.Abstractions;
using Elarion.Abstractions.Dispatch;
using Elarion.Abstractions.Serialization;
using Elarion.AspNetCore.Streams;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Elarion.AspNetCore;

/// <summary>
/// Translates an Elarion <see cref="Result{T}"/> into an ASP.NET Core <see cref="IResult"/>: success values
/// become <c>200 OK</c> (or <c>204 No Content</c>, or <c>201 Created</c> for an
/// <see cref="ElarionCreated{T}"/> envelope), and an <see cref="AppError"/> becomes an RFC 7807
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
    public static IResult ToResult<T>(Result<T> result) {
        return result.IsSuccess ? TypedResults.Ok(result.Value) : ToProblem(result.Error);
    }

    /// <summary>Returns <c>204 No Content</c> on success, otherwise a ProblemDetails failure. Used when the response type is empty.</summary>
    public static IResult ToNoContentResult<T>(Result<T> result) {
        return result.IsSuccess ? TypedResults.NoContent() : ToProblem(result.Error);
    }

    /// <summary>
    /// Returns <c>201 Created</c> with the envelope's inner value as the body (and its
    /// <see cref="ElarionCreated{T}.Location"/> as the <c>Location</c> header when set) on success, otherwise a
    /// ProblemDetails failure. Used when the handler's response type is <see cref="ElarionCreated{T}"/> — the
    /// envelope is peeled here, so it never appears on the wire.
    /// </summary>
    public static IResult ToCreatedResult<T>(Result<ElarionCreated<T>> result) {
        if (!result.IsSuccess) return ToProblem(result.Error);

        var created = result.Value;
        return TypedResults.Created(created.Location, created.Value);
    }

    /// <summary>
    /// Returns the file content of a successful <see cref="ElarionFile"/> result as a real file response (the
    /// payload's bytes with its content type, and a <c>Content-Disposition: attachment</c> download name when
    /// set), otherwise a ProblemDetails failure. Used when the handler's response type is <see cref="ElarionFile"/>.
    /// </summary>
    public static IResult ToFileResult(Result<ElarionFile> result) {
        if (!result.IsSuccess) return ToProblem(result.Error);

        var file = result.Value;
        return TypedResults.Bytes(file.Bytes, file.ContentType, file.FileName);
    }

    /// <summary>
    /// Returns a lazy SSE result for a request-driven <see cref="IStreamHandler{TRequest,TItem}"/>. Bind the
    /// request with a direct minimal-API <c>MapGet</c> lambda in the host compilation, where ASP.NET Core owns
    /// route and query binding. The decorated stream is started only when ASP.NET executes this result; startup
    /// failures remain normal ProblemDetails responses, and an accepted invocation owns its scope until
    /// enumeration completes, faults, or is cancelled.
    /// </summary>
    /// <typeparam name="TRequest">The handler request type.</typeparam>
    /// <typeparam name="TItem">The streamed item type, present in a canonical source-generated JSON context.</typeparam>
    /// <param name="request">The request passed to the generated stream-handler pipeline.</param>
    public static IResult ToStreamResult<TRequest, TItem>(TRequest request)
        where TRequest : notnull {
        return new StreamHandlerSseResult<TRequest, TItem>(request);
    }

    /// <summary>Converts an <see cref="AppError"/> into an RFC 7807 ProblemDetails <see cref="IResult"/>.</summary>
    public static IResult ToProblem(AppError error) {
        var statusCode = HttpAppErrorMapper.MapToStatusCode(error.Kind);

        if (error is { Kind: ErrorKind.Validation, Data: ValidationErrorData validation }) {
            // Field-keyed errors surface under their wire-named field paths; a flat message list (no field
            // structure) falls back to the empty, non-field-specific key.
            var errors = validation.FieldErrors is { } fieldErrors
                ? new Dictionary<string, string[]>(fieldErrors)
                : new Dictionary<string, string[]> { [string.Empty] = [.. validation.Errors] };
            return Results.ValidationProblem(
                errors, error.Message, statusCode: statusCode, extensions: HttpAppErrorMapper.Extensions(error));
        }

        // Leave the title null so ASP.NET fills the canonical reason phrase for the status code.
        return Results.Problem(error.Message, statusCode: statusCode, extensions: HttpAppErrorMapper.Extensions(error));
    }

    /// <summary>
    /// Whether a generated endpoint answers <paramref name="exception"/> itself — everything except the cancellation of
    /// an aborted request, which is the client leaving, not a server failure, and gets no response, and an exception
    /// <see cref="UnexpectedErrorOptions.LeaveToHost"/> hands to the host's exception handling. Used as the catch's
    /// filter, so an exception left to the host propagates with its stack intact.
    /// </summary>
    public static bool IsUnexpected(HttpContext context, Exception exception) {
        ArgumentNullException.ThrowIfNull(context);
        if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested) return false;

        return !UnexpectedErrorOptions.LeavesToHost(new UnexpectedErrorContext {
            Exception = exception,
            Transport = UnexpectedErrorTransports.Http,
            Operation = context.GetEndpoint()?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName,
            Services = context.RequestServices
        });
    }

    /// <summary>
    /// The <c>500</c> ProblemDetails a generated <c>[HttpEndpoint]</c> answers for an exception that escaped the
    /// handler pipeline: the <see cref="UnexpectedErrorOptions"/> message as <c>detail</c>, the code
    /// <see cref="ErrorCodes.Internal"/> and the support reference as the <c>reference</c> member — the same answer
    /// the JSON-RPC and MCP transports give. The exception is logged at error level with the reference and recorded
    /// on the current activity; it does not reach the host's exception-handling middleware unless
    /// <see cref="UnexpectedErrorOptions.LeaveToHost"/> says so.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="exception">The exception the endpoint caught.</param>
    /// <param name="operation">The endpoint name, passed to the options hooks and the log.</param>
    public static IResult ToUnexpectedProblem(HttpContext context, Exception exception, string operation) {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(exception);

        var unexpected = UnexpectedErrorOptions.Describe(new UnexpectedErrorContext {
            Exception = exception, Transport = UnexpectedErrorTransports.Http, Operation = operation,
            Services = context.RequestServices
        });
        context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger(UnexpectedErrorLoggerCategory)
            .LogError(exception, "Unhandled exception in HTTP endpoint {Endpoint} (reference {ErrorReference})",
                operation, unexpected.Reference);
        if (Activity.Current is { } activity) {
            activity.AddException(exception);
            activity.SetTag("elarion.error.reference", unexpected.Reference);
        }

        var extensions = new Dictionary<string, object?> { [HttpAppErrorMapper.CodeExtensionName] = ErrorCodes.Internal };
        if (unexpected.Reference is { } reference) extensions[HttpAppErrorMapper.ReferenceExtensionName] = reference;

        return Results.Problem(unexpected.Message, statusCode: StatusCodes.Status500InternalServerError,
            extensions: extensions);
    }

    private const string UnexpectedErrorLoggerCategory = "Elarion.AspNetCore.HttpEndpoints";

    /// <summary>
    /// Adds the ProblemDetails response metadata (for OpenAPI) covering every status code an Elarion handler
    /// failure can produce: 400 validation, 401, 403, 404, 409, 422, and 500.
    /// </summary>
    /// <remarks>
    /// Generic over the builder because the generated <c>[HttpEndpoint]</c> registrations use the AOT-safe
    /// <c>Map*(string, RequestDelegate)</c> overloads, which return <see cref="IEndpointConventionBuilder"/>
    /// rather than <see cref="RouteHandlerBuilder"/> (ADR-0071).
    /// </remarks>
    public static TBuilder ProducesElarionErrors<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder {
        return builder
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status500InternalServerError);
    }

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
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) {
                // Browser disconnects are normal termination; disposing the result releases the stream scope.
            }
            catch {
                // SSE cannot change to an HTTP problem once items have been written. Abort to make the terminal
                // fault visible to the client instead of falsely signalling a clean completion.
                context.Abort();
            }
        }
    }
}

using System.Security.Claims;
using Elarion.Abstractions;
using Elarion.Abstractions.Dispatch;
using Grpc.Core;

namespace Elarion.Grpc;

/// <summary>
/// Invokes an Elarion handler from a unary gRPC service override while keeping protobuf mapping and
/// authentication application-owned.
/// </summary>
/// <remarks>
/// The hosting application configures an <see cref="IGrpcPrincipalFactory"/> once. Each service method then supplies
/// only its generated request, the exact <see cref="ServerCallContext"/>, and explicit mappings between wire and
/// application types. This adapter creates the per-call dispatch context, flows cancellation, then uses
/// <see cref="HandlerInvoker"/> so the decorated handler chain remains the only path to application code. It does
/// not host gRPC or infer protobuf mappings. Streaming calls are intentionally outside this unary-only API.
/// </remarks>
public sealed class GrpcHandlerInvoker(
    IServiceProvider services,
    IGrpcPrincipalFactory principalFactory,
    IAppErrorTranslator<RpcException> errorTranslator) {
    /// <summary>
    /// Maps and invokes a unary gRPC request, returning its mapped success response or throwing the translated
    /// <see cref="RpcException"/> for a failed <see cref="Result{T}"/>.
    /// </summary>
    /// <typeparam name="TWireRequest">The generated protobuf request type.</typeparam>
    /// <typeparam name="TWireResponse">The generated protobuf response type.</typeparam>
    /// <typeparam name="TRequest">The application handler request type.</typeparam>
    /// <typeparam name="TResponse">The application handler success type.</typeparam>
    /// <param name="wireRequest">The request received from gRPC.</param>
    /// <param name="callContext">The exact gRPC context for this call.</param>
    /// <param name="mapRequest">Maps the protobuf request into the application request.</param>
    /// <param name="mapResponse">Maps the successful application response into the protobuf response.</param>
    /// <returns>The mapped gRPC response.</returns>
    /// <exception cref="RpcException">The registered gRPC translator's representation of a failed result.</exception>
    public async Task<TWireResponse> InvokeUnaryAsync<TWireRequest, TWireResponse, TRequest, TResponse>(
        TWireRequest wireRequest,
        ServerCallContext callContext,
        Func<TWireRequest, TRequest> mapRequest,
        Func<TResponse, TWireResponse> mapResponse)
        where TRequest : notnull {
        ArgumentNullException.ThrowIfNull(callContext);
        ArgumentNullException.ThrowIfNull(mapRequest);
        ArgumentNullException.ThrowIfNull(mapResponse);

        var principal = principalFactory.CreatePrincipal(callContext);
        ArgumentNullException.ThrowIfNull(principal);

        var context = new DispatchScopeContext();
        context.Set(principal);
        context.Set(callContext);

        var result = await HandlerInvoker.InvokeAsync<TRequest, TResponse>(
            services,
            mapRequest(wireRequest),
            context,
            callContext.CancellationToken).ConfigureAwait(false);

        if (result.IsSuccess) {
            return mapResponse(result.Value);
        }

        throw errorTranslator.Translate(result.Error);
    }
}

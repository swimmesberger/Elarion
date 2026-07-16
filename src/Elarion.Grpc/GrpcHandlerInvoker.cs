using System.Security.Claims;
using Elarion.Abstractions;
using Elarion.Abstractions.Dispatch;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.Grpc;

/// <summary>
/// Invokes an Elarion handler from a unary gRPC service override while keeping protobuf mapping and
/// authentication application-owned.
/// </summary>
/// <remarks>
/// A service method supplies its already-authenticated <see cref="ClaimsPrincipal"/> and explicit mappings between
/// its generated wire types and application types. This adapter creates the per-call dispatch context, flows the
/// exact <see cref="ServerCallContext"/> and its cancellation token, then uses <see cref="HandlerInvoker"/> so the
/// decorated handler chain remains the only path to application code. It does not host gRPC or infer protobuf
/// mappings. Streaming calls are intentionally outside this unary-only API.
/// </remarks>
public static class GrpcHandlerInvoker {
    /// <summary>
    /// Maps and invokes a unary gRPC request, returning its mapped success response or throwing the translated
    /// <see cref="RpcException"/> for a failed <see cref="Result{T}"/>.
    /// </summary>
    /// <typeparam name="TWireRequest">The generated protobuf request type.</typeparam>
    /// <typeparam name="TWireResponse">The generated protobuf response type.</typeparam>
    /// <typeparam name="TRequest">The application handler request type.</typeparam>
    /// <typeparam name="TResponse">The application handler success type.</typeparam>
    /// <param name="rootProvider">The application provider that owns the decorated handler registrations.</param>
    /// <param name="wireRequest">The request received from gRPC.</param>
    /// <param name="callContext">The exact gRPC context for this call.</param>
    /// <param name="principal">The principal authenticated by the hosting application.</param>
    /// <param name="mapRequest">Maps the protobuf request into the application request.</param>
    /// <param name="mapResponse">Maps the successful application response into the protobuf response.</param>
    /// <returns>The mapped gRPC response.</returns>
    /// <exception cref="RpcException">The registered gRPC translator's representation of a failed result.</exception>
    public static async ValueTask<TWireResponse> InvokeUnaryAsync<TWireRequest, TWireResponse, TRequest, TResponse>(
        IServiceProvider rootProvider,
        TWireRequest wireRequest,
        ServerCallContext callContext,
        ClaimsPrincipal principal,
        Func<TWireRequest, TRequest> mapRequest,
        Func<TResponse, TWireResponse> mapResponse)
        where TRequest : notnull {
        ArgumentNullException.ThrowIfNull(rootProvider);
        ArgumentNullException.ThrowIfNull(callContext);
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(mapRequest);
        ArgumentNullException.ThrowIfNull(mapResponse);

        var context = new DispatchScopeContext();
        context.Set(principal);
        context.Set(callContext);

        var result = await HandlerInvoker.InvokeAsync<TRequest, TResponse>(
            rootProvider,
            mapRequest(wireRequest),
            context,
            callContext.CancellationToken).ConfigureAwait(false);

        if (result.IsSuccess) {
            return mapResponse(result.Value);
        }

        var translator = rootProvider.GetService<IAppErrorTranslator<RpcException>>()
            ?? GrpcAppErrorTranslator.Default;
        throw translator.Translate(result.Error);
    }
}

using System.Security.Claims;
using Elarion.Abstractions;
using Elarion.Abstractions.Dispatch;
using Grpc.Core;

namespace Elarion.Grpc;

/// <summary>
/// Starts request-driven Elarion server streams from a gRPC service override while keeping protobuf mapping and
/// authentication application-owned.
/// </summary>
/// <remarks>
/// The hosting application configures an <see cref="IGrpcPrincipalFactory"/> once. This adapter creates the
/// per-call dispatch context, flows cancellation, and uses <see cref="StreamHandlerInvoker"/> so the decorated
/// stream-handler chain remains the only path to application code. The returned invocation owns its scope and
/// must be disposed after enumeration. Client and duplex streaming are not part of this adapter.
/// </remarks>
public sealed class GrpcStreamHandlerInvoker(
    IServiceProvider services,
    IGrpcPrincipalFactory principalFactory,
    IAppErrorTranslator<RpcException> errorTranslator)
{
    /// <summary>
    /// Starts an application server stream directly, returning an invocation that owns its dispatch scope or
    /// throwing the translated <see cref="RpcException"/> when stream startup returns a failed result.
    /// </summary>
    /// <typeparam name="TRequest">The application stream-handler request type.</typeparam>
    /// <typeparam name="TItem">The application stream item type.</typeparam>
    /// <param name="request">The application request to dispatch.</param>
    /// <param name="callContext">The exact gRPC context for this call.</param>
    /// <returns>The accepted stream invocation. Dispose it after enumeration.</returns>
    /// <exception cref="RpcException">The registered gRPC translator's representation of a failed stream startup.</exception>
    public async Task<StreamHandlerInvocation<TItem>> InvokeServerStreamingAsync<TRequest, TItem>(
        TRequest request,
        ServerCallContext callContext)
        where TRequest : notnull
    {
        ArgumentNullException.ThrowIfNull(callContext);

        var principal = principalFactory.CreatePrincipal(callContext);
        ArgumentNullException.ThrowIfNull(principal);

        var context = new DispatchScopeContext();
        context.Set(principal);
        context.Set(callContext);

        var result = await StreamHandlerInvoker.InvokeAsync<TRequest, TItem>(
            services,
            request,
            context,
            callContext.CancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            return result.Value;
        }

        throw errorTranslator.Translate(result.Error);
    }

    /// <summary>
    /// Maps a gRPC request, writes every successful application stream item to <paramref name="responseStream"/>,
    /// and disposes the dispatch scope when enumeration ends.
    /// </summary>
    /// <remarks>
    /// A failed stream startup is translated before an item is written. Once an item has been written, gRPC owns
    /// terminal status reporting; exceptions raised by lazy enumeration propagate to grpc-dotnet unchanged.
    /// </remarks>
    /// <typeparam name="TWireRequest">The generated protobuf request type.</typeparam>
    /// <typeparam name="TWireItem">The generated protobuf response item type.</typeparam>
    /// <typeparam name="TRequest">The application stream-handler request type.</typeparam>
    /// <typeparam name="TItem">The application stream item type.</typeparam>
    /// <param name="wireRequest">The request received from gRPC.</param>
    /// <param name="responseStream">The gRPC response stream writer.</param>
    /// <param name="callContext">The exact gRPC context for this call.</param>
    /// <param name="mapRequest">Maps the protobuf request into the application request.</param>
    /// <param name="mapItem">Maps an application stream item into the protobuf response item.</param>
    /// <returns>A task that completes when the stream ends, faults, or is cancelled.</returns>
    /// <exception cref="RpcException">The registered translator's representation of a failed stream startup.</exception>
    public async Task InvokeServerStreamingAsync<TWireRequest, TWireItem, TRequest, TItem>(
        TWireRequest wireRequest,
        IServerStreamWriter<TWireItem> responseStream,
        ServerCallContext callContext,
        Func<TWireRequest, TRequest> mapRequest,
        Func<TItem, TWireItem> mapItem)
        where TRequest : notnull
    {
        ArgumentNullException.ThrowIfNull(responseStream);
        ArgumentNullException.ThrowIfNull(callContext);
        ArgumentNullException.ThrowIfNull(mapRequest);
        ArgumentNullException.ThrowIfNull(mapItem);

        await using var invocation = await InvokeServerStreamingAsync<TRequest, TItem>(
            mapRequest(wireRequest),
            callContext).ConfigureAwait(false);

        await foreach (var item in invocation
            .WithCancellation(callContext.CancellationToken)
            .ConfigureAwait(false))
        {
            await responseStream.WriteAsync(mapItem(item)).ConfigureAwait(false);
        }
    }
}

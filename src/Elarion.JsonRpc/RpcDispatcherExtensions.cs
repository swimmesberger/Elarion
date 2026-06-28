using Elarion.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.JsonRpc;

/// <summary>
/// Bridges Elarion <see cref="IHandler{TRequest,TResponse}"/> handlers into the transport-neutral
/// <see cref="JsonRpcDispatcher"/>.
/// </summary>
/// <remarks>
/// This glue lives in the <c>Elarion</c> application-runtime package — not <c>Elarion.JsonRpc</c> — so the
/// JSON-RPC core stays free of the handler / <see cref="Result{T}"/> / <see cref="AppError"/> contract and
/// remains usable with the raw <see cref="JsonRpcDispatcher.Map{TRequest,TResponse}"/> delegate API.
/// The generated <c>ModuleBootstrapper.RegisterRpcMethods</c> body (see
/// <c>Elarion.Generators.AppModuleDiscoveryGenerator</c>) emits one
/// <see cref="MapHandler{TRequest,TResponse}"/> call per discovered handler.
/// </remarks>
public static class RpcDispatcherExtensions {
    /// <summary>
    /// Registers an <see cref="IHandler{TRequest,TResponse}"/> as a JSON-RPC method. The handler is resolved
    /// from the per-request <see cref="IServiceProvider"/> on each dispatch; its <see cref="Result{T}"/> is
    /// mapped to <see cref="RpcResult{T}"/>, with failures translated to JSON-RPC error codes via
    /// <see cref="AppErrorMapper"/>.
    /// </summary>
    /// <typeparam name="TRequest">The request type deserialized from the JSON-RPC params.</typeparam>
    /// <typeparam name="TResponse">The handler success value type, captured for schema export.</typeparam>
    /// <param name="dispatcher">The dispatcher to register the method on.</param>
    /// <param name="methodName">The JSON-RPC method name (e.g. <c>"clients.create"</c>).</param>
    /// <returns>The same dispatcher, for fluent chaining.</returns>
    public static JsonRpcDispatcher MapHandler<TRequest, TResponse>(
        this JsonRpcDispatcher dispatcher,
        string methodName)
        where TRequest : class =>
        dispatcher.Map<TRequest, TResponse>(
            methodName,
            async (request, serviceProvider, ct) => {
                var handler = serviceProvider.GetRequiredService<IHandler<TRequest, Result<TResponse>>>();
                var result = await handler.HandleAsync(request, ct);
                if (result.IsSuccess) {
                    return RpcResult<TResponse>.Success(result.Value);
                }

                // Failures go through the registered IAppErrorTranslator<RpcError> (a host can override the
                // JSON-RPC error codes that way); the default maps via AppErrorMapper.
                var translator = serviceProvider.GetService<IAppErrorTranslator<RpcError>>()
                    ?? JsonRpcAppErrorTranslator.Default;
                return RpcResult<TResponse>.Failure(translator.Translate(result.Error));
            });
}

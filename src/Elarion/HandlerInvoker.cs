using Elarion.Abstractions;
using Elarion.Abstractions.Dispatch;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion;

/// <summary>
/// The typed-direct entry point for invoking an application handler from a transport: it creates a seeded
/// per-call DI scope, resolves the fully-decorated <see cref="IHandler{TRequest,TResponse}"/>, invokes it, and
/// owns the scope's disposal. This is the sibling of the JSON-RPC/MCP name-based dispatch path
/// (<c>JsonRpcDispatcher</c> / <c>RpcToolInvoker</c>) for transports that already know the static handler type
/// (e.g. gRPC mapping a proto to a request, or a console command).
/// </summary>
/// <remarks>
/// The handler runs through its full decorator pipeline (tracing, validation, authorization, resilience,
/// caching) because the decorated chain is what DI resolves for <see cref="IHandler{TRequest,TResponse}"/>.
/// Pass a <see cref="DispatchScopeContext"/> carrying the caller's identity (and any other per-call state) so
/// the registered <see cref="IDispatchScopeInitializer"/> instances seed the scope before the handler runs —
/// e.g. <c>new DispatchScopeContext().Set&lt;ClaimsPrincipal&gt;(principal)</c> makes <c>ICurrentUser</c>
/// resolve inside the handler.
/// </remarks>
public static class HandlerInvoker {
    /// <summary>
    /// Invokes the handler for <paramref name="request"/> in a fresh seeded scope created from
    /// <paramref name="rootProvider"/> and returns its <see cref="Result{T}"/>.
    /// </summary>
    /// <typeparam name="TRequest">The handler request type.</typeparam>
    /// <typeparam name="TResponse">The handler success value type.</typeparam>
    /// <param name="rootProvider">The provider to create the per-call scope from (the app root, or a request scope).</param>
    /// <param name="request">The request to handle.</param>
    /// <param name="context">The values captured at the call boundary (e.g. the authenticated principal), or <see langword="null"/>.</param>
    /// <param name="ct">A cancellation token flowed into the handler.</param>
    /// <returns>The handler's <see cref="Result{T}"/>.</returns>
    public static async ValueTask<Result<TResponse>> InvokeAsync<TRequest, TResponse>(
        IServiceProvider rootProvider,
        TRequest request,
        DispatchScopeContext? context = null,
        CancellationToken ct = default)
        where TRequest : notnull {
        ArgumentNullException.ThrowIfNull(rootProvider);

        await using var scope = rootProvider.CreateDispatchScope(context);
        var handler = scope.ServiceProvider.GetRequiredService<IHandler<TRequest, Result<TResponse>>>();
        return await handler.HandleAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Fully inferred invoke for requests implementing the self-typed marker
    /// <see cref="IRequest{TSelf, TResponse}"/>: both generic arguments are inferred from
    /// <paramref name="request"/> — <c>await HandlerInvoker.InvokeAsync(provider, new GetClient.Query(id), context, ct)</c>.
    /// Dispatches through the same typed resolution as
    /// <see cref="InvokeAsync{TRequest, TResponse}(IServiceProvider, TRequest, DispatchScopeContext?, CancellationToken)"/>.
    /// </summary>
    /// <typeparam name="TRequest">The handler request type (inferred).</typeparam>
    /// <typeparam name="TResponse">The handler success value type (inferred from the marker).</typeparam>
    /// <param name="rootProvider">The provider to create the per-call scope from (the app root, or a request scope).</param>
    /// <param name="request">The request to handle.</param>
    /// <param name="context">The values captured at the call boundary (e.g. the authenticated principal), or <see langword="null"/>.</param>
    /// <param name="ct">A cancellation token flowed into the handler.</param>
    /// <returns>The handler's <see cref="Result{T}"/>.</returns>
    public static ValueTask<Result<TResponse>> InvokeAsync<TRequest, TResponse>(
        IServiceProvider rootProvider,
        IRequest<TRequest, TResponse> request,
        DispatchScopeContext? context = null,
        CancellationToken ct = default)
        where TRequest : notnull, IRequest<TRequest, TResponse> {
        ArgumentNullException.ThrowIfNull(request);

        return InvokeAsync<TRequest, TResponse>(rootProvider, (TRequest)request, context, ct);
    }
}

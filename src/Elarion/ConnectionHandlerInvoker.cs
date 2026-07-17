using System.Security.Claims;
using Elarion.Abstractions;
using Elarion.Abstractions.Connections;
using Elarion.Abstractions.Dispatch;

namespace Elarion;

/// <summary>
/// Invokes handlers on behalf of one bidirectional client connection while seeding a fresh dispatch scope with
/// the connection identity and optional adapter-owned metadata.
/// </summary>
/// <remarks>
/// <para>
/// Each operation reads <see cref="IClientConnectionSink.Connection"/> exactly once before enrichment or
/// dispatch. Identity promotion atomically publishes immutable snapshots, so an in-flight dispatch keeps the
/// captured principal and connection while the next dispatch observes the promoted snapshot.
/// </para>
/// <para>
/// Adapter enrichment runs first. The invoker then replaces the exact-type <see cref="ClaimsPrincipal"/> and
/// <see cref="ClientConnection"/> entries with the captured framework snapshot, preventing enrichment from
/// spoofing framework identity while preserving every other typed metadata entry.
/// </para>
/// <para>
/// Handler failures remain <see cref="Result{T}"/> values. This helper performs no serialization, protocol
/// mapping, or error translation; the connection adapter owns those wire concerns.
/// </para>
/// </remarks>
public static class ConnectionHandlerInvoker {
    private const string NotFoundMessage = "The requested resource was not found.";

    /// <summary>
    /// Invokes a typed unary handler through its decorated DI pipeline in a fresh scope seeded from the captured
    /// connection snapshot.
    /// </summary>
    /// <typeparam name="TRequest">The handler request type.</typeparam>
    /// <typeparam name="TResponse">The handler success value type.</typeparam>
    /// <param name="rootProvider">The provider from which the per-dispatch scope is created.</param>
    /// <param name="connection">The connection sink whose immutable identity snapshot is captured once.</param>
    /// <param name="request">The already-decoded application request.</param>
    /// <param name="enrichContext">
    /// An optional callback that adds adapter-owned typed metadata before protected framework identity entries
    /// are applied. Exceptions propagate and no handler is invoked.
    /// </param>
    /// <param name="ct">A cancellation token flowed unchanged into the handler pipeline.</param>
    /// <returns>The handler's success or failure as a <see cref="Result{T}"/> value.</returns>
    public static ValueTask<Result<TResponse>> InvokeAsync<TRequest, TResponse>(
        IServiceProvider rootProvider,
        IClientConnectionSink connection,
        TRequest request,
        Action<DispatchScopeContext>? enrichContext = null,
        CancellationToken ct = default)
        where TRequest : notnull {
        ArgumentNullException.ThrowIfNull(rootProvider);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(request);

        var snapshot = connection.Connection;
        var context = CreateContext(snapshot, enrichContext);
        return HandlerInvoker.InvokeAsync<TRequest, TResponse>(rootProvider, request, context, ct);
    }

    /// <summary>
    /// Starts a typed request-driven stream through its decorated DI pipeline in a fresh scope seeded from the
    /// captured connection snapshot.
    /// </summary>
    /// <typeparam name="TRequest">The stream handler request type.</typeparam>
    /// <typeparam name="TItem">The stream item type.</typeparam>
    /// <param name="rootProvider">The provider from which the per-dispatch scope is created.</param>
    /// <param name="connection">The connection sink whose immutable identity snapshot is captured once.</param>
    /// <param name="request">The already-decoded application request.</param>
    /// <param name="enrichContext">
    /// An optional callback that adds adapter-owned typed metadata before protected framework identity entries
    /// are applied. Exceptions propagate and no stream handler is invoked.
    /// </param>
    /// <param name="ct">A cancellation token flowed unchanged into stream startup.</param>
    /// <returns>
    /// A failed startup result, or an accepted <see cref="StreamHandlerInvocation{TItem}"/> that owns the dispatch
    /// scope through lazy enumeration. Terminal enumeration disposes the scope; callers that do not enumerate an
    /// accepted stream must dispose the invocation explicitly. A startup failure disposes the scope immediately.
    /// </returns>
    public static ValueTask<Result<StreamHandlerInvocation<TItem>>> InvokeStreamAsync<TRequest, TItem>(
        IServiceProvider rootProvider,
        IClientConnectionSink connection,
        TRequest request,
        Action<DispatchScopeContext>? enrichContext = null,
        CancellationToken ct = default)
        where TRequest : notnull {
        ArgumentNullException.ThrowIfNull(rootProvider);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(request);

        var snapshot = connection.Connection;
        var context = CreateContext(snapshot, enrichContext);
        return StreamHandlerInvoker.InvokeAsync<TRequest, TItem>(rootProvider, request, context, ct);
    }

    /// <summary>
    /// Invokes an already-decoded named request only when its frozen route is exposed to bidirectional
    /// connections.
    /// </summary>
    /// <param name="rootProvider">The provider from which the one per-dispatch scope is created.</param>
    /// <param name="connection">The connection sink whose immutable identity snapshot is captured once.</param>
    /// <param name="dispatcher">The frozen handler routing table.</param>
    /// <param name="name">The case-insensitive operation name.</param>
    /// <param name="request">The request object already decoded by the connection adapter.</param>
    /// <param name="enrichContext">
    /// An optional callback that adds adapter-owned typed metadata before protected framework identity entries
    /// are applied. Exceptions propagate and no route is invoked.
    /// </param>
    /// <param name="ct">A cancellation token flowed unchanged into the route.</param>
    /// <returns>
    /// The route's boxed success or failure result. Unknown names and routes exposed only to another transport
    /// return the same generic <see cref="AppError.NotFound(string)"/> value so transport exposure is not leaked.
    /// No exception translation is performed.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="request"/> is not assignable to the selected route's declared request type, indicating an
    /// adapter decoding bug.
    /// </exception>
    public static async ValueTask<Result<object>> InvokeNamedAsync(
        IServiceProvider rootProvider,
        IClientConnectionSink connection,
        HandlerDispatcher dispatcher,
        string name,
        object request,
        Action<DispatchScopeContext>? enrichContext = null,
        CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(rootProvider);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(request);

        var snapshot = connection.Connection;
        if (!dispatcher.TryGetRoute(name, HandlerTransports.Connection, out var route))
            return AppError.NotFound(NotFoundMessage);

        if (!route.RequestType.IsInstanceOfType(request)) {
            throw new ArgumentException(
                $"The decoded request for connection operation '{name}' must be assignable to " +
                $"'{route.RequestType.FullName}', but the adapter supplied '{request.GetType().FullName}'.",
                nameof(request));
        }

        var context = CreateContext(snapshot, enrichContext);
        await using var scope = rootProvider.CreateDispatchScope(context);
        return await route.InvokeAsync(request, scope.ServiceProvider, ct).ConfigureAwait(false);
    }

    private static DispatchScopeContext CreateContext(
        ClientConnection snapshot,
        Action<DispatchScopeContext>? enrichContext) {
        ArgumentNullException.ThrowIfNull(snapshot);

        var context = new DispatchScopeContext();
        enrichContext?.Invoke(context);
        context.Set<ClaimsPrincipal>(snapshot.Principal);
        context.Set<ClientConnection>(snapshot);
        return context;
    }
}

using System.Security.Claims;
using Elarion.Abstractions;
using Elarion.Abstractions.Connections;
using Elarion.Abstractions.Dispatch;

namespace Elarion;

/// <summary>
/// Invokes handlers on behalf of one bidirectional client connection while seeding a fresh dispatch scope with
/// the connection identity and optional adapter-owned metadata. Bind it once per connection —
/// <c>var invoker = new ConnectionHandlerInvoker(services, connection)</c> — then dispatch decoded requests:
/// <c>await invoker.InvokeAsync(new ResolveWorldSession.Query(account), ct)</c> for requests carrying the
/// self-typed <see cref="IRequest{TSelf, TResponse}"/> marker, or the explicit
/// <c>InvokeAsync&lt;TRequest, TResponse&gt;</c> form for marker-free requests.
/// </summary>
/// <remarks>
/// <para>
/// The invoker holds the connection <em>sink</em>, not a snapshot: each operation reads
/// <see cref="IClientConnectionSink.Connection"/> exactly once before enrichment or dispatch. Identity
/// promotion atomically publishes immutable snapshots, so an in-flight dispatch keeps the captured principal
/// and connection while the next dispatch observes the promoted snapshot.
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
public sealed class ConnectionHandlerInvoker {
    private const string NotFoundMessage = "The requested resource was not found.";

    private readonly IServiceProvider _services;
    private readonly IClientConnectionSink _connection;

    /// <summary>Binds the invoker to one connection for its lifetime.</summary>
    /// <param name="services">The root provider from which each per-dispatch scope is created.</param>
    /// <param name="connection">The connection sink whose immutable identity snapshot is captured per dispatch.</param>
    public ConnectionHandlerInvoker(IServiceProvider services, IClientConnectionSink connection) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(connection);
        _services = services;
        _connection = connection;
    }

    /// <summary>
    /// Invokes a typed unary handler through its decorated DI pipeline in a fresh scope seeded from the captured
    /// connection snapshot.
    /// </summary>
    /// <typeparam name="TRequest">The handler request type.</typeparam>
    /// <typeparam name="TResponse">The handler success value type.</typeparam>
    /// <param name="request">The already-decoded application request.</param>
    /// <param name="ct">A cancellation token flowed unchanged into the handler pipeline.</param>
    /// <returns>The handler's success or failure as a <see cref="Result{T}"/> value.</returns>
    public ValueTask<Result<TResponse>> InvokeAsync<TRequest, TResponse>(
        TRequest request,
        CancellationToken ct = default)
        where TRequest : notnull {
        ArgumentNullException.ThrowIfNull(request);

        var context = CreateContext(_connection.Connection, enrichContext: null);
        return HandlerInvoker.InvokeAsync<TRequest, TResponse>(_services, request, context, ct);
    }

    /// <summary>
    /// Invokes a typed unary handler with adapter-owned metadata added to the dispatch scope before the
    /// protected framework identity entries are applied.
    /// </summary>
    /// <typeparam name="TRequest">The handler request type.</typeparam>
    /// <typeparam name="TResponse">The handler success value type.</typeparam>
    /// <param name="request">The already-decoded application request.</param>
    /// <param name="enrichContext">
    /// Adds adapter-owned typed metadata before protected framework identity entries are applied. Exceptions
    /// propagate and no handler is invoked.
    /// </param>
    /// <param name="ct">A cancellation token flowed unchanged into the handler pipeline.</param>
    /// <returns>The handler's success or failure as a <see cref="Result{T}"/> value.</returns>
    public ValueTask<Result<TResponse>> InvokeAsync<TRequest, TResponse>(
        TRequest request,
        Action<DispatchScopeContext> enrichContext,
        CancellationToken ct = default)
        where TRequest : notnull {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(enrichContext);

        var context = CreateContext(_connection.Connection, enrichContext);
        return HandlerInvoker.InvokeAsync<TRequest, TResponse>(_services, request, context, ct);
    }

    /// <summary>
    /// Fully inferred unary invoke for requests implementing the self-typed marker
    /// <see cref="IRequest{TSelf, TResponse}"/>: both generic arguments are inferred from
    /// <paramref name="request"/> — <c>await invoker.InvokeAsync(new ResolveWorldSession.Query(account), ct)</c>.
    /// </summary>
    /// <typeparam name="TRequest">The handler request type (inferred).</typeparam>
    /// <typeparam name="TResponse">The handler success value type (inferred from the marker).</typeparam>
    /// <param name="request">The already-decoded application request.</param>
    /// <param name="ct">A cancellation token flowed unchanged into the handler pipeline.</param>
    /// <returns>The handler's success or failure as a <see cref="Result{T}"/> value.</returns>
    public ValueTask<Result<TResponse>> InvokeAsync<TRequest, TResponse>(
        IRequest<TRequest, TResponse> request,
        CancellationToken ct = default)
        where TRequest : notnull, IRequest<TRequest, TResponse> {
        ArgumentNullException.ThrowIfNull(request);

        return InvokeAsync<TRequest, TResponse>((TRequest)request, ct);
    }

    /// <summary>
    /// Fully inferred unary invoke with adapter-owned metadata; see
    /// <see cref="InvokeAsync{TRequest, TResponse}(IRequest{TRequest, TResponse}, CancellationToken)"/>.
    /// </summary>
    /// <typeparam name="TRequest">The handler request type (inferred).</typeparam>
    /// <typeparam name="TResponse">The handler success value type (inferred from the marker).</typeparam>
    /// <param name="request">The already-decoded application request.</param>
    /// <param name="enrichContext">
    /// Adds adapter-owned typed metadata before protected framework identity entries are applied. Exceptions
    /// propagate and no handler is invoked.
    /// </param>
    /// <param name="ct">A cancellation token flowed unchanged into the handler pipeline.</param>
    /// <returns>The handler's success or failure as a <see cref="Result{T}"/> value.</returns>
    public ValueTask<Result<TResponse>> InvokeAsync<TRequest, TResponse>(
        IRequest<TRequest, TResponse> request,
        Action<DispatchScopeContext> enrichContext,
        CancellationToken ct = default)
        where TRequest : notnull, IRequest<TRequest, TResponse> {
        ArgumentNullException.ThrowIfNull(request);

        return InvokeAsync<TRequest, TResponse>((TRequest)request, enrichContext, ct);
    }

    /// <summary>
    /// Starts a typed request-driven stream through its decorated DI pipeline in a fresh scope seeded from the
    /// captured connection snapshot.
    /// </summary>
    /// <typeparam name="TRequest">The stream handler request type.</typeparam>
    /// <typeparam name="TItem">The stream item type.</typeparam>
    /// <param name="request">The already-decoded application request.</param>
    /// <param name="ct">A cancellation token flowed unchanged into stream startup.</param>
    /// <returns>
    /// A failed startup result, or an accepted <see cref="StreamHandlerInvocation{TItem}"/> that owns the dispatch
    /// scope through lazy enumeration. Terminal enumeration disposes the scope; callers that do not enumerate an
    /// accepted stream must dispose the invocation explicitly. A startup failure disposes the scope immediately.
    /// </returns>
    public ValueTask<Result<StreamHandlerInvocation<TItem>>> InvokeStreamAsync<TRequest, TItem>(
        TRequest request,
        CancellationToken ct = default)
        where TRequest : notnull {
        ArgumentNullException.ThrowIfNull(request);

        var context = CreateContext(_connection.Connection, enrichContext: null);
        return StreamHandlerInvoker.InvokeAsync<TRequest, TItem>(_services, request, context, ct);
    }

    /// <summary>
    /// Starts a typed request-driven stream with adapter-owned metadata added to the dispatch scope before the
    /// protected framework identity entries are applied.
    /// </summary>
    /// <typeparam name="TRequest">The stream handler request type.</typeparam>
    /// <typeparam name="TItem">The stream item type.</typeparam>
    /// <param name="request">The already-decoded application request.</param>
    /// <param name="enrichContext">
    /// Adds adapter-owned typed metadata before protected framework identity entries are applied. Exceptions
    /// propagate and no stream handler is invoked.
    /// </param>
    /// <param name="ct">A cancellation token flowed unchanged into stream startup.</param>
    /// <returns>
    /// A failed startup result, or an accepted <see cref="StreamHandlerInvocation{TItem}"/>; see
    /// <see cref="InvokeStreamAsync{TRequest, TItem}(TRequest, CancellationToken)"/> for scope ownership.
    /// </returns>
    public ValueTask<Result<StreamHandlerInvocation<TItem>>> InvokeStreamAsync<TRequest, TItem>(
        TRequest request,
        Action<DispatchScopeContext> enrichContext,
        CancellationToken ct = default)
        where TRequest : notnull {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(enrichContext);

        var context = CreateContext(_connection.Connection, enrichContext);
        return StreamHandlerInvoker.InvokeAsync<TRequest, TItem>(_services, request, context, ct);
    }

    /// <summary>
    /// Fully inferred stream start for requests implementing the self-typed marker
    /// <see cref="IStreamRequest{TSelf, TItem}"/>: both generic arguments are inferred from
    /// <paramref name="request"/>.
    /// </summary>
    /// <typeparam name="TRequest">The stream handler request type (inferred).</typeparam>
    /// <typeparam name="TItem">The stream item type (inferred from the marker).</typeparam>
    /// <param name="request">The already-decoded application request.</param>
    /// <param name="ct">A cancellation token flowed unchanged into stream startup.</param>
    /// <returns>
    /// A failed startup result, or an accepted <see cref="StreamHandlerInvocation{TItem}"/>; see
    /// <see cref="InvokeStreamAsync{TRequest, TItem}(TRequest, CancellationToken)"/> for scope ownership.
    /// </returns>
    public ValueTask<Result<StreamHandlerInvocation<TItem>>> InvokeStreamAsync<TRequest, TItem>(
        IStreamRequest<TRequest, TItem> request,
        CancellationToken ct = default)
        where TRequest : notnull, IStreamRequest<TRequest, TItem> {
        ArgumentNullException.ThrowIfNull(request);

        return InvokeStreamAsync<TRequest, TItem>((TRequest)request, ct);
    }

    /// <summary>
    /// Fully inferred stream start with adapter-owned metadata; see
    /// <see cref="InvokeStreamAsync{TRequest, TItem}(IStreamRequest{TRequest, TItem}, CancellationToken)"/>.
    /// </summary>
    /// <typeparam name="TRequest">The stream handler request type (inferred).</typeparam>
    /// <typeparam name="TItem">The stream item type (inferred from the marker).</typeparam>
    /// <param name="request">The already-decoded application request.</param>
    /// <param name="enrichContext">
    /// Adds adapter-owned typed metadata before protected framework identity entries are applied. Exceptions
    /// propagate and no stream handler is invoked.
    /// </param>
    /// <param name="ct">A cancellation token flowed unchanged into stream startup.</param>
    /// <returns>
    /// A failed startup result, or an accepted <see cref="StreamHandlerInvocation{TItem}"/>; see
    /// <see cref="InvokeStreamAsync{TRequest, TItem}(TRequest, CancellationToken)"/> for scope ownership.
    /// </returns>
    public ValueTask<Result<StreamHandlerInvocation<TItem>>> InvokeStreamAsync<TRequest, TItem>(
        IStreamRequest<TRequest, TItem> request,
        Action<DispatchScopeContext> enrichContext,
        CancellationToken ct = default)
        where TRequest : notnull, IStreamRequest<TRequest, TItem> {
        ArgumentNullException.ThrowIfNull(request);

        return InvokeStreamAsync<TRequest, TItem>((TRequest)request, enrichContext, ct);
    }

    /// <summary>
    /// Invokes an already-decoded named request only when its frozen route is exposed to bidirectional
    /// connections.
    /// </summary>
    /// <param name="dispatcher">The frozen handler routing table.</param>
    /// <param name="name">The case-insensitive operation name.</param>
    /// <param name="request">The request object already decoded by the connection adapter.</param>
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
    public ValueTask<Result<object>> InvokeNamedAsync(
        HandlerDispatcher dispatcher,
        string name,
        object request,
        CancellationToken ct = default) =>
        InvokeNamedCoreAsync(dispatcher, name, request, enrichContext: null, ct);

    /// <summary>
    /// Invokes an already-decoded named request with adapter-owned metadata added to the dispatch scope; see
    /// <see cref="InvokeNamedAsync(HandlerDispatcher, string, object, CancellationToken)"/>.
    /// </summary>
    /// <param name="dispatcher">The frozen handler routing table.</param>
    /// <param name="name">The case-insensitive operation name.</param>
    /// <param name="request">The request object already decoded by the connection adapter.</param>
    /// <param name="enrichContext">
    /// Adds adapter-owned typed metadata before protected framework identity entries are applied. Exceptions
    /// propagate and no route is invoked.
    /// </param>
    /// <param name="ct">A cancellation token flowed unchanged into the route.</param>
    /// <returns>
    /// The route's boxed success or failure result; see
    /// <see cref="InvokeNamedAsync(HandlerDispatcher, string, object, CancellationToken)"/>.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="request"/> is not assignable to the selected route's declared request type, indicating an
    /// adapter decoding bug.
    /// </exception>
    public ValueTask<Result<object>> InvokeNamedAsync(
        HandlerDispatcher dispatcher,
        string name,
        object request,
        Action<DispatchScopeContext> enrichContext,
        CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(enrichContext);
        return InvokeNamedCoreAsync(dispatcher, name, request, enrichContext, ct);
    }

    private async ValueTask<Result<object>> InvokeNamedCoreAsync(
        HandlerDispatcher dispatcher,
        string name,
        object request,
        Action<DispatchScopeContext>? enrichContext,
        CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(request);

        var snapshot = _connection.Connection;
        if (!dispatcher.TryGetRoute(name, HandlerTransports.Connection, out var route))
            return AppError.NotFound(NotFoundMessage);

        if (!route.RequestType.IsInstanceOfType(request)) {
            throw new ArgumentException(
                $"The decoded request for connection operation '{name}' must be assignable to " +
                $"'{route.RequestType.FullName}', but the adapter supplied '{request.GetType().FullName}'.",
                nameof(request));
        }

        var context = CreateContext(snapshot, enrichContext);
        await using var scope = _services.CreateDispatchScope(context);
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

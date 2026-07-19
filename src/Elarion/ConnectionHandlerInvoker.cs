using System.Runtime.CompilerServices;
using System.Security.Claims;
using Elarion.Abstractions;
using Elarion.Abstractions.Connections;
using Elarion.Abstractions.Dispatch;
using Elarion.Abstractions.Pipeline;
using Elarion.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
/// <para>
/// <b>Scope modes.</b> By default every dispatch runs in a fresh seeded DI scope
/// (<see cref="ConnectionDispatchScopeMode.PerMessage"/>). A high-rate connection can opt into
/// <see cref="ConnectionDispatchScopeMode.PerConnection"/>: one scope reused for every unary message, the
/// composed handler chain cached per request type, and one reusable context refilled per message — the
/// steady-state dispatch then allocates no scope, no chain, and no context. Initializers still run per
/// message, so identity promotion is observed. That mode assumes the adapter's sequential receive loop (its
/// caches are deliberately unsynchronized) and requires the owner to <see cref="DisposeAsync"/> the invoker
/// when the connection closes; stream invocations keep their own scope in both modes because the stream owns
/// its scope through lazy enumeration.
/// </para>
/// </remarks>
public sealed class ConnectionHandlerInvoker : IAsyncDisposable {
    private const string NotFoundMessage = "The requested resource was not found.";

    private readonly IServiceProvider _services;
    private readonly IClientConnectionSink _connection;
    private readonly ConnectionDispatchScopeMode _scopeMode;

    // Per-connection mode state: created lazily on first dispatch, reused for every message, disposed with the
    // invoker. Deliberately unsynchronized — connection dispatch is sequential (the adapter's single receive
    // loop is the ordering guarantee), and a lock per message would tax the hot path for no correctness.
    private readonly Dictionary<(Type Request, Type Response), object>? _handlerCache;
    private readonly DispatchScopeContext? _reusableContext;
    private readonly ILogger? _scopeModeLogger;
    private AsyncServiceScope? _connectionScope;
    private IDispatchScopeInitializer[]? _scopeInitializers;
    private bool _disposed;

    /// <summary>Binds the invoker to one connection for its lifetime.</summary>
    /// <param name="services">The root provider from which dispatch scopes are created.</param>
    /// <param name="connection">The connection sink whose immutable identity snapshot is captured per dispatch.</param>
    /// <param name="options">Per-connection dispatch options; <see langword="null"/> is the per-message default.</param>
    public ConnectionHandlerInvoker(
        IServiceProvider services,
        IClientConnectionSink connection,
        ConnectionHandlerInvokerOptions? options = null) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(connection);
        _services = services;
        _connection = connection;
        _scopeMode = options?.ScopeMode ?? ConnectionDispatchScopeMode.PerMessage;
        if (_scopeMode == ConnectionDispatchScopeMode.PerConnection) {
            _handlerCache = [];
            _reusableContext = new DispatchScopeContext();
            _scopeModeLogger = services.GetService<ILoggerFactory>()?.CreateLogger<ConnectionHandlerInvoker>();
        }
    }

    /// <summary>
    /// Disposes the reused connection scope in <see cref="ConnectionDispatchScopeMode.PerConnection"/> mode
    /// (scoped services live until this call); a no-op in per-message mode. Call it when the connection closes,
    /// after the receive loop has stopped dispatching.
    /// </summary>
    public async ValueTask DisposeAsync() {
        if (_disposed) return;

        _disposed = true;
        if (_connectionScope is { } scope) {
            _connectionScope = null;
            await scope.DisposeAsync().ConfigureAwait(false);
        }
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

        return InvokeUnaryCoreAsync<TRequest, TResponse>(request, null, ct);
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

        return InvokeUnaryCoreAsync<TRequest, TResponse>(request, enrichContext, ct);
    }

    /// <summary>
    /// Fully inferred unary invoke for requests implementing the self-typed marker
    /// <see cref="IRequest{TSelf, TResponse}"/>: both generic arguments are inferred from
    /// <paramref name="request"/> — <c>await invoker.InvokeAsync(new ResolveWorldSession.Query(account), ct)</c>.
    /// </summary>
    /// <remarks>
    /// The interface-typed parameter boxes a <c>readonly record struct</c> request per call (C# constraints
    /// do not participate in inference, ADR-0065). A hot value-type request should use the explicit-generic
    /// <see cref="InvokeAsync{TRequest, TResponse}(TRequest, CancellationToken)"/> overload, which
    /// dispatches it unboxed (ADR-0066).
    /// </remarks>
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

        var context = CreateContext(_connection.Connection, null);
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
        CancellationToken ct = default) {
        return InvokeNamedCoreAsync(dispatcher, name, request, null, ct);
    }

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

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
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

        if (!route.RequestType.IsInstanceOfType(request))
            throw new ArgumentException(
                $"The decoded request for connection operation '{name}' must be assignable to " +
                $"'{route.RequestType.FullName}', but the adapter supplied '{request.GetType().FullName}'.",
                nameof(request));

        if (_scopeMode == ConnectionDispatchScopeMode.PerConnection) {
            var provider = SeedConnectionScope(snapshot, enrichContext);
            return await route.InvokeAsync(request, provider, ct).ConfigureAwait(false);
        }

        var context = CreateContext(snapshot, enrichContext);
        await using var scope = _services.CreateDispatchScope(context);
        return await route.InvokeAsync(request, scope.ServiceProvider, ct).ConfigureAwait(false);
    }

    private ValueTask<Result<TResponse>> InvokeUnaryCoreAsync<TRequest, TResponse>(
        TRequest request,
        Action<DispatchScopeContext>? enrichContext,
        CancellationToken ct)
        where TRequest : notnull {
        if (_scopeMode == ConnectionDispatchScopeMode.PerConnection)
            return InvokeInConnectionScopeAsync<TRequest, TResponse>(request, enrichContext, ct);

        var context = CreateContext(_connection.Connection, enrichContext);
        return HandlerInvoker.InvokeAsync<TRequest, TResponse>(_services, request, context, ct);
    }

    /// <summary>
    /// The per-connection-scope unary hot path: no scope creation, no chain resolution, no context allocation
    /// after the first dispatch of a request type — the reused scope is re-seeded per message so identity
    /// promotion (ADR-0053) is still observed by the very next message.
    /// </summary>
    /// <remarks>Pooled state machine: this is the per-packet path of a high-rate connection, and the pooled
    /// builder keeps its suspension allocation-free (the same mechanism the TCP outbound writer uses).</remarks>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<Result<TResponse>> InvokeInConnectionScopeAsync<TRequest, TResponse>(
        TRequest request,
        Action<DispatchScopeContext>? enrichContext,
        CancellationToken ct)
        where TRequest : notnull {
        var provider = SeedConnectionScope(_connection.Connection, enrichContext);

        IHandler<TRequest, Result<TResponse>> handler;
        if (_handlerCache!.TryGetValue((typeof(TRequest), typeof(TResponse)), out var cached)) {
            handler = (IHandler<TRequest, Result<TResponse>>)cached;
        }
        else {
            handler = provider.GetRequiredService<IHandler<TRequest, Result<TResponse>>>();
            // Resolve first, then inspect: the generated pipeline metadata publishes its composed decorator
            // list on first resolution, so the warning sees the decorators actually attached in this process.
            WarnIfPerMessageScopedPipeline(typeof(TRequest));
            _handlerCache[(typeof(TRequest), typeof(TResponse))] = handler;
        }

        return await handler.HandleAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reuses (or lazily creates) the connection scope and re-seeds it for one message: clear and refill the
    /// one reusable context, then run the cached initializer list. Mirrors
    /// <see cref="ServiceProviderDispatchScopeExtensions.SeedScope"/> minus the per-call enumeration.
    /// </summary>
    private IServiceProvider SeedConnectionScope(
        ClientConnection snapshot,
        Action<DispatchScopeContext>? enrichContext) {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var scope = _connectionScope ??= _services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        _scopeInitializers ??= [.. provider.GetServices<IDispatchScopeInitializer>()];

        var context = _reusableContext!;
        context.Clear();
        enrichContext?.Invoke(context);
        context.Set<ClaimsPrincipal>(snapshot.Principal);
        context.Set<ClientConnection>(snapshot);
        foreach (var initializer in _scopeInitializers)
            initializer.Initialize(provider, context);

        return provider;
    }

    /// <summary>
    /// Warns once per handler type when a pipeline that assumes per-message scoping (the unit-of-work
    /// transaction or idempotency decorator) is dispatched under a per-connection scope: its scoped state —
    /// an EF <c>DbContext</c>'s change tracker, the idempotency unit of work — spans every message on this
    /// connection. Reads the generated keyed pipeline metadata; hand-wired registrations without it skip the
    /// check.
    /// </summary>
    private void WarnIfPerMessageScopedPipeline(Type requestType) {
        if (_scopeModeLogger is null || _services is not IKeyedServiceProvider) return;

        var metadata = _services.GetKeyedService<HandlerMetadata>(requestType);
        if (metadata is null) return;

        foreach (var step in metadata.Pipeline.Steps)
            if (step.Decorator == typeof(TransactionDecorator<,>) || step.Decorator == typeof(IdempotencyDecorator<,>))
                _scopeModeLogger.LogWarning(
                    "Handler {HandlerType} attaches {Decorator}, which assumes a per-message dispatch scope, " +
                    "but this connection dispatches under a per-connection scope; its unit-of-work state spans " +
                    "every message on this connection.",
                    metadata.HandlerType,
                    step.Decorator);
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

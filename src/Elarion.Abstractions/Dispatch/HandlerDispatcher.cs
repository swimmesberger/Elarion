using System.Collections.Frozen;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.Abstractions.Dispatch;

/// <summary>
/// The transport-neutral named request/reply dispatcher — a routing table of <see cref="HandlerRoute"/>s
/// (name → handler) that any transport adapts. It owns routing and handler invocation (through the full
/// decorator pipeline, resolved from the call scope); it owns <b>no</b> serialization or wire format. JSON-RPC,
/// MCP, gRPC, a CLI, etc. are thin adapters that deserialize a payload into a route's
/// <see cref="HandlerRoute.RequestType"/>, dispatch, and map the <see cref="Result{T}"/> onto their wire.
/// </summary>
/// <remarks>
/// Built once (typically by the generated bootstrapper, one <see cref="Map{TRequest,TResponse}"/> per
/// <c>[Handler]</c>), then <see cref="Freeze"/>d and shared across transports.
/// </remarks>
public sealed class HandlerDispatcher {
    private readonly Dictionary<string, HandlerRoute> _building = new(StringComparer.OrdinalIgnoreCase);
    private FrozenDictionary<string, HandlerRoute>? _frozen;

    private FrozenDictionary<string, HandlerRoute> Routes =>
        _frozen ?? throw new InvalidOperationException("Call Freeze() after registering all handlers.");

    /// <summary>Registers a handler under <paramref name="name"/>. Call before <see cref="Freeze"/>.</summary>
    /// <typeparam name="TRequest">The handler request type.</typeparam>
    /// <typeparam name="TResponse">The handler success value type.</typeparam>
    public HandlerDispatcher Map<TRequest, TResponse>(
        string name, HandlerTransports transports = HandlerTransports.All)
        where TRequest : class {
        if (_frozen is not null) {
            throw new InvalidOperationException("Cannot register handlers after Freeze() has been called.");
        }

        _building[name] = new HandlerRoute(
            name,
            typeof(TRequest),
            typeof(TResponse),
            transports,
            async (request, serviceProvider, ct) => {
                var handler = serviceProvider.GetRequiredService<IHandler<TRequest, Result<TResponse>>>();
                var result = await handler.HandleAsync((TRequest)request, ct).ConfigureAwait(false);
                return result.IsSuccess
                    ? Result<object>.Success(result.Value!)
                    : Result<object>.Failure(result.Error);
            });

        return this;
    }

    /// <summary>
    /// Registers a delegate-backed handler under <paramref name="name"/> — for manual wiring, a custom transport,
    /// or a test, without a DI-registered <see cref="IHandler{TRequest,TResponse}"/>. The delegate receives the
    /// call scope's <see cref="IServiceProvider"/> so it can still resolve dependencies if it wants.
    /// </summary>
    public HandlerDispatcher MapDelegate<TRequest, TResponse>(
        string name,
        Func<TRequest, IServiceProvider, CancellationToken, ValueTask<Result<TResponse>>> handler,
        HandlerTransports transports = HandlerTransports.All)
        where TRequest : class {
        if (_frozen is not null) {
            throw new InvalidOperationException("Cannot register handlers after Freeze() has been called.");
        }

        _building[name] = new HandlerRoute(
            name,
            typeof(TRequest),
            typeof(TResponse),
            transports,
            async (request, serviceProvider, ct) => {
                var result = await handler((TRequest)request, serviceProvider, ct).ConfigureAwait(false);
                return result.IsSuccess
                    ? Result<object>.Success(result.Value!)
                    : Result<object>.Failure(result.Error);
            });

        return this;
    }

    /// <summary>
    /// Freezes the registry into a <see cref="FrozenDictionary{TKey,TValue}"/> for fast lookups. Must be called once
    /// after all <c>Map</c>/<c>MapDelegate</c> registrations and before any routing; reads throw until it is.
    /// </summary>
    public HandlerDispatcher Freeze() {
        _frozen ??= _building.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        return this;
    }

    /// <summary>Looks up the route registered under <paramref name="name"/> (case-insensitive).</summary>
    public bool TryGetRoute(string name, out HandlerRoute route) =>
        Routes.TryGetValue(name, out route!);

    /// <summary>
    /// Looks up the route registered under <paramref name="name"/> only when it is exposed on
    /// <paramref name="transport"/> — so a JSON-RPC call to an MCP-only operation reports "not found".
    /// </summary>
    public bool TryGetRoute(string name, HandlerTransports transport, out HandlerRoute route) {
        if (Routes.TryGetValue(name, out route!) && (route.Transports & transport) != 0) {
            return true;
        }

        route = null!;
        return false;
    }

    /// <summary>All registered routes — e.g. for schema export or a tool catalogue.</summary>
    public IReadOnlyCollection<HandlerRoute> AllRoutes => Routes.Values;

    /// <summary>The routes exposed on <paramref name="transport"/> (the subset a given transport adapter serves).</summary>
    public IEnumerable<HandlerRoute> RoutesFor(HandlerTransports transport) =>
        Routes.Values.Where(route => (route.Transports & transport) != 0);

    /// <summary>
    /// Dispatches <paramref name="request"/> to the handler registered under <paramref name="name"/>, in the
    /// given <paramref name="scope"/>. Returns <see cref="AppError.NotFound"/> when no route matches — a
    /// name-routed transport that needs a distinct "method not found" wire code should check
    /// <see cref="TryGetRoute"/> first.
    /// </summary>
    public ValueTask<Result<object>> DispatchAsync(
        string name, object request, IServiceProvider scope, CancellationToken ct) {
        if (!Routes.TryGetValue(name, out var route)) {
            return ValueTask.FromResult(
                Result<object>.Failure(AppError.NotFound($"No handler is registered for '{name}'.")));
        }

        return route.InvokeAsync(request, scope, ct);
    }
}

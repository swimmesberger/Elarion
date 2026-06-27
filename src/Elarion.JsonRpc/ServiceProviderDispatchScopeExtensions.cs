using Microsoft.Extensions.DependencyInjection;

namespace Elarion.JsonRpc;

/// <summary>
/// Creates the per-call DI scope used by dispatcher-based transports, running every registered
/// <see cref="IDispatchScopeInitializer"/> so request-boundary state is seeded into the fresh scope.
/// </summary>
public static class ServiceProviderDispatchScopeExtensions {
    /// <summary>
    /// Creates an <see cref="AsyncServiceScope"/> from <paramref name="parent"/> and runs every registered
    /// <see cref="IDispatchScopeInitializer"/> against the new scope, passing <paramref name="inheritFrom"/>
    /// and <paramref name="context"/> (or <see cref="DispatchScopeContext.Empty"/> when <see langword="null"/>).
    /// Use this in place of a raw <see cref="ServiceProviderServiceExtensions.CreateAsyncScope"/> at every
    /// dispatch site so scoped state (current user, tenant, …) is seeded consistently.
    /// </summary>
    /// <param name="parent">The provider to create the call scope from (the request scope, or app root for MCP).</param>
    /// <param name="context">The values captured at the dispatch boundary, or <see langword="null"/> for none.</param>
    /// <param name="inheritFrom">
    /// The originating request scope that initializers may copy already-built instances from, or
    /// <see langword="null"/> when there is none (MCP dispatches from the application root, which has no
    /// request-scoped instances to inherit). For JSON-RPC this is the request scope — the same provider the
    /// call scope is created from.
    /// </param>
    /// <returns>The created scope, ready for dispatch.</returns>
    public static AsyncServiceScope CreateDispatchScope(
        this IServiceProvider parent,
        DispatchScopeContext? context = null,
        IServiceProvider? inheritFrom = null) {
        ArgumentNullException.ThrowIfNull(parent);

        var scope = parent.CreateAsyncScope();
        try {
            var effectiveContext = context ?? DispatchScopeContext.Empty;
            foreach (var initializer in scope.ServiceProvider.GetServices<IDispatchScopeInitializer>()) {
                initializer.Initialize(scope.ServiceProvider, inheritFrom, effectiveContext);
            }
        } catch {
            // An initializer threw before the caller could take ownership of the scope; dispose it so the
            // scope (and any scoped services already resolved) does not leak.
            scope.DisposeAsync().GetAwaiter().GetResult();
            throw;
        }

        return scope;
    }

    /// <summary>
    /// The single copy primitive behind scope inheritance: if <paramref name="inheritFrom"/> holds a
    /// <typeparamref name="T"/> that <paramref name="accept"/> approves, copies it into the
    /// <typeparamref name="T"/> resolved from <paramref name="callScope"/> via
    /// <see cref="IScopeCopyable{T}.CopyFrom"/> and returns <see langword="true"/>. Returns
    /// <see langword="false"/> when there is nothing to inherit (no originating scope, no instance there, or
    /// the candidate is rejected) — the caller then seeds the service some other way.
    /// </summary>
    /// <remarks>
    /// Shared by <see cref="CopyingDispatchScopeInitializer{T}"/> and by bespoke hybrid initializers (e.g. the
    /// current-user one, which inherits the request-scope snapshot via this method and otherwise builds from a
    /// captured principal), so the copy path has exactly one implementation.
    /// </remarks>
    /// <param name="callScope">The per-call scope whose <typeparamref name="T"/> is the copy target.</param>
    /// <param name="inheritFrom">The originating request scope to copy from, or <see langword="null"/>.</param>
    /// <param name="accept">Optional predicate to gate the source (e.g. "is initialized"); defaults to accept.</param>
    /// <typeparam name="T">The inheritable scoped service.</typeparam>
    public static bool TryInherit<T>(
        IServiceProvider callScope,
        IServiceProvider? inheritFrom,
        Func<T, bool>? accept = null)
        where T : class, IScopeCopyable<T> {
        ArgumentNullException.ThrowIfNull(callScope);

        if (inheritFrom?.GetService<T>() is { } source && (accept?.Invoke(source) ?? true)) {
            callScope.GetRequiredService<T>().CopyFrom(source);
            return true;
        }

        return false;
    }
}

using Microsoft.Extensions.DependencyInjection;

namespace Elarion.Abstractions.Dispatch;

/// <summary>
/// Creates the per-call DI scope used by dispatcher-based transports, running every registered
/// <see cref="IDispatchScopeInitializer"/> so request-boundary state is seeded into the fresh scope.
/// </summary>
public static class ServiceProviderDispatchScopeExtensions {
    /// <summary>
    /// Creates an <see cref="AsyncServiceScope"/> from <paramref name="parent"/> and runs every registered
    /// <see cref="IDispatchScopeInitializer"/> against the new scope, passing <paramref name="context"/>
    /// (or <see cref="DispatchScopeContext.Empty"/> when <see langword="null"/>). Use this in place of a raw
    /// <see cref="ServiceProviderServiceExtensions.CreateAsyncScope"/> at every dispatch site so scoped state
    /// (current user, tenant, …) is seeded consistently.
    /// </summary>
    /// <param name="parent">The provider to create the call scope from (the request scope, or app root for MCP).</param>
    /// <param name="context">The values captured at the dispatch boundary, or <see langword="null"/> for none.</param>
    /// <returns>The created scope, ready for dispatch.</returns>
    public static AsyncServiceScope CreateDispatchScope(
        this IServiceProvider parent,
        DispatchScopeContext? context = null) {
        ArgumentNullException.ThrowIfNull(parent);

        var scope = parent.CreateAsyncScope();
        try {
            scope.ServiceProvider.SeedScope(context ?? DispatchScopeContext.Empty);
        } catch {
            // An initializer threw before the caller could take ownership of the scope; dispose it so the
            // scope (and any scoped services already resolved) does not leak.
            scope.DisposeAsync().GetAwaiter().GetResult();
            throw;
        }

        return scope;
    }

    /// <summary>
    /// Runs every registered <see cref="IDispatchScopeInitializer"/> against an <b>existing</b> scope, seeding
    /// it from <paramref name="context"/>. Used by <see cref="CreateDispatchScope"/> for per-call scopes, and
    /// by hosts that seed an existing scope directly — e.g. an HTTP middleware seeding the request scope, whose
    /// handlers run in that scope rather than a child one.
    /// </summary>
    /// <param name="scope">The scope to seed (its own service provider).</param>
    /// <param name="context">The values captured at the boundary.</param>
    public static void SeedScope(this IServiceProvider scope, DispatchScopeContext context) {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(context);

        foreach (var initializer in scope.GetServices<IDispatchScopeInitializer>()) {
            initializer.Initialize(scope, context);
        }
    }
}

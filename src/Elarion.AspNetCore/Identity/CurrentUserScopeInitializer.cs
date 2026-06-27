using System.Security.Claims;
using Elarion.JsonRpc;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.AspNetCore.Identity;

/// <summary>
/// Seeds the per-call <see cref="CurrentUserSnapshot"/> so <c>ICurrentUser</c> (and the authorization decorator
/// that depends on it) resolves correctly inside the fresh DI scope the JSON-RPC and MCP dispatchers create per
/// call — where the request-scope snapshot initialized by <see cref="CurrentUserMiddleware"/> is not visible.
/// </summary>
/// <remarks>
/// <para>
/// Hybrid by necessity: when an originating request scope exists (JSON-RPC, HTTP batch) it <em>copies</em> the
/// already-built request-scope snapshot — reusing the materialized claims, no re-parsing. MCP's per-call scope
/// is rooted at the session / application-root provider, not the HTTP request scope, so the middleware snapshot
/// is unreachable; there it builds the snapshot from the per-message principal captured in the
/// <see cref="DispatchScopeContext"/> (<c>RequestContext.User</c>). The principal is also the fallback if the
/// request-scope snapshot was never initialized, e.g. a host that omitted the middleware.
/// </para>
/// <para>Registered by <c>AddElarionCurrentUser</c>. Stateless, so it is a singleton operating on the passed scopes.</para>
/// </remarks>
internal sealed class CurrentUserScopeInitializer : IDispatchScopeInitializer {
    private static readonly ClaimsPrincipal Anonymous = new(new ClaimsIdentity());

    /// <inheritdoc />
    public void Initialize(IServiceProvider callScope, IServiceProvider? inheritFrom, DispatchScopeContext context) {
        // Inherit the request-scope snapshot when there is an initialized one (JSON-RPC / HTTP batch) — the
        // shared copy path, reusing the materialized claims with no re-parsing.
        if (ServiceProviderDispatchScopeExtensions.TryInherit<CurrentUserSnapshot>(
                callScope, inheritFrom, static snapshot => snapshot.IsInitialized)) {
            return;
        }

        // The only current-user-specific part: no request scope to inherit from (MCP), or it was never
        // initialized — build from the per-message principal captured in the context.
        var principal = context.TryGet<ClaimsPrincipal>(out var captured) && captured is not null
            ? captured
            : Anonymous;
        callScope.GetRequiredService<CurrentUserSnapshot>().Initialize(principal);
    }
}

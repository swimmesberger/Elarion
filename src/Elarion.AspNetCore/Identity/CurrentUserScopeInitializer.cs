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
/// already-built request-scope snapshot — reusing the materialized claims, no re-parsing. MCP dispatches from
/// the application root with no request scope, so there it builds the snapshot from the principal captured in
/// the <see cref="DispatchScopeContext"/>. (The principal is also the fallback if the request-scope snapshot
/// was never initialized, e.g. a host that omitted the middleware.)
/// </para>
/// <para>Registered by <c>AddElarionCurrentUser</c>. Stateless, so it is a singleton operating on the passed scopes.</para>
/// </remarks>
internal sealed class CurrentUserScopeInitializer : IDispatchScopeInitializer {
    private static readonly ClaimsPrincipal Anonymous = new(new ClaimsIdentity());

    /// <inheritdoc />
    public void Initialize(IServiceProvider callScope, IServiceProvider? inheritFrom, DispatchScopeContext context) {
        // GetService (not GetRequired): a host may have replaced ICurrentUser without the snapshot, in which
        // case there is nothing to seed and dispatch must not fail.
        var target = callScope.GetService<CurrentUserSnapshot>();
        if (target is null) {
            return;
        }

        // Prefer copying the request-scope snapshot (JSON-RPC / HTTP batch) — reuses the materialized claims.
        if (inheritFrom?.GetService<CurrentUserSnapshot>() is { IsInitialized: true } source) {
            target.CopyFrom(source);
            return;
        }

        // No request scope to inherit from (MCP), or it was never initialized — build from the captured principal.
        var principal = context.TryGet<ClaimsPrincipal>(out var captured) && captured is not null
            ? captured
            : Anonymous;
        target.Initialize(principal);
    }
}

using System.Security.Claims;
using Elarion.JsonRpc;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.AspNetCore.Identity;

/// <summary>
/// Seeds the per-call <see cref="CurrentUserSnapshot"/> from the <see cref="ClaimsPrincipal"/> captured in
/// the <see cref="DispatchScopeContext"/>, so <c>ICurrentUser</c> (and the authorization decorator that
/// depends on it) resolves correctly inside the fresh DI scope the JSON-RPC and MCP dispatchers create per
/// call — where the request-scope snapshot initialized by <see cref="CurrentUserMiddleware"/> is not visible.
/// </summary>
/// <remarks>
/// Registered by <c>AddElarionCurrentUser</c> as one of possibly many <see cref="IDispatchScopeInitializer"/>
/// instances. Stateless, so it is a singleton that operates on the call scope passed to
/// <see cref="Initialize"/> (no captive dependency on a scoped service).
/// </remarks>
internal sealed class CurrentUserScopeInitializer : IDispatchScopeInitializer {
    private static readonly ClaimsPrincipal Anonymous = new(new ClaimsIdentity());

    /// <inheritdoc />
    public void Initialize(IServiceProvider callScope, DispatchScopeContext context) {
        var principal = context.TryGet<ClaimsPrincipal>(out var captured) && captured is not null
            ? captured
            : Anonymous;

        // GetService (not GetRequired): a host may have replaced ICurrentUser without the snapshot, in which
        // case there is nothing to seed and dispatch must not fail.
        callScope.GetService<CurrentUserSnapshot>()?.Initialize(principal);
    }
}

using System.Security.Claims;
using Elarion.JsonRpc;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.AspNetCore.Identity;

/// <summary>
/// Seeds the per-call <see cref="CurrentUserSnapshot"/> from the <see cref="ClaimsPrincipal"/> captured in the
/// <see cref="DispatchScopeContext"/>, so <c>ICurrentUser</c> (and the authorization decorator that depends on
/// it) resolves inside the fresh DI scope the JSON-RPC and MCP dispatchers create per call.
/// </summary>
/// <remarks>
/// One uniform path for every dispatcher-based transport: JSON-RPC captures <c>HttpContext.User</c> and MCP
/// captures <c>RequestContext.User</c> into the same context, and this initializer seeds the snapshot the same
/// way for both. (HTTP <c>[HttpEndpoint]</c> handlers run in the request scope and read the snapshot
/// <see cref="CurrentUserMiddleware"/> seeds — the same <see cref="CurrentUserSnapshot.Initialize"/> operation.)
/// Registered by <c>AddElarionCurrentUser</c>; stateless, hence a singleton.
/// </remarks>
internal sealed class CurrentUserScopeInitializer : IDispatchScopeInitializer {
    private static readonly ClaimsPrincipal Anonymous = new(new ClaimsIdentity());

    /// <inheritdoc />
    public void Initialize(IServiceProvider callScope, DispatchScopeContext context) {
        var principal = context.TryGet<ClaimsPrincipal>(out var captured) && captured is not null
            ? captured
            : Anonymous;

        // GetService (not GetRequired): a host may have replaced ICurrentUser without the snapshot.
        callScope.GetService<CurrentUserSnapshot>()?.Initialize(principal);
    }
}

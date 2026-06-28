using System.Security.Claims;
using Elarion.JsonRpc;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.Identity;

/// <summary>
/// Seeds the per-call <see cref="ClaimsPrincipalCurrentUser"/> from the <see cref="ClaimsPrincipal"/> captured
/// in the <see cref="DispatchScopeContext"/>, so <c>ICurrentUser</c> (and the authorization decorator that
/// depends on it) resolves inside the fresh DI scope a dispatcher-based transport creates per call.
/// </summary>
/// <remarks>
/// Transport-neutral: every transport captures the caller's principal into the context at its boundary
/// (JSON-RPC <c>HttpContext.User</c>, MCP <c>RequestContext.User</c>, gRPC <c>ServerCallContext</c>, a console
/// command's constructed principal, …) and this one initializer applies it. Registered by
/// <see cref="ClaimsCurrentUserServiceCollectionExtensions.AddElarionClaimsCurrentUser"/>; stateless singleton.
/// </remarks>
internal sealed class CurrentUserScopeInitializer : IDispatchScopeInitializer {
    private static readonly ClaimsPrincipal Anonymous = new(new ClaimsIdentity());

    /// <inheritdoc />
    public void Initialize(IServiceProvider callScope, DispatchScopeContext context) {
        var principal = context.TryGet<ClaimsPrincipal>(out var captured) && captured is not null
            ? captured
            : Anonymous;

        // GetService (not GetRequired): a host may have replaced ICurrentUser without the snapshot.
        callScope.GetService<ClaimsPrincipalCurrentUser>()?.Initialize(principal);
    }
}

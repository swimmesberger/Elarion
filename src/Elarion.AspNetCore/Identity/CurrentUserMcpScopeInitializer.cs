using System.Security.Claims;
using Elarion.JsonRpc;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.AspNetCore.Identity;

/// <summary>
/// Seeds the per-call <see cref="CurrentUserSnapshot"/> for transports that have <b>no originating request
/// scope to inherit from</b> — in practice MCP, whose per-call scope is rooted at the session / application
/// root rather than the HTTP request scope. It builds the snapshot from the per-message principal captured in
/// the <see cref="DispatchScopeContext"/> (<c>RequestContext.User</c>).
/// </summary>
/// <remarks>
/// JSON-RPC and HTTP-batch calls run with an <c>inheritFrom</c> request scope, so the current-user snapshot is
/// inherited there by the generic <see cref="CopyingDispatchScopeInitializer{T}"/> registered via
/// <c>AddDispatchScopeInherited&lt;CurrentUserSnapshot&gt;()</c> — and this initializer no-ops. The two are
/// complementary, keyed solely on whether an originating scope exists, so each transport seeds the snapshot
/// exactly once. Registered by <c>AddElarionCurrentUser</c>; stateless, hence a singleton.
/// </remarks>
internal sealed class CurrentUserMcpScopeInitializer : IDispatchScopeInitializer {
    private static readonly ClaimsPrincipal Anonymous = new(new ClaimsIdentity());

    /// <inheritdoc />
    public void Initialize(IServiceProvider callScope, IServiceProvider? inheritFrom, DispatchScopeContext context) {
        // A request scope exists (JSON-RPC / HTTP) — the snapshot is inherited from it; nothing to do here.
        if (inheritFrom is not null) {
            return;
        }

        var principal = context.TryGet<ClaimsPrincipal>(out var captured) && captured is not null
            ? captured
            : Anonymous;

        // GetService (not GetRequired): a host may have replaced ICurrentUser without the snapshot.
        callScope.GetService<CurrentUserSnapshot>()?.Initialize(principal);
    }
}

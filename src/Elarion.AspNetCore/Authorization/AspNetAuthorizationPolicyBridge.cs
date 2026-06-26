using Elarion.Abstractions.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace Elarion.AspNetCore.Authorization;

/// <summary>
/// An optional, opt-in adapter that exposes an ASP.NET Core authorization policy (registered via the
/// standard <c>AddAuthorization(o =&gt; o.AddPolicy(...))</c>) as an Elarion <see cref="IAuthorizationPolicy"/>,
/// so existing policies and <c>IAuthorizationHandler</c>s can be reused from Elarion handlers.
/// </summary>
/// <remarks>
/// <para>
/// This is the one place Elarion authorization touches ASP.NET's policy engine, and it is deliberately
/// opt-in. <b>Caveat:</b> Elarion passes the handler <em>request</em> as the policy resource
/// (<see cref="AuthorizationContext.Resource"/>), so an ASP.NET requirement handler that casts
/// <c>context.Resource</c> to <see cref="HttpContext"/> will not work under non-HTTP transports. The
/// principal is read from the current <see cref="HttpContext"/>; absent one (e.g. a non-HTTP transport),
/// the policy is denied.
/// </para>
/// </remarks>
internal sealed class AspNetAuthorizationPolicyBridge(
    string name,
    IAuthorizationService authorizationService,
    IHttpContextAccessor httpContextAccessor) : IAuthorizationPolicy {
    /// <inheritdoc />
    public string Name => name;

    /// <inheritdoc />
    public async ValueTask<bool> EvaluateAsync(AuthorizationContext context, CancellationToken ct) {
        var principal = httpContextAccessor.HttpContext?.User;
        if (principal is null) {
            return false;
        }

        var result = await authorizationService
            .AuthorizeAsync(principal, context.Resource, name)
            .ConfigureAwait(false);
        return result.Succeeded;
    }
}

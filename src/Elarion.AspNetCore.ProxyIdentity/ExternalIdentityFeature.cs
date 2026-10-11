using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Elarion.AspNetCore.ProxyIdentity;

/// <summary>
/// The <see cref="ExternalIdentity"/> the proxy scheme (or the Development stand-in) authenticated for the current
/// request, kept in <see cref="HttpContext.Features"/> so it outlives a replaced <see cref="HttpContext.User"/>.
/// </summary>
/// <remarks>
/// An application that signs in a principal of its own after authentication — a local account's, or none at all for a
/// person it does not know — replaces the principal the identity was read from; ASP.NET Core also drops the
/// authentication result once <see cref="HttpContext.User"/> is assigned. The feature is captured when the token (or the
/// stand-in) is accepted, so <see cref="ExternalIdentity.TryRead(HttpContext, ProxyIdentityOptions)"/> and the
/// <c>proxyIdentity</c> session section still see who the proxy signed in. Like the identity itself it grants nothing.
/// </remarks>
public interface IExternalIdentityFeature {
    /// <summary>Who the proxy (or the stand-in) signed in for this request.</summary>
    ExternalIdentity Identity { get; }
}

/// <summary>The feature the proxy scheme and the stand-in set.</summary>
internal sealed class ExternalIdentityFeature(ExternalIdentity identity) : IExternalIdentityFeature {
    public ExternalIdentity Identity { get; } = identity;

    /// <summary>
    /// Records the identity <paramref name="principal"/> carries on <paramref name="context"/>, or clears the feature
    /// when it carries none, so a request never keeps an identity from an earlier authentication it no longer has.
    /// </summary>
    public static void Capture(HttpContext context, ClaimsPrincipal? principal, ProxyIdentityOptions options) {
        var identity = principal is null ? null : ExternalIdentity.TryRead(principal, options);
        context.Features.Set<IExternalIdentityFeature>(identity is null ? null : new ExternalIdentityFeature(identity));
    }
}

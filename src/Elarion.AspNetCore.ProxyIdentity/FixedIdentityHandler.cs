using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Elarion.AspNetCore.ProxyIdentity;

/// <summary>
/// Signs every request in as the fixed identity (<see cref="ProxyIdentityOptions.Fixed"/>), with the issuer
/// <see cref="ProxyIdentityDefaults.FixedIssuer"/>. Nothing on the request is read: there are no switches, and a proxy
/// token is ignored.
/// </summary>
/// <remarks>Registered only while <see cref="ProxyFixedIdentityOptions.Enabled"/> is true and proxy tokens are not.</remarks>
internal sealed class FixedIdentityHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    IOptions<ProxyIdentityOptions> proxyOptions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, loggerFactory, encoder) {
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() {
        var options = proxyOptions.Value;
        var user = options.Fixed;
        var principal = StandInPrincipal.Create(
            options, ProxyIdentityDefaults.FixedScheme, ProxyIdentityDefaults.FixedIssuer,
            user.Subject.Trim(), user.Email?.Trim(), user.Name, user.Groups, user.Roles);
        // Kept beside the principal, so it survives an application replacing HttpContext.User (IExternalIdentityFeature).
        ExternalIdentityFeature.Capture(Context, principal, options);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, ProxyIdentityDefaults.FixedScheme)));
    }
}

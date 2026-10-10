using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Elarion.AspNetCore.ProxyIdentity;

/// <summary>
/// Stands in for the proxy during local development: every request is signed in with the claim shape a proxy token
/// carries (under the configured <see cref="ProxyIdentityOptions.Claims"/> names), built from
/// <see cref="ProxyIdentityOptions.Development"/>, with the issuer <see cref="ProxyIdentityDefaults.DevelopmentIssuer"/>.
/// </summary>
/// <remarks>
/// Registered only while <see cref="ProxyIdentityOptions.Enabled"/> is false, which <c>AddElarionProxyIdentity</c>
/// refuses outside the Development environment.
/// </remarks>
internal sealed class DevelopmentIdentityHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    IOptions<ProxyIdentityOptions> proxyOptions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, loggerFactory, encoder) {
    /// <summary>The longest switch value accepted; anything longer is ignored rather than becoming a claim.</summary>
    private const int MaxSwitchLength = 256;

    protected override Task<AuthenticateResult> HandleAuthenticateAsync() {
        var options = proxyOptions.Value;
        var user = Resolve(options.Development, Switched(options.Development));
        var names = options.Claims;

        var claims = new List<Claim> {
            new(ExternalIdentity.IssuerClaim, ProxyIdentityDefaults.DevelopmentIssuer),
            new(names.Subject, user.Subject),
        };
        if (!string.IsNullOrEmpty(names.Email)) {
            claims.Add(new Claim(names.Email, user.Email));
            if (!string.IsNullOrEmpty(names.EmailVerified)) {
                claims.Add(new Claim(names.EmailVerified, "true", ClaimValueTypes.Boolean));
            }
        }
        if (!string.IsNullOrEmpty(names.Name) && !string.IsNullOrWhiteSpace(user.Name)) {
            claims.Add(new Claim(names.Name, user.Name));
        }
        AddAll(claims, names.Groups, user.Groups);
        AddAll(claims, names.Roles, user.Roles);

        var nameType = string.IsNullOrEmpty(names.Name) ? names.Subject : names.Name;
        var roleType = string.IsNullOrEmpty(names.Roles) ? ClaimTypes.Role : names.Roles;
        var identity = new ClaimsIdentity(claims, ProxyIdentityDefaults.DevelopmentScheme, nameType, roleType);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), ProxyIdentityDefaults.DevelopmentScheme);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    /// <summary>The user to sign in: the configured default, a configured user the switch names, or an ad-hoc one.</summary>
    internal static (string Subject, string Email, string? Name, IEnumerable<string> Groups, IEnumerable<string> Roles) Resolve(
        ProxyDevelopmentIdentityOptions development, string? switchedTo) {
        if (switchedTo is null
            || string.Equals(switchedTo, development.Email, StringComparison.OrdinalIgnoreCase)
            || string.Equals(switchedTo, development.Subject, StringComparison.Ordinal)) {
            return (development.Subject, development.Email, development.Name, development.Groups, development.Roles);
        }

        var known = development.Users.FirstOrDefault(user =>
            string.Equals(user.Email, switchedTo, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrEmpty(user.Subject) && string.Equals(user.Subject, switchedTo, StringComparison.Ordinal)));
        if (known is not null) {
            return (SubjectOf(known.Subject, known.Email), known.Email, known.Name, known.Groups, known.Roles);
        }
        // An unknown address gets a subject derived from it, so it behaves like a first sign-in of a new person.
        return (SubjectOf(null, switchedTo), switchedTo, switchedTo, [], []);
    }

    private static string SubjectOf(string? subject, string email) {
        return string.IsNullOrWhiteSpace(subject) ? "dev:" + email.ToLowerInvariant() : subject;
    }

    private string? Switched(ProxyDevelopmentIdentityOptions development) {
        if (!string.IsNullOrEmpty(development.UserHeader)) {
            var header = Request.Headers[development.UserHeader].ToString().Trim();
            if (header.Length is > 0 and <= MaxSwitchLength) {
                return header;
            }
        }
        if (!string.IsNullOrEmpty(development.UserCookie)
            && Request.Cookies.TryGetValue(development.UserCookie, out var cookie)
            && Uri.UnescapeDataString(cookie).Trim() is { Length: > 0 and <= MaxSwitchLength } value) {
            return value;
        }
        return null;
    }

    private static void AddAll(List<Claim> claims, string? claimType, IEnumerable<string> values) {
        if (string.IsNullOrEmpty(claimType)) {
            return;
        }
        claims.AddRange(values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => new Claim(claimType, value.Trim())));
    }
}

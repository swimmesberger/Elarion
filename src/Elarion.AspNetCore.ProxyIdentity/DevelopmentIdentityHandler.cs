using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Elarion.AspNetCore.ProxyIdentity;

/// <summary>
/// Stands in for the proxy during local development: every request is signed in with the claim shape a proxy token
/// carries (under the configured <see cref="ProxyIdentityOptions.Claims"/> names), built from
/// <see cref="ProxyIdentityOptions.Development"/>, with the issuer <see cref="ProxyIdentityDefaults.DevelopmentIssuer"/>.
/// The user switch selects the user, or signs nobody in with <see cref="ProxyIdentityDefaults.DevelopmentSignedOutUser"/>;
/// the roles and groups switches replace that user's roles and groups.
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
        var development = options.Development;
        var switchedTo = ReadSwitch(development.UserHeader, development.UserCookie, emptyIsValue: false);
        if (switchedTo == ProxyIdentityDefaults.DevelopmentSignedOutUser) {
            // Signed out: nobody is signed in, as when the proxy forwarded no token, so the roles and groups switches have
            // nobody to apply to, and no identity may linger in the request's features.
            ExternalIdentityFeature.Capture(Context, null, options);
            return Task.FromResult(AuthenticateResult.NoResult());
        }
        var user = Resolve(development, switchedTo);
        // A present roles/groups switch replaces the resolved user's values for this request; absent, they stay.
        var groups = SwitchedList(development.GroupsHeader, development.GroupsCookie) ?? user.Groups;
        var roles = SwitchedList(development.RolesHeader, development.RolesCookie) ?? user.Roles;
        var principal = StandInPrincipal.Create(
            options, ProxyIdentityDefaults.DevelopmentScheme, ProxyIdentityDefaults.DevelopmentIssuer,
            user.Subject, user.Email, user.Name, groups, roles);
        // Kept beside the principal, so it survives an application replacing HttpContext.User (IExternalIdentityFeature).
        ExternalIdentityFeature.Capture(Context, principal, options);
        var ticket = new AuthenticationTicket(principal, ProxyIdentityDefaults.DevelopmentScheme);
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

    /// <summary>
    /// The trimmed value of a switch: the header when the request carries an acceptable one, else the (URL-encoded)
    /// cookie; null when neither is present, the switch is disabled, or the value is too long. An empty value counts as
    /// absent unless <paramref name="emptyIsValue"/>, where it is a deliberate "none".
    /// </summary>
    private string? ReadSwitch(string? headerName, string? cookieName, bool emptyIsValue) {
        if (!string.IsNullOrEmpty(headerName)
            && Request.Headers.TryGetValue(headerName, out var header)
            && Accept(header.ToString(), emptyIsValue) is { } fromHeader) {
            return fromHeader;
        }
        if (!string.IsNullOrEmpty(cookieName)
            && Request.Cookies.TryGetValue(cookieName, out var cookie)
            && Accept(Uri.UnescapeDataString(cookie), emptyIsValue) is { } fromCookie) {
            return fromCookie;
        }
        return null;
    }

    /// <summary>The comma-separated values of a list switch (trimmed, de-duplicated), or null when the switch is absent.</summary>
    private IReadOnlyList<string>? SwitchedList(string? headerName, string? cookieName) {
        return ReadSwitch(headerName, cookieName, emptyIsValue: true)?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static string? Accept(string raw, bool emptyIsValue) {
        var value = raw.Trim();
        return value.Length <= MaxSwitchLength && (emptyIsValue || value.Length > 0) ? value : null;
    }
}

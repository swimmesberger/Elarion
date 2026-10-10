using System.Security.Claims;
using Elarion.Abstractions.Identity;

namespace Elarion.AspNetCore.ProxyIdentity;

/// <summary>
/// Who the authenticating proxy says is calling, read from the validated token: the seam between the proxy and the
/// application. It grants nothing by itself — linking it to a local account, mapping groups to permissions, or trusting
/// the e-mail address for an invitation is application policy built on top of it.
/// </summary>
/// <remarks>
/// Read it from the request principal in host code (<see cref="TryRead(ClaimsPrincipal, ProxyIdentityOptions)"/>) or,
/// transport-neutrally inside a handler, from <see cref="ICurrentUser"/>
/// (<see cref="TryRead(ICurrentUser, ProxyIdentityOptions)"/>).
/// </remarks>
public sealed record ExternalIdentity {
    /// <summary>The claim the issuer is read from. JwtBearer keeps inbound claim names, so this is the token's own <c>iss</c>.</summary>
    public const string IssuerClaim = "iss";

    /// <summary>The token's issuer. Together with <see cref="Subject"/> it is the stable key of the person.</summary>
    public required string Issuer { get; init; }

    /// <summary>The issuer's never reassigned subject (<see cref="ProxyIdentityClaimNames.Subject"/>).</summary>
    public required string Subject { get; init; }

    /// <summary>The e-mail address the token names, trimmed, or null.</summary>
    public string? Email { get; init; }

    /// <summary>
    /// Whether <see cref="Email"/> may be treated as verified, already decided by
    /// <see cref="ProxyIdentityOptions.EmailTrust"/>: always <see langword="false"/> without an address or under
    /// <see cref="ProxyEmailTrust.None"/>.
    /// </summary>
    public bool EmailVerified { get; init; }

    /// <summary>The display name the token carries, or null.</summary>
    public string? Name { get; init; }

    /// <summary>The person's groups at the identity provider (<see cref="ProxyIdentityClaimNames.Groups"/>).</summary>
    public IReadOnlyList<string> Groups { get; init; } = [];

    /// <summary>The person's roles for this application (<see cref="ProxyIdentityClaimNames.Roles"/>, RFC 9068 §2.2.3.1).</summary>
    public IReadOnlyList<string> Roles { get; init; } = [];

    /// <summary>Whether this is the Development stand-in identity rather than a token the proxy signed.</summary>
    public bool IsDevelopmentIdentity => Issuer == ProxyIdentityDefaults.DevelopmentIssuer;

    /// <summary>
    /// Reads the identity from an authenticated principal, or returns null when the principal is anonymous or carries
    /// no issuer or subject.
    /// </summary>
    public static ExternalIdentity? TryRead(ClaimsPrincipal principal, ProxyIdentityOptions options) {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(options);
        if (principal.Identity?.IsAuthenticated is not true) {
            return null;
        }
        return Read(type => principal.FindAll(type).Select(claim => claim.Value), options);
    }

    /// <summary>
    /// Reads the identity from the current user (transport-neutral, usable in handlers), or returns null when the
    /// user is anonymous or carries no issuer or subject.
    /// </summary>
    public static ExternalIdentity? TryRead(ICurrentUser user, ProxyIdentityOptions options) {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(options);
        return user.IsAuthenticated ? Read(user.GetClaimValues, options) : null;
    }

    private static ExternalIdentity? Read(Func<string, IEnumerable<string>> values, ProxyIdentityOptions options) {
        var names = options.Claims;
        var issuer = First(values, IssuerClaim);
        var subject = First(values, names.Subject);
        if (issuer is null || subject is null) {
            return null;
        }

        var email = First(values, names.Email);
        var verified = email is not null && options.EmailTrust switch {
            ProxyEmailTrust.Trusted => true,
            ProxyEmailTrust.Claim => bool.TryParse(First(values, names.EmailVerified), out var claimed) && claimed,
            _ => false,
        };
        return new ExternalIdentity {
            Issuer = issuer,
            Subject = subject,
            Email = email,
            EmailVerified = verified,
            Name = First(values, names.Name),
            Groups = All(values, names.Groups),
            Roles = All(values, names.Roles),
        };
    }

    private static string? First(Func<string, IEnumerable<string>> values, string? claimType) {
        return All(values, claimType) is [var first, ..] ? first : null;
    }

    /// <summary>
    /// Every non-blank value of <paramref name="claimType"/>, trimmed. A JSON array claim arrives as one claim per
    /// element and a single string as one claim, so both shapes read the same.
    /// </summary>
    private static IReadOnlyList<string> All(Func<string, IEnumerable<string>> values, string? claimType) {
        if (string.IsNullOrEmpty(claimType)) {
            return [];
        }
        return values(claimType)
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}

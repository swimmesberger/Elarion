using System.Security.Claims;

namespace Elarion.AspNetCore.ProxyIdentity;

/// <summary>
/// Builds the principal of an identity the application configured rather than a proxy signed (the Development stand-in,
/// the fixed identity): the claim shape a proxy token carries, under the configured <see cref="ProxyIdentityOptions.Claims"/>
/// names, so application code reads it exactly like a real one.
/// </summary>
internal static class StandInPrincipal {
    public static ClaimsPrincipal Create(
        ProxyIdentityOptions options,
        string scheme,
        string issuer,
        string subject,
        string? email,
        string? name,
        IEnumerable<string> groups,
        IEnumerable<string> roles) {
        var names = options.Claims;
        var claims = new List<Claim> {
            new(ExternalIdentity.IssuerClaim, issuer),
            new(names.Subject, subject),
        };
        if (!string.IsNullOrEmpty(names.Email) && !string.IsNullOrWhiteSpace(email)) {
            claims.Add(new Claim(names.Email, email));
            // The address comes from the application's own configuration, so it is as verified as it gets.
            if (!string.IsNullOrEmpty(names.EmailVerified)) {
                claims.Add(new Claim(names.EmailVerified, "true", ClaimValueTypes.Boolean));
            }
        }
        if (!string.IsNullOrEmpty(names.Name) && !string.IsNullOrWhiteSpace(name)) {
            claims.Add(new Claim(names.Name, name));
        }
        AddAll(claims, names.Groups, groups);
        AddAll(claims, names.Roles, roles);

        var nameType = string.IsNullOrEmpty(names.Name) ? names.Subject : names.Name;
        var roleType = string.IsNullOrEmpty(names.Roles) ? ClaimTypes.Role : names.Roles;
        return new ClaimsPrincipal(new ClaimsIdentity(claims, scheme, nameType, roleType));
    }

    private static void AddAll(List<Claim> claims, string? claimType, IEnumerable<string> values) {
        if (string.IsNullOrEmpty(claimType)) {
            return;
        }
        claims.AddRange(values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => new Claim(claimType, value.Trim())));
    }
}

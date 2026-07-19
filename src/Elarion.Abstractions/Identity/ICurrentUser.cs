namespace Elarion.Abstractions.Identity;

/// <summary>
/// Provides framework-level access to the current authenticated user.
/// </summary>
public interface ICurrentUser {
    /// <summary>Unique user identifier for the current principal.</summary>
    string UserId { get; }

    /// <summary>User email address, if available.</summary>
    string? Email { get; }

    /// <summary>Roles assigned to the current principal.</summary>
    IReadOnlyList<string> Roles { get; }

    /// <summary>Whether the current principal is authenticated.</summary>
    bool IsAuthenticated { get; }

    /// <summary>Returns whether the current principal has the specified role.</summary>
    bool IsInRole(string role);

    /// <summary>
    /// Returns whether the current principal has a claim of <paramref name="type"/> with the given
    /// <paramref name="value"/>.
    /// </summary>
    /// <remarks>
    /// A default interface method so existing implementers keep compiling; the default <b>fails closed</b>
    /// (returns <see langword="false"/>) so a stale implementer denies rather than silently authorizes.
    /// Real implementers (e.g. the claims-based <c>ClaimsPrincipalCurrentUser</c>) override it to answer from
    /// the principal's claims.
    /// </remarks>
    bool HasClaim(string type, string value) {
        return false;
    }

    /// <summary>Returns all values of claims of the given <paramref name="type"/> held by the current principal.</summary>
    /// <remarks>A default interface method that fails closed (returns an empty sequence); see <see cref="HasClaim"/>.</remarks>
    IEnumerable<string> GetClaimValues(string type) {
        return [];
    }
}

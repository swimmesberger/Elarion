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
}


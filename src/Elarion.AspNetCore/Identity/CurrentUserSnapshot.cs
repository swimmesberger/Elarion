using System.Security.Claims;
using Microsoft.Extensions.Options;
using Elarion.Abstractions.Identity;

namespace Elarion.AspNetCore.Identity;

/// <summary>
/// Scoped copy of the authenticated ASP.NET principal exposed through the framework identity abstraction.
/// </summary>
public sealed class CurrentUserSnapshot(IOptions<AspNetCoreCurrentUserOptions> options) : ICurrentUser {
    private bool _initialized;
    private bool _isAuthenticated;
    private string? _userId;
    private string? _email;
    private IReadOnlyList<string> _roles = [];

    private AspNetCoreCurrentUserOptions Options => options.Value;

    /// <inheritdoc />
    public string UserId {
        get {
            EnsureInitialized();

            return _userId
                ?? throw new InvalidOperationException($"User id claim '{Options.UserIdClaimType}' is not set.");
        }
    }

    /// <inheritdoc />
    public string? Email {
        get {
            EnsureInitialized();

            return _email;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Roles {
        get {
            EnsureInitialized();

            return _roles;
        }
    }

    /// <inheritdoc />
    public bool IsAuthenticated {
        get {
            EnsureInitialized();

            return _isAuthenticated;
        }
    }

    /// <inheritdoc />
    public bool IsInRole(string role) =>
        Roles.Contains(role, StringComparer.Ordinal);

    internal void Initialize(ClaimsPrincipal principal) {
        _isAuthenticated = principal.Identity?.IsAuthenticated == true;
        _userId = principal.FindFirstValue(Options.UserIdClaimType);
        _email = principal.FindFirstValue(Options.EmailClaimType);

        var claimRoles = principal.FindAll(Options.RoleClaimType)
            .Select(claim => claim.Value)
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        _roles = claimRoles.Length > 0 || !_isAuthenticated
            ? claimRoles
            : Options.DefaultRolesWhenAuthenticated.ToArray();
        _initialized = true;
    }

    private void EnsureInitialized() {
        if (!_initialized) {
            throw new InvalidOperationException("Current user has not been initialized. Call UseElarionCurrentUser() after authentication middleware before accessing ICurrentUser.");
        }
    }
}

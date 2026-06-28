using System.Security.Claims;
using Microsoft.Extensions.Options;
using Elarion.Abstractions.Identity;

namespace Elarion.AspNetCore.Identity;

/// <summary>
/// Scoped view of the authenticated ASP.NET principal exposed through the framework identity abstraction.
/// </summary>
/// <remarks>
/// Holds the <see cref="ClaimsPrincipal"/> set by <see cref="Initialize"/> and materializes the claims lookup
/// and roles <b>lazily</b> on first access (then caches them). Lazy materialization is what makes seeding the
/// snapshot uniform across transports: building it from the principal per dispatch call costs nothing until a
/// handler actually reads a claim, and each snapshot parses at most once — so JSON-RPC/MCP can seed a fresh
/// per-call snapshot without the request-scope one ever being re-parsed.
/// </remarks>
public sealed class CurrentUserSnapshot(IOptions<AspNetCoreCurrentUserOptions> options) : ICurrentUser {
    private ClaimsPrincipal? _principal;
    private ILookup<string, string>? _claims;
    private IReadOnlyList<string>? _roles;

    private AspNetCoreCurrentUserOptions Options => options.Value;

    private ClaimsPrincipal Principal =>
        _principal ?? throw new InvalidOperationException(
            "Current user has not been initialized. Call UseElarionCurrentUser() after authentication middleware before accessing ICurrentUser.");

    /// <inheritdoc />
    public string UserId =>
        Principal.FindFirstValue(Options.UserIdClaimType)
        ?? throw new InvalidOperationException($"User id claim '{Options.UserIdClaimType}' is not set.");

    /// <inheritdoc />
    public string? Email => Principal.FindFirstValue(Options.EmailClaimType);

    /// <inheritdoc />
    public IReadOnlyList<string> Roles => _roles ??= ResolveRoles();

    /// <inheritdoc />
    public bool IsAuthenticated => Principal.Identity?.IsAuthenticated == true;

    /// <inheritdoc />
    public bool IsInRole(string role) =>
        Roles.Contains(role, StringComparer.Ordinal);

    /// <inheritdoc />
    public bool HasClaim(string type, string value) =>
        Claims[type].Contains(value, StringComparer.Ordinal);

    /// <inheritdoc />
    public IEnumerable<string> GetClaimValues(string type) =>
        Claims[type];

    /// <summary>
    /// Sets the principal this snapshot exposes. Claims and roles are materialized lazily on first access, so
    /// this is cheap and may be called once per dispatch scope. Called by <see cref="CurrentUserMiddleware"/>
    /// (HTTP request scope) and by the current-user dispatch-scope initializer (JSON-RPC / MCP call scopes).
    /// </summary>
    internal void Initialize(ClaimsPrincipal principal) => _principal = principal;

    private ILookup<string, string> Claims =>
        // Materialized once per snapshot, on first claim access; the strings are immutable, so the lookup is a
        // true snapshot from that point and holds no live reference to the principal's Claim objects.
        _claims ??= Principal.Claims.ToLookup(claim => claim.Type, claim => claim.Value, StringComparer.Ordinal);

    private IReadOnlyList<string> ResolveRoles() {
        var claimRoles = Principal.FindAll(Options.RoleClaimType)
            .Select(claim => claim.Value)
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return claimRoles.Length > 0 || !IsAuthenticated
            ? claimRoles
            : Options.DefaultRolesWhenAuthenticated.ToArray();
    }
}

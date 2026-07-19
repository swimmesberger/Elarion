using System.Security.Claims;
using Elarion.Abstractions.Identity;

namespace Elarion.Identity;

/// <summary>
/// The default, transport-neutral <see cref="ICurrentUser"/>: a scoped view over an authenticated
/// <see cref="ClaimsPrincipal"/>, with no ASP.NET dependency. Any transport (HTTP, JSON-RPC, MCP, gRPC,
/// console) seeds it with the caller's principal and handlers read it the same way.
/// </summary>
/// <remarks>
/// Holds the principal set by <see cref="Initialize"/> and materializes the claims lookup and roles
/// <b>lazily</b> on first access (then caches). Lazy materialization makes seeding a fresh instance per
/// dispatch call free until a claim is read, and each instance parses at most once. Seed it through the
/// dispatch-scope rail — capture the principal into a <c>DispatchScopeContext</c> at the transport boundary
/// and the registered current-user initializer applies it — rather than calling <see cref="Initialize"/>
/// directly (which is internal).
/// <para>
/// When no principal has been seeded (e.g. a background delivery scope such as the integration-event pump or
/// the outbox, which run consumers off any request boundary), this instance behaves as an <b>anonymous</b>
/// caller — <see cref="IsAuthenticated"/> is <see langword="false"/>, <see cref="Roles"/> and the claim
/// accessors are empty, and <see cref="HasClaim"/> returns <see langword="false"/> — so an authorization or
/// feature-gate check <b>fails closed</b> (denies) rather than throwing. Only <see cref="UserId"/> still
/// throws, because the contract is non-nullable and there is genuinely no id for an anonymous caller;
/// authorization consults <see cref="IsAuthenticated"/> first, so it never reaches that path for an
/// unauthenticated caller.
/// </para>
/// </remarks>
public sealed class ClaimsPrincipalCurrentUser(ClaimsCurrentUserOptions options) : ICurrentUser {
    private static readonly ClaimsPrincipal AnonymousPrincipal = new(new ClaimsIdentity());

    private ClaimsPrincipal? _principal;
    private ILookup<string, string>? _claims;
    private IReadOnlyList<string>? _roles;

    // When unseeded, expose an anonymous (unauthenticated, claim-less) principal so authorization and
    // feature-gate targeting fail closed instead of crashing background delivery scopes that never seed a user.
    private ClaimsPrincipal Principal => _principal ?? AnonymousPrincipal;

    /// <inheritdoc />
    public string UserId =>
        Principal.FindFirst(options.UserIdClaimType)?.Value
        ?? throw new InvalidOperationException($"User id claim '{options.UserIdClaimType}' is not set.");

    /// <inheritdoc />
    public string? Email => Principal.FindFirst(options.EmailClaimType)?.Value;

    /// <inheritdoc />
    public IReadOnlyList<string> Roles => _roles ??= ResolveRoles();

    /// <inheritdoc />
    public bool IsAuthenticated => Principal.Identity?.IsAuthenticated == true;

    /// <inheritdoc />
    public bool IsInRole(string role) {
        return Roles.Contains(role, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public bool HasClaim(string type, string value) {
        return Claims[type].Contains(value, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public IEnumerable<string> GetClaimValues(string type) {
        return Claims[type];
    }

    /// <summary>
    /// Sets the principal this instance exposes. Claims and roles are materialized lazily on first access, so
    /// this is cheap and is called once per dispatch scope by the current-user dispatch-scope initializer.
    /// </summary>
    internal void Initialize(ClaimsPrincipal principal) {
        _principal = principal;
    }

    private ILookup<string, string> Claims =>
        _claims ??= Principal.Claims.ToLookup(claim => claim.Type, claim => claim.Value, StringComparer.Ordinal);

    private IReadOnlyList<string> ResolveRoles() {
        var claimRoles = Principal.FindAll(options.RoleClaimType)
            .Select(claim => claim.Value)
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return claimRoles.Length > 0 || !IsAuthenticated
            ? claimRoles
            : options.DefaultRolesWhenAuthenticated.ToArray();
    }
}

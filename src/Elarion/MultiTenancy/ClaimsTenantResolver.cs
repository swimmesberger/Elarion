using Elarion.Abstractions.Identity;
using Elarion.Abstractions.MultiTenancy;

namespace Elarion.MultiTenancy;

/// <summary>
/// The shipped <see cref="ITenantResolver"/>: reads the tenant id from a claim on the current principal.
/// Transport-neutral — it depends only on <see cref="ICurrentUser"/>, so it answers identically under HTTP,
/// JSON-RPC, MCP, and a connection adapter.
/// </summary>
/// <remarks>
/// An unauthenticated principal has no tenant, so resolution returns <see langword="null"/> and the scope
/// fails closed. A principal carrying more than one tenant claim is ambiguous and also resolves to
/// <see langword="null"/>: picking the first would make the isolation boundary depend on claim ordering.
/// </remarks>
/// <param name="user">The current principal.</param>
/// <param name="options">Which claim carries the tenant id.</param>
public sealed class ClaimsTenantResolver(ICurrentUser user, TenantScopingOptions options) : ITenantResolver {
    /// <inheritdoc />
    public string? Resolve() {
        if (!user.IsAuthenticated) return null;

        string? found = null;
        foreach (var value in user.GetClaimValues(options.ClaimType)) {
            if (string.IsNullOrWhiteSpace(value)) continue;
            // A second tenant claim makes the boundary ambiguous; deny rather than pick one by position.
            if (found is not null) return null;

            found = value;
        }

        return found;
    }
}

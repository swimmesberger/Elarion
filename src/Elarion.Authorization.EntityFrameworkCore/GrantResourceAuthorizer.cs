using Elarion.Abstractions.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Elarion.Authorization.EntityFrameworkCore;

/// <summary>
/// The grants-backed <see cref="IResourceAuthorizer"/>: authorizes a point check when the resource-grants table
/// records a share of that resource (matched by <c>ResourceType.Name</c> and the stringified id) with the
/// caller's user id or any of their roles, for the operation. Owner-based access is the handler's concern via
/// the escape hatch, or modeled as a user grant.
/// </summary>
internal sealed class GrantResourceAuthorizer(IResourceGrantSource grants) : IResourceAuthorizer {
    public async ValueTask<bool> AuthorizeResourceAsync(ResourceAuthorizationContext context, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(context);

        var user = context.User;
        if (!user.IsAuthenticated || context.ResourceId is null) {
            return false;
        }

        var resourceType = context.ResourceType.Name;
        var resourceId = context.ResourceId.ToString();
        var operation = context.Operation.Name;
        var userId = user.UserId;
        var roles = user.Roles;

        return await grants.Grants
            .AnyAsync(
                grant => grant.ResourceType == resourceType
                    && grant.ResourceId == resourceId
                    && grant.Operation == operation
                    && ((grant.PrincipalKind == "user" && grant.PrincipalId == userId)
                        || (grant.PrincipalKind == "role" && roles.Contains(grant.PrincipalId))),
                ct)
            .ConfigureAwait(false);
    }
}

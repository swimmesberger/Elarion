using Elarion.Abstractions.Authorization;
using Microsoft.Extensions.Logging;

namespace Elarion.Authorization;

/// <summary>
/// The fail-closed default <see cref="IResourceAuthorizer"/>: a resource type with its own registered check (for
/// example <c>AddElarionResourceFilterCheck</c>) is answered by that check; for every other type, with no resource
/// backend registered (for example <c>AddElarionResourceAuthorization</c>), the <c>[RequireResource]</c> check denies
/// and logs a warning, so a missing registration is a visible 403 rather than a silent allow.
/// </summary>
internal sealed class DenyResourceAuthorizer(ILogger<DenyResourceAuthorizer> logger, IServiceProvider services)
    : IResourceAuthorizer {
    public ValueTask<bool> AuthorizeResourceAsync(ResourceAuthorizationContext context, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(context);

        if (ResourceTypeAuthorizer.Find(services, context.ResourceType) is { } typed)
            return typed.AuthorizeResourceAsync(context, ct);

        logger.LogWarning(
            "No resource check is registered for '{ResourceType}' and no IResourceAuthorizer backend is registered "
            + "(e.g. via AddElarionResourceFilterCheck or AddElarionResourceAuthorization); denying access.",
            context.ResourceTypeName);
        return ValueTask.FromResult(false);
    }
}

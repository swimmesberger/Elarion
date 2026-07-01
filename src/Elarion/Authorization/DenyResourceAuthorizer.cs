using Elarion.Abstractions.Authorization;
using Microsoft.Extensions.Logging;

namespace Elarion.Authorization;

/// <summary>
/// The fail-closed default <see cref="IResourceAuthorizer"/>: with no resource backend registered (for example
/// <c>AddElarionResourceAuthorization</c>), every <c>[RequireResource]</c> check denies and logs a warning, so a
/// missing registration is a visible 403 rather than a silent allow.
/// </summary>
internal sealed class DenyResourceAuthorizer(ILogger<DenyResourceAuthorizer> logger) : IResourceAuthorizer {
    public ValueTask<bool> AuthorizeResourceAsync(ResourceAuthorizationContext context, CancellationToken ct) {
        logger.LogWarning(
            "No IResourceAuthorizer is registered (e.g. via AddElarionResourceAuthorization); denying access to "
            + "resource '{ResourceType}'.",
            context.ResourceTypeName);
        return ValueTask.FromResult(false);
    }
}

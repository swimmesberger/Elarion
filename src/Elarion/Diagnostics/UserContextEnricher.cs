using System.Text;
using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Diagnostics;
using Elarion.Abstractions.Identity;

namespace Elarion.Diagnostics;

/// <summary>
/// The built-in <see cref="IHandlerContextEnricher"/>: stamps the calling user's identity — <c>user.id</c> +
/// <c>user.roles</c> + <c>user.permissions</c> from <see cref="ICurrentUser"/> — onto the handler trace span and
/// log scope. Registered by default when current-user support is added (<c>AddElarionClaimsCurrentUser</c>) and by
/// <c>AddElarionUserContextEnrichment</c>; governed by <see cref="UserContextEnrichmentOptions"/>.
/// </summary>
/// <remarks>
/// On by default; <c>user.id</c>/<c>user.roles</c> are OpenTelemetry semantic-convention keys (<c>user.permissions</c>
/// mirrors them — no convention exists for permissions). Email is PII and off by default. Contributes nothing for an
/// anonymous caller or when <see cref="UserContextEnrichmentOptions.Enabled"/> is <see langword="false"/>. User
/// identity is never recorded on metrics (unbounded cardinality) — it rides only the span and the log scope.
/// </remarks>
public sealed class UserContextEnricher(
    ICurrentUser user,
    UserContextEnrichmentOptions options,
    AuthorizationOptions? authorizationOptions = null
) : IHandlerContextEnricher {
    private const string DefaultPermissionClaimType = "permission";

    /// <inheritdoc />
    public void Enrich(HandlerEnrichmentContext context) {
        if (!options.Enabled
            || user is not { IsAuthenticated: true }
            || user.UserId is not { Length: > 0 } userId)
            return;

        context.SetTag("user.id", userId);
        context.AddScopeItem("UserId", userId);

        if (options.IncludeRoles) {
            var roles = JoinBounded(user.Roles, options.MaxItems);
            if (roles.Length > 0) {
                context.SetTag("user.roles", roles);
                context.AddScopeItem("UserRoles", roles);
            }
        }

        if (options.IncludePermissions) {
            var claimType = authorizationOptions?.PermissionClaimType ?? DefaultPermissionClaimType;
            var permissions = JoinBounded(user.GetClaimValues(claimType), options.MaxItems);
            if (permissions.Length > 0) {
                // No OpenTelemetry semantic convention exists for permissions; user.permissions mirrors user.roles.
                context.SetTag("user.permissions", permissions);
                context.AddScopeItem("UserPermissions", permissions);
            }
        }

        if (options.IncludeEmail && user.Email is { Length: > 0 } email) {
            context.SetTag("user.email", email);
            context.AddScopeItem("UserEmail", email);
        }
    }

    // Bounded, allocation-light join (no LINQ) over roles or claim values: caps at maxItems so a caller with many
    // roles/permissions can't blow up the tag, and skips null/empty entries.
    private static string JoinBounded(IEnumerable<string> values, int maxItems) {
        string? first = null;
        StringBuilder? builder = null;
        var count = 0;

        foreach (var value in values) {
            if (string.IsNullOrEmpty(value)) continue;

            if (count == 0) {
                first = value;
            }
            else {
                builder ??= new StringBuilder(first);
                builder.Append(',').Append(value);
            }

            if (++count >= maxItems) break;
        }

        if (count == 0) return string.Empty;

        return builder?.ToString() ?? first!;
    }
}

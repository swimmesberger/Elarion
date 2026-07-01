using Elarion.Abstractions.Identity;
using OpenFeature.Model;

namespace Elarion.FeatureFlags.OpenFeature;

/// <summary>
/// Builds an OpenFeature <see cref="EvaluationContext"/> from Elarion's <see cref="ICurrentUser"/>, so targeting and
/// percentage rollouts evaluate against the authenticated user the same way under JSON-RPC, MCP, and HTTP — with no
/// <c>HttpContext</c> dependency.
/// </summary>
/// <remarks>
/// The user id is set both as the standard OpenFeature <see cref="EvaluationContext.TargetingKey"/> (which
/// vendor providers such as LaunchDarkly/ConfigCat/flagd consume) and as the <c>UserId</c>/<c>Groups</c> attributes
/// the Microsoft.FeatureManagement OpenFeature provider reads, so a single context drives every backend. An
/// unauthenticated caller yields an empty context (no targeting key), which providers treat as anonymous.
/// </remarks>
public static class ElarionEvaluationContext {
    /// <summary>Targeting attribute key the Microsoft.FeatureManagement provider reads for the subject id.</summary>
    public const string UserIdKey = "UserId";

    /// <summary>Targeting attribute key the Microsoft.FeatureManagement provider reads for the subject's groups.</summary>
    public const string GroupsKey = "Groups";

    /// <summary>Creates an evaluation context for the supplied user.</summary>
    public static EvaluationContext Create(ICurrentUser currentUser) {
        var builder = EvaluationContext.Builder();

        if (currentUser.IsAuthenticated && !string.IsNullOrWhiteSpace(currentUser.UserId)) {
            builder.SetTargetingKey(currentUser.UserId);
            builder.Set(UserIdKey, currentUser.UserId);
        }

        if (currentUser.Roles.Count > 0) {
            var groups = new List<Value>(currentUser.Roles.Count);
            foreach (var role in currentUser.Roles) {
                groups.Add(new Value(role));
            }

            builder.Set(GroupsKey, new Value(groups));
        }

        return builder.Build();
    }
}

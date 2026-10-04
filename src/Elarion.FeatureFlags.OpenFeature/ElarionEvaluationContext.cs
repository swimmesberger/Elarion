using Elarion.Abstractions.Features;
using OpenFeature.Model;

namespace Elarion.FeatureFlags.OpenFeature;

/// <summary>
/// Builds an OpenFeature <see cref="EvaluationContext"/> from Elarion's explicit <see cref="FeatureEvaluationContext"/>, so
/// targeting and percentage rollouts evaluate against the subject the caller passed — the authenticated user for the
/// ambient context — the same way under JSON-RPC, MCP, and HTTP, with no <c>HttpContext</c> dependency.
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

    /// <summary>Targeting attribute key carrying the tenant id, when the evaluation is tenant-scoped.</summary>
    public const string TenantIdKey = "TenantId";

    /// <summary>Creates an OpenFeature evaluation context for the supplied Elarion context.</summary>
    public static EvaluationContext Create(FeatureEvaluationContext context) {
        var builder = EvaluationContext.Builder();

        if (!string.IsNullOrWhiteSpace(context.UserId)) {
            builder.SetTargetingKey(context.UserId);
            builder.Set(UserIdKey, context.UserId);
        }

        if (context.Roles.Count > 0) {
            var groups = new List<Value>(context.Roles.Count);
            foreach (var role in context.Roles) groups.Add(new Value(role));

            builder.Set(GroupsKey, new Value(groups));
        }

        if (!string.IsNullOrWhiteSpace(context.TenantId)) builder.Set(TenantIdKey, context.TenantId);

        foreach (var (key, value) in context.Attributes) builder.Set(key, value);

        return builder.Build();
    }
}

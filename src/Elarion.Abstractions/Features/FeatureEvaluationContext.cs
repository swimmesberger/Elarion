using Elarion.Abstractions.Identity;
using Elarion.Abstractions.MultiTenancy;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.Abstractions.Features;

/// <summary>
/// The explicit input a feature-flag owner evaluates a flag against: who is asking, for which tenant, and the
/// services the resolver may use. Every owner — a code-defined <see cref="IFeatureFlagResolver"/> or the
/// host's <see cref="IBackendFeatureFlagEvaluator"/> — receives one of these instead of reaching into the
/// ambient DI scope, so a flag can be evaluated for a caller other than the current request (an admin previewing
/// another user's experience, a background job acting for a user) by passing a different context.
/// </summary>
/// <remarks>
/// <see cref="FromScope"/> builds the ambient context from the scope's <see cref="ICurrentUser"/> and, when
/// registered, <see cref="ITenantContext"/>; the gate decorators, the session snapshot, and
/// <see cref="IFeatureFlagService"/>'s ambient overloads all evaluate through it, so the same flag cannot answer
/// differently depending on the entry point. It is an immutable record: derive another caller's context with
/// <c>context with { UserId = "u-42", Roles = ["admin"] }</c>.
/// </remarks>
public sealed record FeatureEvaluationContext {
    /// <summary>
    /// The services a resolver may use (a <c>DbContext</c>, a clock, a configuration reader). For the ambient
    /// context this is the current request's scope.
    /// </summary>
    public required IServiceProvider Services { get; init; }

    /// <summary>The subject's user id, or <see langword="null"/> for an anonymous caller.</summary>
    public string? UserId { get; init; }

    /// <summary>The subject's roles; empty for an anonymous caller.</summary>
    public IReadOnlyList<string> Roles { get; init; } = [];

    /// <summary>
    /// The tenant the flag is evaluated for, or <see langword="null"/> when no tenant is resolved or the scope
    /// spans every tenant (<see cref="ITenantContext.IsSystemScope"/>).
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// Extra targeting attributes (plan, region, build channel) for resolvers and backends that key on more than
    /// the user and tenant. Empty by default; the ambient context never populates it.
    /// </summary>
    public IReadOnlyDictionary<string, string> Attributes { get; init; } = EmptyAttributes;

    /// <summary>Whether the subject is a known user (<see cref="UserId"/> is set).</summary>
    public bool IsAuthenticated => UserId is not null;

    private static readonly IReadOnlyDictionary<string, string> EmptyAttributes =
        new Dictionary<string, string>(0);

    /// <summary>
    /// Builds the ambient context for <paramref name="services"/>: the scope's <see cref="ICurrentUser"/> (an
    /// unauthenticated or unregistered user yields an anonymous context) and its <see cref="ITenantContext"/>.
    /// </summary>
    /// <param name="services">The request scope the flag is being evaluated in.</param>
    public static FeatureEvaluationContext FromScope(IServiceProvider services) {
        ArgumentNullException.ThrowIfNull(services);

        var user = services.GetService<ICurrentUser>();
        var tenant = services.GetService<ITenantContext>();
        var authenticated = user is { IsAuthenticated: true } && !string.IsNullOrWhiteSpace(user.UserId);

        return new FeatureEvaluationContext {
            Services = services,
            UserId = authenticated ? user!.UserId : null,
            Roles = authenticated ? user!.Roles : [],
            TenantId = tenant is { IsSystemScope: false } ? tenant.TenantId : null
        };
    }
}

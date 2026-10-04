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
    private IServiceProvider _services = NoServices.Instance;

    /// <summary>
    /// The services a resolver may use (a <c>DbContext</c>, a clock, a configuration reader). For the ambient
    /// context (<see cref="FromScope"/>) this is the current request's scope. Optional: a context built for a
    /// resolver or backend that needs no services (a test, a pure targeting rule) may leave it unset, and then
    /// carries an empty provider — <c>GetService</c> returns <see langword="null"/> and <c>GetRequiredService</c>
    /// throws an <see cref="InvalidOperationException"/> that says the context carries no services. It can never be
    /// <see langword="null"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    public IServiceProvider Services {
        get => _services;
        init => _services = value ?? throw new ArgumentNullException(nameof(value));
    }

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

    private sealed class NoServices : IServiceProvider, ISupportRequiredService {
        public static readonly NoServices Instance = new();

        public object? GetService(Type serviceType) {
            return serviceType == typeof(IServiceProvider) ? this : null;
        }

        public object GetRequiredService(Type serviceType) {
            return GetService(serviceType) ?? throw new InvalidOperationException(
                $"This {nameof(FeatureEvaluationContext)} carries no services, so '{serviceType}' cannot be resolved. "
                + $"Set {nameof(Services)} (or build the context with {nameof(FromScope)}) when the resolver needs services.");
        }
    }
}

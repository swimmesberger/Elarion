using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Elarion.Abstractions.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.Authorization;

/// <summary>
/// Registers the transport-neutral authorization runtime: the default <see cref="ClaimsAuthorizer"/> and
/// named <see cref="IAuthorizationPolicy"/> instances.
/// </summary>
public static class AuthorizationServiceCollectionExtensions {
    /// <summary>
    /// Registers <see cref="AuthorizationOptions"/> and the default <see cref="IAuthorizer"/>
    /// (<see cref="ClaimsAuthorizer"/>). Required by any host whose handlers use authorization attributes;
    /// <c>AddElarionIdentity</c> calls this for you. Uses <c>TryAdd</c> so a host can override the authorizer.
    /// </summary>
    public static IServiceCollection AddElarionAuthorization(
        this IServiceCollection services,
        Action<AuthorizationOptions>? configure = null) {
        ArgumentNullException.ThrowIfNull(services);

        var options = new AuthorizationOptions();
        configure?.Invoke(options);

        services.TryAddSingleton(options);
        services.TryAddScoped<IAuthorizer, ClaimsAuthorizer>();
        // Fail-closed default; AddElarionResourceAuthorization replaces it with the grants-backed authorizer.
        services.TryAddScoped<IResourceAuthorizer, DenyResourceAuthorizer>();
        // The generated per-module PermissionCatalogModule contributions (registered via ConfigureDefaultServices)
        // aggregate into this catalog, so seeding/admin code can enumerate every [RequirePermission]/[RequireRole].
        services.TryAddSingleton<IPermissionCatalog, PermissionCatalog>();
        return services;
    }

    /// <summary>
    /// Registers a named <see cref="IAuthorizationPolicy"/> (resolved from DI, so it may inject services),
    /// bound to <paramref name="name"/>. Usually emitted by the generator from <c>[AuthorizationPolicy("name")]</c>.
    /// </summary>
    public static IServiceCollection AddElarionAuthorizationPolicy<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TPolicy>(
        this IServiceCollection services, string name)
        where TPolicy : class, IAuthorizationPolicy {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(name);

        services.TryAddScoped<TPolicy>();
        services.AddScoped(sp => new NamedAuthorizationPolicy(name, sp.GetRequiredService<TPolicy>()));
        return services;
    }

    /// <summary>
    /// Registers a named <see cref="IAuthorizationPolicy"/> whose name is read from its
    /// <see cref="AuthorizationPolicyAttribute"/>. Convenience for manual registration of an attributed policy.
    /// </summary>
    public static IServiceCollection AddElarionAuthorizationPolicy<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TPolicy>(
        this IServiceCollection services)
        where TPolicy : class, IAuthorizationPolicy {
        ArgumentNullException.ThrowIfNull(services);

        var attribute = typeof(TPolicy).GetCustomAttribute<AuthorizationPolicyAttribute>(false)
                        ?? throw new InvalidOperationException(
                            $"'{typeof(TPolicy)}' has no [AuthorizationPolicy] attribute; pass the policy name explicitly.");
        return services.AddElarionAuthorizationPolicy<TPolicy>(attribute.Name);
    }

    /// <summary>Registers a named authorization policy from an inline delegate, for simple checks.</summary>
    public static IServiceCollection AddElarionAuthorizationPolicy(
        this IServiceCollection services,
        string name,
        Func<AuthorizationContext, CancellationToken, ValueTask<bool>> evaluate) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(evaluate);

        services.AddScoped(_ => new NamedAuthorizationPolicy(name, new DelegateAuthorizationPolicy(evaluate)));
        return services;
    }
}

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
        return services;
    }

    /// <summary>Registers a named <see cref="IAuthorizationPolicy"/> resolved from DI (may inject services).</summary>
    public static IServiceCollection AddElarionAuthorizationPolicy<TPolicy>(this IServiceCollection services)
        where TPolicy : class, IAuthorizationPolicy {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IAuthorizationPolicy, TPolicy>();
        return services;
    }

    /// <summary>Registers a named authorization policy from an inline delegate, for simple checks.</summary>
    public static IServiceCollection AddElarionAuthorizationPolicy(
        this IServiceCollection services,
        string name,
        Func<AuthorizationContext, CancellationToken, ValueTask<bool>> evaluate) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(evaluate);

        services.AddSingleton<IAuthorizationPolicy>(new DelegateAuthorizationPolicy(name, evaluate));
        return services;
    }
}

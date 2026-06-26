using Elarion.Abstractions.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.AspNetCore.Authorization;

/// <summary>
/// Opt-in bridge from ASP.NET Core authorization policies to Elarion's transport-neutral
/// <see cref="IAuthorizationPolicy"/>. Use only when reusing existing ASP.NET policies; the recommended
/// path is a native <see cref="IAuthorizationPolicy"/> (see the core <c>AddElarionAuthorizationPolicy</c>).
/// </summary>
public static class AspNetAuthorizationServiceCollectionExtensions {
    /// <summary>
    /// Registers an Elarion <see cref="IAuthorizationPolicy"/> named <paramref name="policyName"/> that
    /// delegates to the ASP.NET policy of the same name (defined via <c>AddAuthorization</c>). Requires the
    /// host to have registered ASP.NET authorization and the named policy.
    /// </summary>
    public static IServiceCollection AddAspNetAuthorizationPolicyBridge(
        this IServiceCollection services, string policyName) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(policyName);

        services.AddHttpContextAccessor();
        services.AddScoped<IAuthorizationPolicy>(sp => new AspNetAuthorizationPolicyBridge(
            policyName,
            sp.GetRequiredService<IAuthorizationService>(),
            sp.GetRequiredService<IHttpContextAccessor>()));
        return services;
    }
}

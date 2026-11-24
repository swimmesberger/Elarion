using Microsoft.Extensions.DependencyInjection;

namespace Elarion.Blobs;

/// <summary>Registers named <see cref="UploadPolicy"/> instances for the upload endpoints.</summary>
public static class UploadPolicyServiceCollectionExtensions {
    /// <summary>
    /// Registers the upload policy <paramref name="name"/>, or configures it again when it is registered already (a host
    /// adjusting a module's policy). Both upload endpoints — the direct one and tus — resolve it when a client names it.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="name">
    /// The name clients send (<c>?policy=</c>, or the tus metadata key <c>policy</c>): visible ASCII without spaces.
    /// </param>
    /// <param name="configure">Sets the policy's limits and check.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or contains a space, a control or a
    /// non-ASCII character.</exception>
    public static IServiceCollection AddElarionUploadPolicy(
        this IServiceCollection services, string name, Action<UploadPolicy> configure) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(configure);
        foreach (var c in name)
            if (c is < '!' or > '~')
                throw new ArgumentException(
                    $"An upload policy name is visible ASCII without spaces; '{name}' is not.", nameof(name));

        UploadPolicies? policies = null;
        foreach (var descriptor in services)
            if (descriptor.ServiceType == typeof(UploadPolicies) && descriptor.ImplementationInstance is UploadPolicies existing)
                policies = existing;

        if (policies is null) {
            policies = new UploadPolicies();
            services.AddSingleton(policies);
        }

        configure(policies.GetOrAdd(name));
        return services;
    }
}

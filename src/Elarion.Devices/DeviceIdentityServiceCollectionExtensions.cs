using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.Devices;

/// <summary>Registers the device identity and provisioning services (ADR-0054).</summary>
public static class DeviceIdentityServiceCollectionExtensions {
    /// <summary>
    /// Registers <see cref="IDevicePairingService"/> and <see cref="HmacChallengeVerifier"/>.
    /// Deliberately registers <b>no stores</b>: device keys are durable identity, so a silent
    /// in-memory default would be a footgun — add
    /// <c>AddElarionDeviceIdentityEntityFrameworkCore&lt;TDbContext&gt;()</c> (production) or
    /// <see cref="AddElarionInMemoryDeviceIdentityStores"/> (tests/dev) explicitly.
    /// Safe to call repeatedly; later <paramref name="configure"/> delegates compose onto the same
    /// options instance.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional provisioning defaults (code length/alphabet/TTL, key size).</param>
    public static IServiceCollection AddElarionDeviceIdentity(
        this IServiceCollection services,
        Action<DeviceProvisioningOptions>? configure = null) {
        ArgumentNullException.ThrowIfNull(services);

        // Compose repeat calls onto the one instance this method registered. Keyed descriptors are
        // skipped (reading ImplementationInstance on them throws); a host-registered non-instance
        // descriptor fails loud instead of being silently shadowed by a fresh default.
        var descriptor = services.LastOrDefault(candidate =>
            candidate.ServiceType == typeof(DeviceProvisioningOptions) && !candidate.IsKeyedService);
        if (descriptor is not null && descriptor.ImplementationInstance is not DeviceProvisioningOptions)
            throw new InvalidOperationException(
                "DeviceProvisioningOptions is already registered with a factory or implementation type. "
                + "Configure provisioning via AddElarionDeviceIdentity(options => …) instead of registering the options yourself.");

        var existing = descriptor?.ImplementationInstance as DeviceProvisioningOptions;
        var options = existing ?? new DeviceProvisioningOptions();
        configure?.Invoke(options);
        options.Validate();
        if (existing is null) services.AddSingleton(options);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IDevicePairingService, DevicePairingService>();
        services.TryAddSingleton<HmacChallengeVerifier>();
        return services;
    }

    /// <summary>
    /// Registers the in-memory key and pairing-code stores — tests and single-node development
    /// only (keys vanish on restart).
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddElarionInMemoryDeviceIdentityStores(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);
        services.RemoveAll<IDeviceKeyStore>();
        services.AddSingleton<IDeviceKeyStore, InMemoryDeviceKeyStore>();
        services.RemoveAll<IPairingCodeStore>();
        services.AddSingleton<IPairingCodeStore, InMemoryPairingCodeStore>();
        return services;
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.Devices.EntityFrameworkCore;

/// <summary>Registers the EF-backed device identity stores (ADR-0054).</summary>
public static class DeviceIdentityEntityFrameworkCoreServiceCollectionExtensions {
    /// <summary>
    /// Registers the full device identity chain — <see cref="IDevicePairingService"/>,
    /// <see cref="HmacChallengeVerifier"/>, and the durable
    /// <see cref="IDeviceKeyStore"/>/<see cref="IPairingCodeStore"/> over
    /// <typeparamref name="TDbContext"/>. The context must map the device identity tables —
    /// annotate it with <c>[GenerateElarionDeviceIdentity]</c> or call
    /// <c>modelBuilder.UseElarionDeviceIdentity()</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional provisioning defaults (code length/alphabet/TTL, key size).</param>
    public static IServiceCollection AddElarionDeviceIdentityEntityFrameworkCore<TDbContext>(
        this IServiceCollection services,
        Action<DeviceProvisioningOptions>? configure = null)
        where TDbContext : DbContext {
        ArgumentNullException.ThrowIfNull(services);
        services.AddElarionDeviceIdentity(configure);
        services.RemoveAll<IDeviceKeyStore>();
        services.AddSingleton<IDeviceKeyStore, EfCoreDeviceKeyStore<TDbContext>>();
        services.RemoveAll<IPairingCodeStore>();
        services.AddSingleton<IPairingCodeStore, EfCoreDevicePairingCodeStore<TDbContext>>();
        return services;
    }
}

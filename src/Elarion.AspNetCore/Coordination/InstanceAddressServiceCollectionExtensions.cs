using System.Net.NetworkInformation;
using System.Net.Sockets;
using Elarion.Abstractions.Coordination;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.AspNetCore;

/// <summary>
/// Registers this instance's advertised address (ADR-0050): what a role lease publishes on its row so
/// non-holders can reach the holder (e.g. through <c>UseElarionRoleHolderProxy</c>).
/// </summary>
public static class InstanceAddressServiceCollectionExtensions {
    /// <summary>
    /// Registers the <see cref="IInstanceAddressProvider"/>. With <paramref name="advertisedAddress"/>
    /// set, that exact base address is advertised (use behind NAT/proxies or for HTTPS between
    /// instances); otherwise the address is auto-detected from the server's bound endpoints, with
    /// wildcard hosts (<c>0.0.0.0</c>, <c>[::]</c>, <c>+</c>, <c>*</c>) replaced by the machine's
    /// first non-loopback IPv4 address — the flat-network happy path.
    /// </summary>
    public static IServiceCollection AddElarionInstanceAddress(
        this IServiceCollection services,
        string? advertisedAddress = null) {
        ArgumentNullException.ThrowIfNull(services);
        if (advertisedAddress is not null) {
            services.TryAddSingleton<IInstanceAddressProvider>(
                new FixedInstanceAddressProvider(advertisedAddress.TrimEnd('/')));
        }
        else {
            services.TryAddSingleton<IInstanceAddressProvider, ServerAddressInstanceAddressProvider>();
        }

        return services;
    }

    private sealed class FixedInstanceAddressProvider(string address) : IInstanceAddressProvider {
        public string? GetInstanceAddress() => address;
    }
}

/// <summary>
/// Best-effort auto-detection of the instance address from the server's bound endpoints. Consulted at
/// lease-heartbeat cadence (never on a request path); returns <see langword="null"/> until the server
/// has bound its addresses, so the lease row simply fills in on a later renewal.
/// </summary>
internal sealed class ServerAddressInstanceAddressProvider(IServer server) : IInstanceAddressProvider {
    private volatile string? _resolved;

    public string? GetInstanceAddress() {
        if (_resolved is { } cached) {
            return cached;
        }

        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
        if (addresses is null || addresses.Count == 0) {
            return null;
        }

        // Prefer plain http for instance-to-instance traffic on a flat network; the first address is
        // the fallback. Explicit configuration (AddElarionInstanceAddress("…")) overrides all of this.
        var raw = addresses.FirstOrDefault(static a => a.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                  ?? addresses.First();
        // Wildcard binding hosts are not valid URI hosts; normalize before parsing.
        raw = raw.Replace("://+", "://0.0.0.0", StringComparison.Ordinal)
            .Replace("://*", "://0.0.0.0", StringComparison.Ordinal);
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) {
            return null;
        }

        var host = uri.Host;
        if (host is "0.0.0.0" or "::" or "[::]") {
            host = ResolveLocalIPv4();
            if (host is null) {
                return null;
            }
        }

        var resolved = new UriBuilder(uri.Scheme, host, uri.Port).Uri.GetLeftPart(UriPartial.Authority);
        _resolved = resolved;
        return resolved;
    }

    private static string? ResolveLocalIPv4() {
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces()) {
            if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback) {
                continue;
            }

            foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses) {
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork) {
                    return unicast.Address.ToString();
                }
            }
        }

        return null;
    }
}

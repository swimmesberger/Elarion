using Elarion.Abstractions.Coordination;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Elarion.AspNetCore;

/// <summary>Installs the role-holder proxy (ADR-0050).</summary>
public static class RoleHolderProxyApplicationBuilderExtensions {
    /// <summary>
    /// Serves the given path prefixes on every instance by transparently forwarding them to the
    /// current holder of <paramref name="role"/> when this instance is not it — the in-app version of
    /// the ingress rule you will eventually write (the prefix list maps 1:1 onto it; delete this call
    /// when the load balancer takes over). Place it <b>before</b> routing/auth so nothing executes
    /// locally on the proxy path.
    /// </summary>
    /// <remarks>
    /// Opt-in and self-deactivating: when no role lease is registered under <paramref name="role"/>
    /// (single-instance mode, local dev), no middleware is installed at all — the pipeline is
    /// byte-identical to not calling this. On the holder the per-request cost is one lock-free
    /// <c>IsHeld</c> check. Failure modes are bounded and loud: an unreachable or unknown holder, or a
    /// request that arrives already-forwarded while the lease is mid-failover, answers
    /// <c>503 + Retry-After</c> — the proxy never retries, queues, or forwards more than one hop.
    /// Every instance must advertise an address (<c>AddElarionInstanceAddress()</c> or
    /// <c>RoleLeaseOptions.AdvertisedAddress</c>).
    /// </remarks>
    public static IApplicationBuilder UseElarionRoleHolderProxy(
        this IApplicationBuilder app,
        string role,
        params string[] pathPrefixes) {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        if (pathPrefixes.Length == 0) {
            throw new ArgumentException(
                "UseElarionRoleHolderProxy needs at least one path prefix (e.g. \"/quotes\") — the same "
                + "prefixes an ingress rule would route to the role holder.", nameof(pathPrefixes));
        }

        var prefixes = pathPrefixes.Select(static prefix => new PathString(prefix)).ToArray();
        var logger = app.ApplicationServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Elarion.AspNetCore.RoleHolderProxy");

        var lease = app.ApplicationServices.GetKeyedService<IRoleLease>(role);
        if (lease is null) {
            // Single-instance mode: no lease, no proxy, no middleware — the pipeline stays untouched.
            logger.LogInformation(
                "No role lease is registered for '{Role}'; the role-holder proxy is inactive "
                + "(single-instance mode).", role);
            return app;
        }

        var client = new HttpMessageInvoker(new SocketsHttpHandler {
            UseCookies = false,
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        });
        var middleware = new RoleHolderProxyMiddleware(lease, prefixes, client, logger);
        return app.Use(next => context => middleware.InvokeAsync(context, next));
    }
}

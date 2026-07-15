using Elarion.Abstractions.Coordination;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Elarion.AspNetCore;

/// <summary>Installs the role-holder proxy (ADR-0050).</summary>
public static class RoleHolderProxyApplicationBuilderExtensions {
    /// <summary>
    /// Routes matching requests to the holder of the virtual partition selected by
    /// <paramref name="affinityKey"/>. The key resolver runs before routing, so it should read path,
    /// query, or headers directly rather than route values.
    /// </summary>
    public static IApplicationBuilder UseElarionPartitionHolderProxy(
        this IApplicationBuilder app,
        string partition,
        Func<HttpContext, string?> affinityKey,
        params string[] pathPrefixes) =>
        UseElarionPartitionHolderProxyCore(app, partition, null, affinityKey, pathPrefixes);

    /// <summary>
    /// Routes matching requests to the holder of the virtual partition selected by the scoped
    /// <paramref name="affinityKey"/>. Use the same stable scope as the subsystem being addressed;
    /// actor routes use the actor's generated logical name so ingress and actor activation resolve
    /// the same virtual shard.
    /// </summary>
    public static IApplicationBuilder UseElarionPartitionHolderProxy(
        this IApplicationBuilder app,
        string partition,
        string affinityScope,
        Func<HttpContext, string?> affinityKey,
        params string[] pathPrefixes) {
        ArgumentException.ThrowIfNullOrWhiteSpace(affinityScope);
        return UseElarionPartitionHolderProxyCore(
            app,
            partition,
            affinityScope,
            affinityKey,
            pathPrefixes);
    }

    private static IApplicationBuilder UseElarionPartitionHolderProxyCore(
        IApplicationBuilder app,
        string partition,
        string? affinityScope,
        Func<HttpContext, string?> affinityKey,
        string[] pathPrefixes) {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        ArgumentNullException.ThrowIfNull(affinityKey);
        ValidatePrefixes(pathPrefixes, nameof(pathPrefixes));

        var rolePartition = app.ApplicationServices.GetKeyedService<IRolePartition>(partition);
        var logger = CreateLogger(app);
        if (rolePartition is null) {
            logger.LogInformation(
                "No role partition is registered for '{Partition}'; the partition-holder proxy is inactive.",
                partition);
            return app;
        }

        var middleware = new RoleHolderProxyMiddleware(
            context => {
                var key = affinityKey(context);
                if (key is null) {
                    return null;
                }

                var target = affinityScope is null
                    ? rolePartition.Resolve(key)
                    : rolePartition.Resolve(affinityScope, key);
                return new RoleHolderTarget(
                    target.Role,
                    target.IsHeld,
                    target.CurrentHolderAddress);
            },
            ToPrefixes(pathPrefixes),
            CreateClient(),
            logger);
        return app.Use(next => context => middleware.InvokeAsync(context, next));
    }

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
    /// byte-identical to not calling this. Matching requests on the holder pay one lock-free
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
        ValidatePrefixes(pathPrefixes, nameof(pathPrefixes));

        var prefixes = ToPrefixes(pathPrefixes);
        var logger = CreateLogger(app);

        var lease = app.ApplicationServices.GetKeyedService<IRoleLease>(role);
        if (lease is null) {
            // Single-instance mode: no lease, no proxy, no middleware — the pipeline stays untouched.
            logger.LogInformation(
                "No role lease is registered for '{Role}'; the role-holder proxy is inactive "
                + "(single-instance mode).", role);
            return app;
        }

        var middleware = new RoleHolderProxyMiddleware(lease, prefixes, CreateClient(), logger);
        return app.Use(next => context => middleware.InvokeAsync(context, next));
    }

    private static void ValidatePrefixes(string[] pathPrefixes, string parameterName) {
        if (pathPrefixes.Length == 0) {
            throw new ArgumentException(
                "A role-holder proxy needs at least one path prefix (e.g. \"/quotes\").",
                parameterName);
        }
    }

    private static PathString[] ToPrefixes(string[] pathPrefixes) =>
        pathPrefixes.Select(static prefix => new PathString(prefix)).ToArray();

    private static ILogger CreateLogger(IApplicationBuilder app) =>
        app.ApplicationServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Elarion.AspNetCore.RoleHolderProxy");

    private static HttpMessageInvoker CreateClient() =>
        new(new SocketsHttpHandler {
            UseCookies = false,
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            // The default ConnectTimeout is infinite; a crashed holder (dropped SYNs) must surface as the
            // documented 503 + Retry-After, not a hang. Time-to-response-headers is bounded in the middleware.
            ConnectTimeout = RoleHolderProxyMiddleware.DefaultConnectTimeout
        });
}

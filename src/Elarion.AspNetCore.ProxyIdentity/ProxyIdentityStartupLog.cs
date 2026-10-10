using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Elarion.AspNetCore.ProxyIdentity;

/// <summary>Says once at startup what this instance accepts, and warns about the settings that weaken it.</summary>
internal sealed class ProxyIdentityStartupLog(IOptions<ProxyIdentityOptions> options, ILogger<ProxyIdentityStartupLog> logger)
    : IHostedService {
    public Task StartAsync(CancellationToken cancellationToken) {
        var proxy = options.Value;
        if (!proxy.Enabled) {
            logger.LogWarning(
                "Proxy identity is OFF: every request runs as the Development stand-in {Email} (switch with the {Header} "
                + "header or the {Cookie} cookie). Development only.",
                proxy.Development.Email, proxy.Development.UserHeader ?? "-", proxy.Development.UserCookie ?? "-");
            return Task.CompletedTask;
        }
        if (proxy.AllowAnyAudience) {
            logger.LogWarning(
                "Proxy token audience validation is DISABLED: any token issued by {Issuer} is accepted, for any of its "
                + "applications. Set {Section}:Audiences to scope it to this one.",
                proxy.Issuer, ProxyIdentityDefaults.SectionName);
        }
        var (address, isJwks) = ProxySigningKeys.Source(proxy);
        logger.LogInformation(
            "Validating proxy tokens from issuer {Issuer} (keys: {KeySource} {Address}; header: {Header}; cookie: {Cookie}; "
            + "e-mail trust: {EmailTrust}).",
            proxy.Issuer, isJwks ? "JWKS" : "discovery", address, proxy.TokenHeader ?? "-", proxy.TokenCookie ?? "-",
            proxy.EmailTrust);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) {
        return Task.CompletedTask;
    }
}

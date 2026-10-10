using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.Configuration;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Elarion.AspNetCore.ProxyIdentity;

/// <summary>
/// Builds the IdentityModel <see cref="ConfigurationManager{T}"/> JwtBearer validates proxy tokens with. All caching,
/// refreshing and concurrency is the library's: the first fetch is awaited by every concurrent caller (one request on
/// the wire), later refreshes run in the background while the current keys keep serving, a token naming an unknown key
/// requests a refresh (at most once per <see cref="ProxyIdentityOptions.RefreshInterval"/>), and a failed or refused
/// refresh keeps the last accepted keys. The last-known-good cache — which keeps accepting a configuration a refresh
/// replaced — is off unless <see cref="ProxyIdentityOptions.LastKnownGoodLifetime"/> is positive.
/// </summary>
internal static class ProxySigningKeys {
    /// <summary>The OpenID Connect discovery document of <paramref name="issuer"/> (Discovery §4: the path is appended).</summary>
    public static string DiscoveryAddress(string issuer) {
        return issuer.TrimEnd('/') + "/.well-known/openid-configuration";
    }

    /// <summary>Where the keys come from: the JWKS URL, or the discovery document (explicit or derived from the issuer).</summary>
    public static (string Address, bool IsJwks) Source(ProxyIdentityOptions options) {
        if (!string.IsNullOrWhiteSpace(options.JwksUrl)) {
            return (options.JwksUrl, true);
        }
        return (string.IsNullOrWhiteSpace(options.MetadataAddress) ? DiscoveryAddress(options.Issuer!) : options.MetadataAddress, false);
    }

    public static ConfigurationManager<OpenIdConnectConfiguration> Create(ProxyIdentityOptions options, HttpClient httpClient) {
        var (address, isJwks) = Source(options);
        IConfigurationRetriever<OpenIdConnectConfiguration> retriever = isJwks
            ? new JwksConfigurationRetriever(options.Issuer!)
            : new OpenIdConnectConfigurationRetriever();
        var documents = new HttpDocumentRetriever(httpClient) { RequireHttps = options.RequireHttpsMetadata };
        return new ConfigurationManager<OpenIdConnectConfiguration>(
            address, retriever, documents, new IssuerBoundConfigurationValidator(options.Issuer!)) {
            AutomaticRefreshInterval = options.AutomaticRefreshInterval,
            RefreshInterval = options.RefreshInterval,
            UseLastKnownGoodConfiguration = options.LastKnownGoodLifetime > TimeSpan.Zero,
            LastKnownGoodLifetime = options.LastKnownGoodLifetime > TimeSpan.Zero
                ? options.LastKnownGoodLifetime
                : BaseConfigurationManager.DefaultLastKnownGoodConfigurationLifetime,
        };
    }
}

/// <summary>
/// Reads a bare JSON Web Key Set as an OpenID Connect configuration whose issuer is the configured one, so the JWKS-URL
/// mode and the discovery mode share one <see cref="ConfigurationManager{T}"/> and one set of refresh rules.
/// </summary>
internal sealed class JwksConfigurationRetriever(string issuer) : IConfigurationRetriever<OpenIdConnectConfiguration> {
    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(
        string address, IDocumentRetriever retriever, CancellationToken cancel) {
        var document = await retriever.GetDocumentAsync(address, cancel).ConfigureAwait(false);
        var keySet = new JsonWebKeySet(document);
        var configuration = new OpenIdConnectConfiguration { Issuer = issuer, JsonWebKeySet = keySet, JwksUri = address };
        foreach (var key in keySet.GetSigningKeys()) {
            configuration.SigningKeys.Add(key);
        }
        return configuration;
    }
}

/// <summary>
/// Accepts a fetched configuration only when it names exactly the configured issuer (OpenID Connect Discovery §4.3) and
/// carries at least one signing key. A refused fetch is treated by the <see cref="ConfigurationManager{T}"/> like a
/// failed one: the first fetch fails closed, a later one keeps the configuration accepted before. The issuer check
/// also matters for validation: IdentityModel accepts a token whose <c>iss</c> equals the configuration's issuer, so a
/// discovery document naming a different issuer must never become current.
/// </summary>
internal sealed class IssuerBoundConfigurationValidator(string issuer) : IConfigurationValidator<OpenIdConnectConfiguration> {
    public ConfigurationValidationResult Validate(OpenIdConnectConfiguration configuration) {
        if (!string.Equals(configuration.Issuer, issuer, StringComparison.Ordinal)) {
            return new ConfigurationValidationResult {
                Succeeded = false,
                ErrorMessage = $"The configuration names the issuer '{configuration.Issuer}', not the configured '{issuer}'.",
            };
        }
        if (configuration.SigningKeys.Count == 0) {
            return new ConfigurationValidationResult {
                Succeeded = false,
                ErrorMessage = "The configuration holds no signing key.",
            };
        }
        return new ConfigurationValidationResult { Succeeded = true };
    }
}

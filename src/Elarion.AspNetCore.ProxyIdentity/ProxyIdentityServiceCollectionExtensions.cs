using System.Security.Claims;
using Elarion.AspNetCore.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Elarion.AspNetCore.ProxyIdentity;

/// <summary>
/// Host wiring for an application behind an authenticating reverse proxy (Cloudflare Access, Google IAP, oauth2-proxy,
/// an OpenID Connect gateway): the proxy signs the person in and forwards a signed token; this validates it.
/// </summary>
public static class ProxyIdentityServiceCollectionExtensions {
    private const string BearerPrefix = "Bearer ";

    /// <summary>
    /// Registers the proxy identity as the default authentication scheme: a JwtBearer scheme
    /// (<see cref="ProxyIdentityDefaults.AuthenticationScheme"/>) that reads the token only from the configured header or
    /// cookie, or — while <see cref="ProxyIdentityOptions.Enabled"/> is false in Development — the stand-in scheme
    /// (<see cref="ProxyIdentityDefaults.DevelopmentScheme"/>). It also maps <c>ICurrentUser</c> to the configured claim
    /// names (subject, e-mail, roles) and logs once at startup what this instance accepts.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration; options bind from <see cref="ProxyIdentityDefaults.SectionName"/>.</param>
    /// <param name="environment">The host environment; the stand-in identity is allowed only in Development.</param>
    /// <param name="configure">Optional changes applied after binding, before validation.</param>
    /// <returns>The authentication builder, so the host can add further, explicitly named schemes.</returns>
    /// <exception cref="InvalidOperationException">
    /// The options are incomplete or contradictory, or the integration is disabled outside Development.
    /// </exception>
    /// <remarks>
    /// <c>ICurrentUser</c> registration is first-wins: call this before any other <c>AddElarionCurrentUser</c> so the
    /// proxy's claim names apply. Pair it with <c>app.UseAuthentication()</c> and <c>app.UseElarionCurrentUser()</c>.
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddElarionProxyIdentity(builder.Configuration, builder.Environment);
    /// builder.Services.AddElarionAuthorization();
    ///
    /// var app = builder.Build();
    /// app.UseAuthentication();
    /// app.UseElarionCurrentUser();
    /// app.UseAuthorization();
    /// </code>
    /// </example>
    public static AuthenticationBuilder AddElarionProxyIdentity(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        Action<ProxyIdentityOptions>? configure = null) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var options = new ProxyIdentityOptions();
        configuration.GetSection(ProxyIdentityDefaults.SectionName).Bind(options);
        configure?.Invoke(options);
        if (Validate(options, environment) is { } problem) {
            throw new InvalidOperationException(problem);
        }

        // The validated instance is the one every part reads; later IConfiguration changes do not re-open validation.
        services.TryAddSingleton(Options.Create(options));
        services.AddHostedService<ProxyIdentityStartupLog>();
        services.AddElarionCurrentUser(currentUser => {
            currentUser.UserIdClaimType = options.Claims.Subject;
            if (!string.IsNullOrEmpty(options.Claims.Email)) {
                currentUser.EmailClaimType = options.Claims.Email;
            }
            if (!string.IsNullOrEmpty(options.Claims.Roles)) {
                currentUser.RoleClaimType = options.Claims.Roles;
            }
        });

        if (!options.Enabled) {
            return services.AddAuthentication(ProxyIdentityDefaults.DevelopmentScheme)
                .AddScheme<AuthenticationSchemeOptions, DevelopmentIdentityHandler>(ProxyIdentityDefaults.DevelopmentScheme, null);
        }

        services.AddHttpClient(ProxyIdentityDefaults.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10));
        var builder = services.AddAuthentication(ProxyIdentityDefaults.AuthenticationScheme)
            .AddJwtBearer(ProxyIdentityDefaults.AuthenticationScheme, jwt => ConfigureJwtBearer(jwt, options));
        // Created lazily, once per scheme options instance: the HTTP client factory does not exist at registration time.
        services.AddOptions<JwtBearerOptions>(ProxyIdentityDefaults.AuthenticationScheme)
            .Configure<IHttpClientFactory>((jwt, clients) =>
                jwt.ConfigurationManager = ProxySigningKeys.Create(options, clients.CreateClient(ProxyIdentityDefaults.HttpClientName)));
        return builder;
    }

    private static void ConfigureJwtBearer(JwtBearerOptions jwt, ProxyIdentityOptions options) {
        var names = options.Claims;
        // Keep the token's claim names ("sub", "email", "roles") instead of the WS-* URIs.
        jwt.MapInboundClaims = false;
        jwt.RequireHttpsMetadata = options.RequireHttpsMetadata;
        jwt.TokenValidationParameters = new TokenValidationParameters {
            ValidateIssuer = true,
            ValidIssuer = options.Issuer,
            ValidateAudience = !options.AllowAnyAudience,
            ValidAudiences = options.EffectiveAudiences(),
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = options.ClockSkew,
            NameClaimType = string.IsNullOrEmpty(names.Name) ? names.Subject : names.Name,
            RoleClaimType = string.IsNullOrEmpty(names.Roles) ? ClaimTypes.Role : names.Roles,
        };
        jwt.Events = new JwtBearerEvents {
            OnMessageReceived = context => {
                // Only the configured header and cookie carry the proxy's token. Without a token here JwtBearer would
                // fall back to "Authorization: Bearer" on its own — a location the proxy may not control.
                if (ReadToken(context.Request, options) is { } token) {
                    context.Token = token;
                } else {
                    context.NoResult();
                }
                return Task.CompletedTask;
            },
        };
    }

    /// <summary>The proxy's token on a request: the configured header (a <c>Bearer </c> prefix stripped), else the cookie.</summary>
    internal static string? ReadToken(HttpRequest request, ProxyIdentityOptions options) {
        if (!string.IsNullOrWhiteSpace(options.TokenHeader)) {
            var header = request.Headers[options.TokenHeader].ToString().Trim();
            if (header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)) {
                header = header[BearerPrefix.Length..].Trim();
            }
            if (header.Length > 0) {
                return header;
            }
        }
        return !string.IsNullOrWhiteSpace(options.TokenCookie)
            && request.Cookies.TryGetValue(options.TokenCookie, out var cookie) && !string.IsNullOrWhiteSpace(cookie)
                ? cookie.Trim()
                : null;
    }

    /// <summary>The reason <paramref name="options"/> cannot run in <paramref name="environment"/>, or null when it can.</summary>
    internal static string? Validate(ProxyIdentityOptions options, IHostEnvironment environment) {
        const string section = ProxyIdentityDefaults.SectionName;
        if (!options.Enabled) {
            if (!environment.IsDevelopment()) {
                return $"{section}:Enabled is false in the '{environment.EnvironmentName}' environment. Configure the "
                    + $"authenticating proxy ({section}:Issuer, a key source and {section}:Audiences) and set "
                    + $"{section}:Enabled=true, or run in Development to use the stand-in identity.";
            }
            return string.IsNullOrWhiteSpace(options.Development.Subject) || string.IsNullOrWhiteSpace(options.Development.Email)
                ? $"{section}:Development:Subject and :Email must not be empty."
                : null;
        }

        if (string.IsNullOrWhiteSpace(options.Issuer)) {
            return $"{section}:Enabled is true but {section}:Issuer is empty.";
        }
        if (!string.IsNullOrWhiteSpace(options.JwksUrl) && !string.IsNullOrWhiteSpace(options.MetadataAddress)) {
            return $"{section}:JwksUrl and {section}:MetadataAddress are both set; configure exactly one key source.";
        }
        var (address, isJwks) = ProxySigningKeys.Source(options);
        var source = isJwks ? $"{section}:JwksUrl" : string.IsNullOrWhiteSpace(options.MetadataAddress)
            ? $"the discovery address derived from {section}:Issuer ({address})"
            : $"{section}:MetadataAddress";
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)) {
            return $"{source} is not an absolute http(s) URL. Set {section}:JwksUrl or {section}:MetadataAddress.";
        }
        if (options.RequireHttpsMetadata && uri.Scheme != Uri.UriSchemeHttps) {
            return $"{source} is not an https URL. Use https, or set {section}:RequireHttpsMetadata=false for a local test issuer.";
        }
        if (string.IsNullOrWhiteSpace(options.TokenHeader) && string.IsNullOrWhiteSpace(options.TokenCookie)) {
            return $"{section}: neither TokenHeader nor TokenCookie is set, so no token could ever be read.";
        }
        if (string.IsNullOrWhiteSpace(options.Claims.Subject)) {
            return $"{section}:Claims:Subject is empty.";
        }
        var audiences = options.EffectiveAudiences();
        if (options.AllowAnyAudience && audiences.Count > 0) {
            return $"{section}:AllowAnyAudience is true and {section}:Audiences is set; configure one or the other.";
        }
        if (!options.AllowAnyAudience && audiences.Count == 0) {
            return $"{section}:Enabled is true but {section}:Audiences is empty. Set it to this application's audience; "
                + "without it a token the same issuer minted for any other application would be accepted here. If this "
                + $"really is the issuer's only application, set {section}:AllowAnyAudience=true to accept that explicitly.";
        }
        if (options.ClockSkew < TimeSpan.Zero) {
            return $"{section}:ClockSkew must not be negative.";
        }
        if (options.AutomaticRefreshInterval < TimeSpan.FromMinutes(5)) {
            return $"{section}:AutomaticRefreshInterval must be at least five minutes (the IdentityModel minimum).";
        }
        if (options.LastKnownGoodLifetime < TimeSpan.Zero) {
            return $"{section}:LastKnownGoodLifetime must not be negative.";
        }
        if (options.RefreshInterval < TimeSpan.FromSeconds(1)) {
            return $"{section}:RefreshInterval must be at least one second (the IdentityModel minimum).";
        }
        return null;
    }
}

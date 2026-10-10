using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Elarion.Abstractions.Identity;
using Elarion.AspNetCore.Identity;
using Elarion.AspNetCore.ProxyIdentity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Elarion.Tests.ProxyIdentity;

/// <summary>An RSA signing key with a key id, publishable as a JWK.</summary>
internal sealed class TestSigningKey {
    private TestSigningKey(string kid) {
        Key = new RsaSecurityKey(RSA.Create(2048)) { KeyId = kid };
    }

    public RsaSecurityKey Key { get; }

    public static TestSigningKey Create(string kid) {
        return new TestSigningKey(kid);
    }

    public static string Jwks(params TestSigningKey[] keys) {
        return $$"""{"keys":[{{string.Join(",", keys.Select(key => key.Jwk()))}}]}""";
    }

    private string Jwk() {
        var parameters = Key.Rsa.ExportParameters(includePrivateParameters: false);
        return $$"""{"kty":"RSA","use":"sig","alg":"RS256","kid":"{{Key.KeyId}}","n":"{{Base64UrlEncoder.Encode(parameters.Modulus)}}","e":"{{Base64UrlEncoder.Encode(parameters.Exponent)}}"}""";
    }

    /// <summary>A signed token; <paramref name="claims"/> values may be strings, booleans or string arrays.</summary>
    public string Mint(
        string issuer,
        string? audience,
        IDictionary<string, object>? claims = null,
        DateTime? expires = null) {
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = now.AddMinutes(-1),
            NotBefore = now.AddMinutes(-1),
            Expires = expires ?? now.AddMinutes(5),
            Claims = claims ?? new Dictionary<string, object> { ["sub"] = "user-1" },
            SigningCredentials = new SigningCredentials(Key, SecurityAlgorithms.RsaSha256),
        });
    }
}

/// <summary>
/// Stands in for the proxy's key endpoint (and discovery document): serves the configured documents, counts requests
/// per URL, can hold every response until released, and can simulate an outage.
/// </summary>
internal sealed class FakeKeyEndpoint : HttpMessageHandler {
    private readonly ConcurrentDictionary<string, int> _hits = new(StringComparer.Ordinal);
    private volatile TaskCompletionSource? _gate;

    public ConcurrentDictionary<string, string> Documents { get; } = new(StringComparer.Ordinal);

    public bool Outage { get; set; }

    public int Hits(string url) {
        return _hits.TryGetValue(url, out var hits) ? hits : 0;
    }

    public int TotalHits => _hits.Values.Sum();

    /// <summary>Holds every response until <see cref="Release"/>.</summary>
    public void Hold() {
        _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Release() {
        _gate?.TrySetResult();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
        var url = request.RequestUri!.AbsoluteUri;
        _hits.AddOrUpdate(url, 1, (_, hits) => hits + 1);
        if (_gate is { } gate) {
            await gate.Task.WaitAsync(cancellationToken);
        }
        if (Outage || !Documents.TryGetValue(url, out var document)) {
            return new HttpResponseMessage(Outage ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.NotFound);
        }
        return new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent(document, Encoding.UTF8, "application/json"),
        };
    }
}

/// <summary>A real Kestrel host with the proxy identity, answering <c>/whoami</c> with what the application sees.</summary>
internal sealed class ProxyIdentityTestHost(WebApplication app, HttpClient client) : IAsyncDisposable {
    public const string Issuer = "https://issuer.example";
    public const string JwksUrl = "https://issuer.example/keys";
    public const string Audience = "app-audience";
    public const string TokenHeader = "X-Proxy-Token";
    public const string TokenCookie = "proxy_token";

    public WebApplication App { get; } = app;

    public HttpClient Client { get; } = client;

    /// <summary>Settings of a valid JWKS-mode configuration; tests override single keys.</summary>
    public static Dictionary<string, string?> JwksSettings() {
        return new Dictionary<string, string?> {
            ["ProxyIdentity:Enabled"] = "true",
            ["ProxyIdentity:Issuer"] = Issuer,
            ["ProxyIdentity:JwksUrl"] = JwksUrl,
            ["ProxyIdentity:Audiences:0"] = Audience,
            ["ProxyIdentity:TokenHeader"] = TokenHeader,
            ["ProxyIdentity:TokenCookie"] = TokenCookie,
        };
    }

    public static async Task<ProxyIdentityTestHost> StartAsync(
        Dictionary<string, string?> settings,
        FakeKeyEndpoint? keys = null,
        string environment = "Production",
        Action<IServiceCollection>? services = null) {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddElarionProxyIdentity(builder.Configuration, builder.Environment);
        if (keys is not null) {
            builder.Services.AddHttpClient(ProxyIdentityDefaults.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => keys);
        }
        services?.Invoke(builder.Services);

        var app = builder.Build();
        app.UseAuthentication();
        app.UseElarionCurrentUser();
        app.MapGet("/whoami", (ICurrentUser user, HttpContext context, IOptions<ProxyIdentityOptions> options) => {
            if (!user.IsAuthenticated || ExternalIdentity.TryRead(context.User, options.Value) is not { } identity) {
                return Results.StatusCode(StatusCodes.Status401Unauthorized);
            }
            return Results.Text(string.Join("|",
                user.UserId, user.Email, string.Join(",", user.Roles), identity.Issuer, identity.EmailVerified,
                identity.Name, string.Join(",", identity.Groups), identity.IsDevelopmentIdentity));
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        var baseAddress = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new ProxyIdentityTestHost(app, new HttpClient { BaseAddress = new Uri(baseAddress) });
    }

    /// <summary>Calls <c>/whoami</c> with the given header and cookie values (null omits them).</summary>
    public async Task<WhoAmI> WhoAmIAsync(
        string? header = null,
        string? cookie = null,
        string headerName = TokenHeader,
        string cookieName = TokenCookie) {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/whoami");
        if (header is not null) {
            request.Headers.TryAddWithoutValidation(headerName, header);
        }
        if (cookie is not null) {
            request.Headers.Add("Cookie", $"{cookieName}={cookie}");
        }
        using var response = await Client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return new WhoAmI(response.StatusCode, body.Length == 0 ? [] : body.Split('|'));
    }

    public async ValueTask DisposeAsync() {
        Client.Dispose();
        await App.DisposeAsync();
    }
}

/// <summary>The <c>/whoami</c> answer: user id, e-mail, roles, issuer, e-mail verified, name, groups, development flag.</summary>
internal sealed record WhoAmI(HttpStatusCode Status, string[] Fields) {
    public string UserId => Fields[0];
    public string Email => Fields[1];
    public string Roles => Fields[2];
    public string Issuer => Fields[3];
    public bool EmailVerified => bool.Parse(Fields[4]);
    public string Name => Fields[5];
    public string Groups => Fields[6];
    public bool IsDevelopmentIdentity => bool.Parse(Fields[7]);
}

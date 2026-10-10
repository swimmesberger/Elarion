using AwesomeAssertions;
using Elarion.AspNetCore.ProxyIdentity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Elarion.Tests.ProxyIdentity;

/// <summary>
/// The startup contract: which configurations <c>AddElarionProxyIdentity</c> accepts, which it refuses with a message
/// naming the setting, and which scheme becomes the default.
/// </summary>
public sealed class ProxyIdentityConfigurationTests {
    public static TheoryData<string, string, string?> RefusedConfigurations() {
        // (description, key=value overrides on top of a valid JWKS configuration, expected message fragment)
        return new TheoryData<string, string, string?> {
            { "disabled outside Development", "ProxyIdentity:Enabled=false", "Enabled is false in the 'Production' environment" },
            { "no issuer", "ProxyIdentity:Issuer=", "Issuer is empty" },
            { "no audience", "ProxyIdentity:Audiences:0=", "Audiences is empty" },
            { "any audience together with audiences", "ProxyIdentity:AllowAnyAudience=true", "configure one or the other" },
            { "both key sources", "ProxyIdentity:MetadataAddress=https://issuer.example/.well-known/openid-configuration", "configure exactly one key source" },
            { "http JWKS URL", "ProxyIdentity:JwksUrl=http://issuer.example/keys", "is not an https URL" },
            { "relative JWKS URL", "ProxyIdentity:JwksUrl=/keys", "is not an absolute http(s) URL" },
            { "non-URL issuer without a key source", "ProxyIdentity:JwksUrl=;ProxyIdentity:Issuer=issuer", "discovery address derived from ProxyIdentity:Issuer" },
            { "no token location", "ProxyIdentity:TokenHeader=;ProxyIdentity:TokenCookie=", "neither TokenHeader nor TokenCookie" },
            { "no subject claim", "ProxyIdentity:Claims:Subject=", "Claims:Subject is empty" },
            { "refresh below the library minimum", "ProxyIdentity:RefreshInterval=00:00:00.5", "RefreshInterval must be at least one second" },
            { "automatic refresh below the library minimum", "ProxyIdentity:AutomaticRefreshInterval=00:01:00", "AutomaticRefreshInterval must be at least five minutes" },
        };
    }

    [Theory]
    [MemberData(nameof(RefusedConfigurations))]
    public void Refuses(string description, string overrides, string? expected) {
        var settings = ProxyIdentityTestHost.JwksSettings();
        foreach (var pair in overrides.Split(';')) {
            var (key, value) = (pair[..pair.IndexOf('=')], pair[(pair.IndexOf('=') + 1)..]);
            settings[key] = value;
        }

        var act = () => Register(settings, Environments.Production);

        act.Should().Throw<InvalidOperationException>(description).WithMessage($"*{expected}*");
    }

    public static TheoryData<string, string> AcceptedConfigurations() {
        return new TheoryData<string, string> {
            { "JWKS URL", "" },
            { "discovery derived from the issuer", "ProxyIdentity:JwksUrl=" },
            { "explicit discovery document", "ProxyIdentity:JwksUrl=;ProxyIdentity:MetadataAddress=https://login.example/tenant/.well-known/openid-configuration" },
            { "header only", "ProxyIdentity:TokenCookie=" },
            { "cookie only", "ProxyIdentity:TokenHeader=" },
            { "any audience", "ProxyIdentity:Audiences:0=;ProxyIdentity:AllowAnyAudience=true" },
            { "comma-separated audiences", "ProxyIdentity:Audiences:0=a, b" },
            { "local http issuer when https metadata is waived", "ProxyIdentity:JwksUrl=http://localhost:5000/keys;ProxyIdentity:RequireHttpsMetadata=false" },
        };
    }

    [Theory]
    [MemberData(nameof(AcceptedConfigurations))]
    public async Task Accepts_AndMakesTheProxySchemeTheDefault(string description, string overrides) {
        var settings = ProxyIdentityTestHost.JwksSettings();
        foreach (var pair in overrides.Split(';', StringSplitOptions.RemoveEmptyEntries)) {
            settings[pair[..pair.IndexOf('=')]] = pair[(pair.IndexOf('=') + 1)..];
        }

        await using var provider = Register(settings, Environments.Production);

        var scheme = await provider.GetRequiredService<IAuthenticationSchemeProvider>().GetDefaultAuthenticateSchemeAsync();
        scheme!.Name.Should().Be(ProxyIdentityDefaults.AuthenticationScheme, description);
    }

    [Fact]
    public async Task DisabledInDevelopment_SelectsTheStandInScheme() {
        await using var provider = Register(new Dictionary<string, string?>(), Environments.Development);

        var scheme = await provider.GetRequiredService<IAuthenticationSchemeProvider>().GetDefaultAuthenticateSchemeAsync();
        scheme!.Name.Should().Be(ProxyIdentityDefaults.DevelopmentScheme);
    }

    [Fact]
    public void Unconfigured_RefusesOutsideDevelopment() {
        var act = () => Register(new Dictionary<string, string?>(), "Staging");

        act.Should().Throw<InvalidOperationException>().WithMessage("*Enabled is false in the 'Staging' environment*");
    }

    [Fact]
    public async Task EnabledInDevelopment_ValidatesRealTokens() {
        await using var provider = Register(ProxyIdentityTestHost.JwksSettings(), Environments.Development);

        var scheme = await provider.GetRequiredService<IAuthenticationSchemeProvider>().GetDefaultAuthenticateSchemeAsync();
        scheme!.Name.Should().Be(ProxyIdentityDefaults.AuthenticationScheme);
    }

    [Fact]
    public void Configure_RunsAfterBindingAndBeforeValidation() {
        var settings = ProxyIdentityTestHost.JwksSettings();
        settings["ProxyIdentity:Audiences:0"] = "";
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var act = () => services.AddElarionProxyIdentity(
            configuration, new TestEnvironment(Environments.Production), options => options.Audiences.Add("set-in-code"));

        act.Should().NotThrow();
    }

    [Fact]
    public void Options_BindTheWholeSection() {
        var settings = ProxyIdentityTestHost.JwksSettings();
        settings["ProxyIdentity:Claims:Subject"] = "oid";
        settings["ProxyIdentity:Claims:Roles"] = "app_roles";
        settings["ProxyIdentity:EmailTrust"] = "Trusted";
        settings["ProxyIdentity:LogoutUrl"] = "/logout";
        settings["ProxyIdentity:ClockSkew"] = "00:00:10";
        settings["ProxyIdentity:Development:Users:0:Email"] = "second@example.com";

        using var provider = Register(settings, Environments.Production);

        var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ProxyIdentityOptions>>().Value;
        options.Claims.Subject.Should().Be("oid");
        options.Claims.Roles.Should().Be("app_roles");
        options.EmailTrust.Should().Be(ProxyEmailTrust.Trusted);
        options.LogoutUrl.Should().Be("/logout");
        options.ClockSkew.Should().Be(TimeSpan.FromSeconds(10));
        options.Development.Users.Should().ContainSingle().Which.Email.Should().Be("second@example.com");
        var currentUser = provider.GetRequiredService<Elarion.Identity.ClaimsCurrentUserOptions>();
        currentUser.UserIdClaimType.Should().Be("oid");
        currentUser.RoleClaimType.Should().Be("app_roles");
    }

    private static ServiceProvider Register(Dictionary<string, string?> settings, string environment) {
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        services.AddElarionProxyIdentity(configuration, new TestEnvironment(environment));
        return services.BuildServiceProvider();
    }

    private sealed class TestEnvironment(string name) : IHostEnvironment {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}

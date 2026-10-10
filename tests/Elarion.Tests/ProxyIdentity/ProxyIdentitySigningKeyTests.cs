using System.Diagnostics;
using System.Net;
using AwesomeAssertions;
using Xunit;

namespace Elarion.Tests.ProxyIdentity;

/// <summary>
/// Key retrieval as IdentityModel's <c>ConfigurationManager</c> performs it behind the proxy identity: one fetch for
/// any number of concurrent first requests, discovery with an issuer-bound document, rotation, and outages that keep
/// the last accepted keys. These pin the behaviour the documentation promises.
/// </summary>
public sealed class ProxyIdentitySigningKeyTests {
    private const string Issuer = ProxyIdentityTestHost.Issuer;
    private const string Audience = ProxyIdentityTestHost.Audience;
    private const string DiscoveryUrl = Issuer + "/.well-known/openid-configuration";
    private const string DiscoveredJwksUrl = Issuer + "/discovered-keys";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly TestSigningKey _keyA = TestSigningKey.Create("key-a");
    private readonly TestSigningKey _keyB = TestSigningKey.Create("key-b");

    [Fact]
    public async Task ConcurrentFirstRequests_ShareOneFetch_AndAllSignIn() {
        var keys = new FakeKeyEndpoint();
        keys.Documents[ProxyIdentityTestHost.JwksUrl] = TestSigningKey.Jwks(_keyA);
        keys.Hold();
        await using var host = await ProxyIdentityTestHost.StartAsync(ProxyIdentityTestHost.JwksSettings(), keys);
        var token = _keyA.Mint(Issuer, Audience);

        var requests = Enumerable.Range(0, 24).Select(_ => host.WhoAmIAsync(header: token)).ToList();
        await WaitUntilAsync(() => keys.TotalHits > 0);
        // Give every request time to reach the key lookup while the one fetch is still held open.
        await Task.Delay(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);
        // None was answered from an empty key cache while the fetch was in flight: every one waits for it.
        requests.Should().OnlyContain(request => !request.IsCompleted);
        keys.Release();
        var answers = await Task.WhenAll(requests);

        answers.Should().AllSatisfy(answer => answer.Status.Should().Be(HttpStatusCode.OK));
        keys.Hits(ProxyIdentityTestHost.JwksUrl).Should().Be(1);
    }

    [Fact]
    public async Task Discovery_FetchesTheKeysTheIssuersDocumentNames() {
        var keys = DiscoveryEndpoint(documentIssuer: Issuer);
        await using var host = await ProxyIdentityTestHost.StartAsync(DiscoverySettings(), keys);

        var whoAmI = await host.WhoAmIAsync(header: _keyA.Mint(Issuer, Audience));

        whoAmI.Status.Should().Be(HttpStatusCode.OK);
        keys.Hits(DiscoveryUrl).Should().Be(1);
        keys.Hits(DiscoveredJwksUrl).Should().Be(1);
    }

    [Fact]
    public async Task Discovery_ExplicitMetadataAddress_IsUsed() {
        const string metadata = "https://login.example/tenant/v2.0/.well-known/openid-configuration";
        var keys = new FakeKeyEndpoint();
        keys.Documents[metadata] = $$"""{"issuer":"{{Issuer}}","jwks_uri":"{{DiscoveredJwksUrl}}"}""";
        keys.Documents[DiscoveredJwksUrl] = TestSigningKey.Jwks(_keyA);
        var settings = DiscoverySettings();
        settings["ProxyIdentity:MetadataAddress"] = metadata;
        await using var host = await ProxyIdentityTestHost.StartAsync(settings, keys);

        var whoAmI = await host.WhoAmIAsync(header: _keyA.Mint(Issuer, Audience));

        whoAmI.Status.Should().Be(HttpStatusCode.OK);
        keys.Hits(metadata).Should().Be(1);
        keys.Hits(DiscoveryUrl).Should().Be(0);
    }

    [Fact]
    public async Task Discovery_DocumentNamingAnotherIssuer_IsRefused() {
        var keys = DiscoveryEndpoint(documentIssuer: "https://other-issuer.example");
        await using var host = await ProxyIdentityTestHost.StartAsync(DiscoverySettings(), keys);

        (await host.WhoAmIAsync(header: _keyA.Mint(Issuer, Audience))).Status.Should().Be(HttpStatusCode.Unauthorized);
        // Nor does the foreign issuer named by the document become acceptable.
        (await host.WhoAmIAsync(header: _keyA.Mint("https://other-issuer.example", Audience))).Status
            .Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task EmptyKeySet_AtStartup_IsRefused() {
        var keys = new FakeKeyEndpoint();
        keys.Documents[ProxyIdentityTestHost.JwksUrl] = TestSigningKey.Jwks();
        await using var host = await ProxyIdentityTestHost.StartAsync(ProxyIdentityTestHost.JwksSettings(), keys);

        (await host.WhoAmIAsync(header: _keyA.Mint(Issuer, Audience))).Status.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task KeyRotation_IsPickedUpAfterTheFirstTokenWithTheNewKey() {
        var keys = new FakeKeyEndpoint();
        keys.Documents[ProxyIdentityTestHost.JwksUrl] = TestSigningKey.Jwks(_keyA);
        await using var host = await ProxyIdentityTestHost.StartAsync(ProxyIdentityTestHost.JwksSettings(), keys);
        (await host.WhoAmIAsync(header: _keyA.Mint(Issuer, Audience))).Status.Should().Be(HttpStatusCode.OK);

        keys.Documents[ProxyIdentityTestHost.JwksUrl] = TestSigningKey.Jwks(_keyA, _keyB);
        var rotated = _keyB.Mint(Issuer, Audience);
        // The unknown key requests a refresh, which IdentityModel runs in the background: the request that discovered
        // the rotation is refused, the new key is accepted as soon as the refresh lands, and the old one keeps working.
        keys.Hold();
        (await host.WhoAmIAsync(header: rotated)).Status.Should().Be(HttpStatusCode.Unauthorized);
        keys.Release();
        await WaitUntilAsync(async () => (await host.WhoAmIAsync(header: rotated)).Status == HttpStatusCode.OK);

        keys.Hits(ProxyIdentityTestHost.JwksUrl).Should().Be(2);
        (await host.WhoAmIAsync(header: _keyA.Mint(Issuer, Audience))).Status.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RemovedKey_StopsValidatingAfterTheRefresh() {
        var keys = new FakeKeyEndpoint();
        keys.Documents[ProxyIdentityTestHost.JwksUrl] = TestSigningKey.Jwks(_keyA);
        await using var host = await ProxyIdentityTestHost.StartAsync(ProxyIdentityTestHost.JwksSettings(), keys);
        (await host.WhoAmIAsync(header: _keyA.Mint(Issuer, Audience))).Status.Should().Be(HttpStatusCode.OK);

        keys.Documents[ProxyIdentityTestHost.JwksUrl] = TestSigningKey.Jwks(_keyB);
        var rotated = _keyB.Mint(Issuer, Audience);
        await host.WhoAmIAsync(header: rotated);
        await WaitUntilAsync(async () => (await host.WhoAmIAsync(header: rotated)).Status == HttpStatusCode.OK);

        (await host.WhoAmIAsync(header: _keyA.Mint(Issuer, Audience))).Status.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task LastKnownGoodLifetime_KeepsAWithdrawnKeyDuringItsGrace() {
        var keys = new FakeKeyEndpoint();
        keys.Documents[ProxyIdentityTestHost.JwksUrl] = TestSigningKey.Jwks(_keyA);
        var settings = ProxyIdentityTestHost.JwksSettings();
        settings["ProxyIdentity:LastKnownGoodLifetime"] = "00:10:00";
        await using var host = await ProxyIdentityTestHost.StartAsync(settings, keys);
        (await host.WhoAmIAsync(header: _keyA.Mint(Issuer, Audience))).Status.Should().Be(HttpStatusCode.OK);

        keys.Documents[ProxyIdentityTestHost.JwksUrl] = TestSigningKey.Jwks(_keyB);
        var rotated = _keyB.Mint(Issuer, Audience);
        await host.WhoAmIAsync(header: rotated);
        await WaitUntilAsync(async () => (await host.WhoAmIAsync(header: rotated)).Status == HttpStatusCode.OK);

        (await host.WhoAmIAsync(header: _keyA.Mint(Issuer, Audience))).Status.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Outage_DuringRefresh_KeepsServingTheLastAcceptedKeys() {
        var keys = new FakeKeyEndpoint();
        keys.Documents[ProxyIdentityTestHost.JwksUrl] = TestSigningKey.Jwks(_keyA);
        await using var host = await ProxyIdentityTestHost.StartAsync(ProxyIdentityTestHost.JwksSettings(), keys);
        (await host.WhoAmIAsync(header: _keyA.Mint(Issuer, Audience))).Status.Should().Be(HttpStatusCode.OK);

        keys.Outage = true;
        (await host.WhoAmIAsync(header: _keyB.Mint(Issuer, Audience))).Status.Should().Be(HttpStatusCode.Unauthorized);
        await WaitUntilAsync(() => keys.Hits(ProxyIdentityTestHost.JwksUrl) >= 2);

        (await host.WhoAmIAsync(header: _keyA.Mint(Issuer, Audience))).Status.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RefusedRefresh_KeepsServingTheLastAcceptedKeys() {
        var keys = new FakeKeyEndpoint();
        keys.Documents[ProxyIdentityTestHost.JwksUrl] = TestSigningKey.Jwks(_keyA);
        await using var host = await ProxyIdentityTestHost.StartAsync(ProxyIdentityTestHost.JwksSettings(), keys);
        (await host.WhoAmIAsync(header: _keyA.Mint(Issuer, Audience))).Status.Should().Be(HttpStatusCode.OK);

        keys.Documents[ProxyIdentityTestHost.JwksUrl] = TestSigningKey.Jwks();
        (await host.WhoAmIAsync(header: _keyB.Mint(Issuer, Audience))).Status.Should().Be(HttpStatusCode.Unauthorized);
        await WaitUntilAsync(() => keys.Hits(ProxyIdentityTestHost.JwksUrl) >= 2);

        (await host.WhoAmIAsync(header: _keyA.Mint(Issuer, Audience))).Status.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Outage_AtStartup_FailsClosed_AndRecoversWhenTheEndpointReturns() {
        var keys = new FakeKeyEndpoint { Outage = true };
        keys.Documents[ProxyIdentityTestHost.JwksUrl] = TestSigningKey.Jwks(_keyA);
        await using var host = await ProxyIdentityTestHost.StartAsync(ProxyIdentityTestHost.JwksSettings(), keys);
        var token = _keyA.Mint(Issuer, Audience);

        (await host.WhoAmIAsync(header: token)).Status.Should().Be(HttpStatusCode.Unauthorized);

        keys.Outage = false;
        (await host.WhoAmIAsync(header: token)).Status.Should().Be(HttpStatusCode.OK);
    }

    private FakeKeyEndpoint DiscoveryEndpoint(string documentIssuer) {
        var keys = new FakeKeyEndpoint();
        keys.Documents[DiscoveryUrl] = $$"""{"issuer":"{{documentIssuer}}","jwks_uri":"{{DiscoveredJwksUrl}}"}""";
        keys.Documents[DiscoveredJwksUrl] = TestSigningKey.Jwks(_keyA);
        return keys;
    }

    private static Dictionary<string, string?> DiscoverySettings() {
        var settings = ProxyIdentityTestHost.JwksSettings();
        settings.Remove("ProxyIdentity:JwksUrl");
        return settings;
    }

    private static Task WaitUntilAsync(Func<bool> condition) {
        return WaitUntilAsync(() => Task.FromResult(condition()));
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition) {
        var watch = Stopwatch.StartNew();
        while (!await condition()) {
            if (watch.Elapsed > Patience) {
                throw new TimeoutException("The condition was not met in time.");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(25), TestContext.Current.CancellationToken);
        }
    }
}

using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Elarion.WebPush;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Elarion.Tests.WebPush;

public sealed class VapidTests {
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public void Generate_ProducesAValidatingP256Pair() {
        var keys = VapidKeys.Generate();

        Base64Url.DecodeFromChars(keys.PublicKey).Should().HaveCount(65).And.StartWith([0x04]);
        Base64Url.DecodeFromChars(keys.PrivateKey).Should().HaveCount(32);
        FluentActions.Invoking(keys.Validate).Should().NotThrow();
        keys.ToString().Should().NotContain(keys.PrivateKey);
    }

    [Fact]
    public void Validate_MismatchedHalves_Throws() {
        var keys = VapidKeys.Generate() with { PublicKey = VapidKeys.Generate().PublicKey };

        FluentActions.Invoking(keys.Validate).Should().Throw<Exception>();
    }

    [Fact]
    public void Token_IsAnEs256JwtForTheAudienceSignedByThePublicKey() {
        var keys = VapidKeys.Generate();
        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(1_900_000_000);

        var token = VapidTokenFactory.CreateToken("https://fcm.googleapis.com", "mailto:ops@example.com", expiresAt, keys);

        var parts = token.Split('.');
        parts.Should().HaveCount(3);
        using (var header = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[0]))) {
            header.RootElement.GetProperty("alg").GetString().Should().Be("ES256");
            header.RootElement.GetProperty("typ").GetString().Should().Be("JWT");
        }

        using (var claims = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1]))) {
            claims.RootElement.GetProperty("aud").GetString().Should().Be("https://fcm.googleapis.com");
            claims.RootElement.GetProperty("exp").GetInt64().Should().Be(1_900_000_000);
            claims.RootElement.GetProperty("sub").GetString().Should().Be("mailto:ops@example.com");
        }

        var publicKey = Base64Url.DecodeFromChars(keys.PublicKey);
        using var verifier = ECDsa.Create(new ECParameters {
            Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = publicKey[1..33], Y = publicKey[33..65] }
        });
        var signature = Base64Url.DecodeFromChars(parts[2]);
        signature.Should().HaveCount(64, "JWS ES256 signatures are raw r ‖ s, not DER");
        verifier.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
            .Should().BeTrue();
    }

    [Fact]
    public void AuthorizationHeader_IsCachedPerAudienceAndRenewedBeforeExpiry() {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch.AddYears(50));
        var factory = new VapidTokenFactory(time);
        var keys = VapidKeys.Generate();

        var first = factory.GetAuthorizationHeader("https://fcm.googleapis.com", "mailto:a@b.c", keys);
        factory.GetAuthorizationHeader("https://fcm.googleapis.com", "mailto:a@b.c", keys).Should().Be(first);
        factory.GetAuthorizationHeader("https://updates.push.services.mozilla.com", "mailto:a@b.c", keys)
            .Should().NotBe(first);
        first.Should().StartWith("vapid t=").And.EndWith($", k={keys.PublicKey}");

        time.Advance(VapidTokenFactory.TokenLifetime - TimeSpan.FromMinutes(30));
        factory.GetAuthorizationHeader("https://fcm.googleapis.com", "mailto:a@b.c", keys).Should().NotBe(first);
    }

    [Fact]
    public async Task Provider_ConfiguredKeysWinOverTheStore() {
        var configured = VapidKeys.Generate();
        var store = new InMemoryVapidKeyStore();
        await store.GetOrAddAsync(VapidKeys.Generate(), TestToken);
        var provider = new VapidKeyProvider(
            new WebPushOptions { PublicKey = configured.PublicKey, PrivateKey = configured.PrivateKey },
            store, NullLogger<VapidKeyProvider>.Instance);

        (await provider.GetAsync(TestToken)).Should().Be(configured);
    }

    [Fact]
    public async Task Provider_GeneratesOnceAndConcurrentNodesAdoptTheWinner() {
        var store = new InMemoryVapidKeyStore();
        var providers = Enumerable.Range(0, 8)
            .Select(_ => new VapidKeyProvider(new WebPushOptions(), store, NullLogger<VapidKeyProvider>.Instance))
            .ToArray();

        var resolved = await Task.WhenAll(providers.Select(provider => provider.GetAsync(TestToken).AsTask()));

        resolved.Distinct().Should().ContainSingle();
        (await store.GetAsync(TestToken)).Should().Be(resolved[0]);
        (await providers[0].GetAsync(TestToken)).Should().BeSameAs(resolved[0]);
    }

    [Theory]
    [InlineData(null, "Subject is required")]
    [InlineData("ops@example.com", "mailto: or https:")]
    public void Options_InvalidSubject_Throws(string? subject, string message) {
        FluentActions.Invoking(() => new WebPushOptions { Subject = subject }.Validate())
            .Should().Throw<InvalidOperationException>().WithMessage($"*{message}*");
    }

    [Fact]
    public void Options_HalfConfiguredKeys_Throws() {
        FluentActions.Invoking(() => new WebPushOptions {
                Subject = "mailto:a@b.c", PublicKey = VapidKeys.Generate().PublicKey
            }.Validate())
            .Should().Throw<InvalidOperationException>().WithMessage("*together*");
    }

    [Theory]
    [InlineData("fcm.googleapis.com", true)]
    [InlineData("updates.push.services.mozilla.com", true)]
    [InlineData("web.push.apple.com", true)]
    [InlineData("wns2-by3p.notify.windows.com", true)]
    [InlineData("FCM.GOOGLEAPIS.COM", true)]
    [InlineData("notfcm.googleapis.com", false)]
    [InlineData("fcm.googleapis.com.attacker.example", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("localhost", false)]
    public void Options_EndpointHostAllowList_MatchesHostsAndSubdomainsOnly(string host, bool allowed) {
        new WebPushOptions().IsEndpointHostAllowed(host).Should().Be(allowed);
        new WebPushOptions { AllowAnyEndpointHost = true }.IsEndpointHostAllowed(host).Should().BeTrue();
    }
}

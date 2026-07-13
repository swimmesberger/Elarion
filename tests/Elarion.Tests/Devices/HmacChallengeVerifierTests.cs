using System.Security.Claims;
using System.Security.Cryptography;
using AwesomeAssertions;
using Elarion.Devices;
using Xunit;

namespace Elarion.Tests.Devices;

public sealed class HmacChallengeVerifierTests {
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    private readonly InMemoryDeviceKeyStore _keys = new();

    private HmacChallengeVerifier CreateVerifier() => new(_keys);

    private async Task<byte[]> ProvisionAsync(string deviceId) {
        var key = RandomNumberGenerator.GetBytes(32);
        await _keys.PutAsync(deviceId, key, TestToken);
        return key;
    }

    [Fact]
    public async Task Verify_CorrectResponse_ReturnsTheDevicePrincipal() {
        var key = await ProvisionAsync("meter-1");
        var verifier = CreateVerifier();
        var nonce = HmacChallengeVerifier.CreateNonce();

        var principal = await verifier.VerifyAsync(
            "meter-1", nonce, HmacChallengeVerifier.ComputeResponse(key, nonce), TestToken);

        principal.Should().NotBeNull();
        DevicePrincipal.IsDevice(principal!).Should().BeTrue();
        DevicePrincipal.GetDeviceId(principal!).Should().Be("meter-1");
        principal!.FindFirst(ClaimTypes.NameIdentifier)!.Value.Should().Be("meter-1");
        principal.Identity!.IsAuthenticated.Should().BeTrue();
    }

    [Fact]
    public async Task Verify_TamperedResponse_ReturnsNull() {
        var key = await ProvisionAsync("meter-1");
        var verifier = CreateVerifier();
        var nonce = HmacChallengeVerifier.CreateNonce();
        var response = HmacChallengeVerifier.ComputeResponse(key, nonce);
        response[0] ^= 0xFF;

        (await verifier.VerifyAsync("meter-1", nonce, response, TestToken)).Should().BeNull();
    }

    [Fact]
    public async Task Verify_ResponseForADifferentNonce_ReturnsNull() {
        var key = await ProvisionAsync("meter-1");
        var verifier = CreateVerifier();
        var staleResponse = HmacChallengeVerifier.ComputeResponse(key, HmacChallengeVerifier.CreateNonce());

        (await verifier.VerifyAsync("meter-1", HmacChallengeVerifier.CreateNonce(), staleResponse, TestToken))
            .Should().BeNull();
    }

    [Fact]
    public async Task Verify_UnknownDevice_ReturnsNull() {
        var verifier = CreateVerifier();
        var nonce = HmacChallengeVerifier.CreateNonce();
        var response = HmacChallengeVerifier.ComputeResponse(RandomNumberGenerator.GetBytes(32), nonce);

        (await verifier.VerifyAsync("ghost", nonce, response, TestToken)).Should().BeNull();
    }

    [Fact]
    public async Task Verify_AnotherDevicesKey_ReturnsNull() {
        await ProvisionAsync("meter-1");
        var otherKey = await ProvisionAsync("meter-2");
        var verifier = CreateVerifier();
        var nonce = HmacChallengeVerifier.CreateNonce();

        (await verifier.VerifyAsync("meter-1", nonce, HmacChallengeVerifier.ComputeResponse(otherKey, nonce), TestToken))
            .Should().BeNull();
    }

    [Fact]
    public async Task Verify_EmptyDeviceIdOrNonce_ReturnsNull() {
        var verifier = CreateVerifier();

        (await verifier.VerifyAsync("", HmacChallengeVerifier.CreateNonce(), new byte[32], TestToken)).Should().BeNull();
        (await verifier.VerifyAsync("meter-1", ReadOnlyMemory<byte>.Empty, new byte[32], TestToken)).Should().BeNull();
    }

    [Fact]
    public void CreateNonce_IsFreshPerCallAndRejectsWeakSizes() {
        HmacChallengeVerifier.CreateNonce().Should().NotEqual(HmacChallengeVerifier.CreateNonce());

        var act = () => HmacChallengeVerifier.CreateNonce(8);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void DevicePrincipal_NonDevicePrincipals_AreRecognized() {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "user-1")], "Cookies"));

        DevicePrincipal.IsDevice(user).Should().BeFalse();
        DevicePrincipal.GetDeviceId(user).Should().BeNull();
    }

    [Fact]
    public void DevicePrincipal_CarriesAdditionalClaims() {
        var principal = DevicePrincipal.Create("meter-1", [new Claim("elarion:permission", "telemetry.write")]);

        principal.HasClaim("elarion:permission", "telemetry.write").Should().BeTrue();
    }
}

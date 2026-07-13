using AwesomeAssertions;
using Elarion.Devices;
using Elarion.Devices.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.Devices;

[Trait("Category", "Integration")]
public sealed class PostgreSqlDeviceIdentityIntegrationTests(PostgreSqlDeviceIdentityFixture fixture)
    : IClassFixture<PostgreSqlDeviceIdentityFixture> {
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task KeyStore_CreateGetRemove_Roundtrips() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var provider = CreateProvider();
        var store = provider.GetRequiredService<IDeviceKeyStore>();
        var deviceId = NewDeviceId();
        var key = new byte[] { 1, 2, 3, 4, 5 };

        await store.PutAsync(deviceId, key, TestToken);

        var stored = await store.GetKeyAsync(deviceId, TestToken);
        stored.Should().NotBeNull();
        stored!.Value.ToArray().Should().Equal(key);

        (await store.RemoveAsync(deviceId, TestToken)).Should().BeTrue();
        (await store.GetKeyAsync(deviceId, TestToken)).Should().BeNull();
        (await store.RemoveAsync(deviceId, TestToken)).Should().BeFalse();
    }

    [Fact]
    public async Task KeyStore_PutForAnExistingDevice_RotatesTheKey() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var provider = CreateProvider();
        var store = provider.GetRequiredService<IDeviceKeyStore>();
        var deviceId = NewDeviceId();
        await store.PutAsync(deviceId, new byte[] { 1 }, TestToken);

        await store.PutAsync(deviceId, new byte[] { 2 }, TestToken);

        (await store.GetKeyAsync(deviceId, TestToken))!.Value.ToArray().Should().Equal(2);
    }

    [Fact]
    public async Task PairingStore_DuplicateHash_ReturnsFalse() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var provider = CreateProvider();
        var store = provider.GetRequiredService<IPairingCodeStore>();
        var entry = NewEntry(expiresIn: TimeSpan.FromMinutes(5));

        (await store.TryCreateAsync(entry, TestToken)).Should().BeTrue();
        (await store.TryCreateAsync(entry with { DeviceId = NewDeviceId() }, TestToken)).Should().BeFalse();
    }

    [Fact]
    public async Task PairingStore_Claim_ConsumesExactlyOnce() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var provider = CreateProvider();
        var store = provider.GetRequiredService<IPairingCodeStore>();
        var entry = NewEntry(expiresIn: TimeSpan.FromMinutes(5));
        await store.TryCreateAsync(entry, TestToken);

        var claimed = await store.ClaimAsync(entry.CodeHash, DateTimeOffset.UtcNow, TestToken);
        claimed.Should().NotBeNull();
        claimed!.DeviceId.Should().Be(entry.DeviceId);
        claimed.ExpiresAt.Should().BeCloseTo(entry.ExpiresAt, TimeSpan.FromMilliseconds(1));

        (await store.ClaimAsync(entry.CodeHash, DateTimeOffset.UtcNow, TestToken)).Should().BeNull();
    }

    [Fact]
    public async Task PairingStore_ConcurrentClaims_HaveExactlyOneWinner() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var provider = CreateProvider();
        var store = provider.GetRequiredService<IPairingCodeStore>();
        var entry = NewEntry(expiresIn: TimeSpan.FromMinutes(5));
        await store.TryCreateAsync(entry, TestToken);

        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            store.ClaimAsync(entry.CodeHash, DateTimeOffset.UtcNow, TestToken).AsTask()));

        claims.Count(claimedEntry => claimedEntry is not null).Should().Be(1);
    }

    [Fact]
    public async Task PairingStore_ExpiredEntry_NeverClaims() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var provider = CreateProvider();
        var store = provider.GetRequiredService<IPairingCodeStore>();
        var entry = NewEntry(expiresIn: TimeSpan.FromMinutes(-1));
        await store.TryCreateAsync(entry, TestToken);

        (await store.ClaimAsync(entry.CodeHash, DateTimeOffset.UtcNow, TestToken)).Should().BeNull();
    }

    [Fact]
    public async Task PairingStore_DeleteExpired_RemovesOnlyExpiredRows() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var provider = CreateProvider();
        var store = provider.GetRequiredService<IPairingCodeStore>();
        var expired = NewEntry(expiresIn: TimeSpan.FromMinutes(-1));
        var live = NewEntry(expiresIn: TimeSpan.FromMinutes(5));
        await store.TryCreateAsync(expired, TestToken);
        await store.TryCreateAsync(live, TestToken);

        var removed = await store.DeleteExpiredAsync(DateTimeOffset.UtcNow, TestToken);

        removed.Should().BeGreaterThanOrEqualTo(1);
        (await store.ClaimAsync(live.CodeHash, DateTimeOffset.UtcNow, TestToken))!.DeviceId.Should().Be(live.DeviceId);
    }

    [Fact]
    public async Task FullChain_IssueRedeemVerify_OverTheEfStores() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var provider = CreateProvider();
        var pairing = provider.GetRequiredService<IDevicePairingService>();
        var verifier = provider.GetRequiredService<HmacChallengeVerifier>();

        var issued = await pairing.IssueAsync(cancellationToken: TestToken);
        var credentials = await pairing.RedeemAsync(issued.Code, TestToken);
        credentials.Should().NotBeNull();

        var nonce = HmacChallengeVerifier.CreateNonce();
        var response = HmacChallengeVerifier.ComputeResponse(credentials!.Key.Span, nonce);
        var principal = await verifier.VerifyAsync(credentials.DeviceId, nonce, response, TestToken);

        principal.Should().NotBeNull();
        DevicePrincipal.GetDeviceId(principal!).Should().Be(issued.DeviceId);

        (await pairing.RedeemAsync(issued.Code, TestToken)).Should().BeNull();
    }

    private static string NewDeviceId() => Guid.CreateVersion7().ToString();

    private static PairingCodeEntry NewEntry(TimeSpan expiresIn) =>
        new() {
            CodeHash = Convert.ToHexStringLower(Guid.CreateVersion7().ToByteArray()),
            DeviceId = NewDeviceId(),
            ExpiresAt = DateTimeOffset.UtcNow + expiresIn,
        };

    private ServiceProvider CreateProvider() {
        var services = new ServiceCollection();
        services.AddDbContext<DeviceIdentityIntegrationDbContext>(options => options.UseNpgsql(fixture.ConnectionString));
        services.AddElarionDeviceIdentityEntityFrameworkCore<DeviceIdentityIntegrationDbContext>();
        return services.BuildServiceProvider();
    }
}

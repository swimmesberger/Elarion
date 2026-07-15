using AwesomeAssertions;
using Elarion.Devices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Elarion.Tests.Devices;

public sealed class DevicePairingServiceTests {
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-07-13T12:00:00Z"));
    private readonly InMemoryPairingCodeStore _codes = new();
    private readonly InMemoryDeviceKeyStore _keys = new();
    private readonly DeviceProvisioningOptions _options = new();

    private DevicePairingService CreateService() => new(_codes, _keys, _options, _time);

    [Fact]
    public async Task Issue_ProducesCodeFromAlphabetWithConfiguredLengthAndTtl() {
        var service = CreateService();

        var issued = await service.IssueAsync(cancellationToken: TestToken);

        issued.Code.Should().HaveLength(_options.CodeLength);
        issued.Code.ToCharArray().Should().OnlyContain(ch => _options.CodeAlphabet.Contains(ch));
        issued.DeviceId.Should().NotBeNullOrWhiteSpace();
        issued.ExpiresAt.Should().Be(_time.GetUtcNow() + _options.CodeTimeToLive);
    }

    [Fact]
    public async Task Issue_HonorsPerIssueOverrides() {
        var service = CreateService();

        var issued = await service.IssueAsync(
            new PairingCodeIssueOptions { DeviceId = "gateway-7", TimeToLive = TimeSpan.FromMinutes(2) },
            TestToken);

        issued.DeviceId.Should().Be("gateway-7");
        issued.ExpiresAt.Should().Be(_time.GetUtcNow() + TimeSpan.FromMinutes(2));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Issue_RejectsBlankCallerSuppliedDeviceId(string deviceId) {
        var service = CreateService();

        var issue = async () => await service.IssueAsync(new PairingCodeIssueOptions { DeviceId = deviceId }, TestToken);

        await issue.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Issue_RejectsDeviceIdLongerThanTheStoreBound_AndAcceptsTheBoundItself() {
        var service = CreateService();

        // One over the durable stores' column width fails at issue, not later inside the store.
        var issue = async () => await service.IssueAsync(
            new PairingCodeIssueOptions { DeviceId = new string('d', DeviceIds.MaxLength + 1) }, TestToken);
        await issue.Should().ThrowAsync<ArgumentException>().WithMessage($"*{DeviceIds.MaxLength}*");

        var issued = await service.IssueAsync(
            new PairingCodeIssueOptions { DeviceId = new string('d', DeviceIds.MaxLength) }, TestToken);
        issued.DeviceId.Should().HaveLength(DeviceIds.MaxLength);
    }

    [Fact]
    public async Task Redeem_MintsCredentialsAndStoresTheKey() {
        var service = CreateService();
        var issued = await service.IssueAsync(cancellationToken: TestToken);

        var credentials = await service.RedeemAsync(issued.Code, TestToken);

        credentials.Should().NotBeNull();
        credentials!.DeviceId.Should().Be(issued.DeviceId);
        credentials.Key.Length.Should().Be(_options.KeySizeBytes);
        var stored = await _keys.GetKeyAsync(issued.DeviceId, TestToken);
        stored.Should().NotBeNull();
        stored!.Value.ToArray().Should().Equal(credentials.Key.ToArray());
    }

    [Fact]
    public async Task Redeem_IsSingleUse() {
        var service = CreateService();
        var issued = await service.IssueAsync(cancellationToken: TestToken);

        (await service.RedeemAsync(issued.Code, TestToken)).Should().NotBeNull();
        (await service.RedeemAsync(issued.Code, TestToken)).Should().BeNull();
    }

    [Fact]
    public async Task Redeem_NormalizesHumanTypedInput() {
        var service = CreateService();
        var issued = await service.IssueAsync(cancellationToken: TestToken);
        var typed = $" {issued.Code[..4].ToLowerInvariant()}-{issued.Code[4..].ToLowerInvariant()} ";

        var credentials = await service.RedeemAsync(typed, TestToken);

        credentials.Should().NotBeNull();
    }

    [Fact]
    public async Task Redeem_UnknownCode_ReturnsNull() {
        var service = CreateService();

        (await service.RedeemAsync("WRONGCOD", TestToken)).Should().BeNull();
        (await service.RedeemAsync("   ", TestToken)).Should().BeNull();
    }

    [Fact]
    public async Task Redeem_ExpiredCode_ReturnsNull() {
        var service = CreateService();
        var issued = await service.IssueAsync(cancellationToken: TestToken);

        _time.Advance(_options.CodeTimeToLive + TimeSpan.FromSeconds(1));

        (await service.RedeemAsync(issued.Code, TestToken)).Should().BeNull();
    }

    [Fact]
    public async Task AddElarionDeviceIdentity_WithInMemoryStores_ResolvesTheWholeChain() {
        var services = new ServiceCollection();
        services.AddElarionDeviceIdentity(options => options.CodeLength = 10);
        services.AddElarionInMemoryDeviceIdentityStores();
        await using var provider = services.BuildServiceProvider();

        var pairing = provider.GetRequiredService<IDevicePairingService>();
        var issued = await pairing.IssueAsync(cancellationToken: TestToken);
        issued.Code.Should().HaveLength(10);

        var credentials = await pairing.RedeemAsync(issued.Code, TestToken);
        credentials.Should().NotBeNull();

        var verifier = provider.GetRequiredService<HmacChallengeVerifier>();
        var nonce = HmacChallengeVerifier.CreateNonce();
        var response = HmacChallengeVerifier.ComputeResponse(credentials!.Key.Span, nonce);
        (await verifier.VerifyAsync(credentials.DeviceId, nonce, response, TestToken)).Should().NotBeNull();
    }

    [Fact]
    public void AddElarionDeviceIdentity_CalledTwice_ComposesOntoOneOptionsInstance() {
        var services = new ServiceCollection();
        services.AddElarionDeviceIdentity(options => options.CodeLength = 12);
        services.AddElarionDeviceIdentity(options => options.KeySizeBytes = 64);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<DeviceProvisioningOptions>();
        options.CodeLength.Should().Be(12);
        options.KeySizeBytes.Should().Be(64);
    }

    [Fact]
    public async Task InMemoryKeyStore_MissingOrRemovedKey_IsNullNeverEmpty() {
        (await _keys.GetKeyAsync("ghost", TestToken)).Should().BeNull();

        await _keys.PutAsync("meter-1", new byte[] { 1 }, TestToken);
        (await _keys.RemoveAsync("meter-1", TestToken)).Should().BeTrue();
        (await _keys.GetKeyAsync("meter-1", TestToken)).Should().BeNull();
    }

    [Fact]
    public async Task Redeem_ForAnAlreadyProvisionedDeviceId_RotatesTheKey() {
        var service = CreateService();
        var first = await service.RedeemAsync(
            (await service.IssueAsync(new PairingCodeIssueOptions { DeviceId = "gateway-7" }, TestToken)).Code, TestToken);

        var second = await service.RedeemAsync(
            (await service.IssueAsync(new PairingCodeIssueOptions { DeviceId = "gateway-7" }, TestToken)).Code, TestToken);

        second.Should().NotBeNull();
        second!.Key.ToArray().Should().NotEqual(first!.Key.ToArray());
        (await _keys.GetKeyAsync("gateway-7", TestToken))!.Value.ToArray().Should().Equal(second.Key.ToArray());
    }

    [Fact]
    public async Task Reissue_SupersedesTheEarlierPendingCode_AndRevocationMakesCodesUnredeemable() {
        var service = CreateService();
        var first = await service.IssueAsync(new PairingCodeIssueOptions { DeviceId = "gateway-pending" }, TestToken);
        var second = await service.IssueAsync(new PairingCodeIssueOptions { DeviceId = "gateway-pending" }, TestToken);

        (await service.RedeemAsync(first.Code, TestToken)).Should().BeNull();
        (await service.RevokeAsync(second.DeviceId, TestToken)).Should().Be(1);
        (await service.RedeemAsync(second.Code, TestToken)).Should().BeNull();
    }

    [Theory]
    [InlineData("abcdefghjkmnpqrstvwxyz23456789")] // lowercase: not normalization-stable
    [InlineData("ABCD-EFGH23456789")]              // separator: stripped by normalization
    [InlineData("AABCDEFGH23456789")]              // duplicate: biases generation
    [InlineData("ABC123")]                         // too short
    public void AddElarionDeviceIdentity_RejectsUnredeemableOrBiasedAlphabets(string alphabet) {
        var services = new ServiceCollection();

        var act = () => services.AddElarionDeviceIdentity(options => options.CodeAlphabet = alphabet);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AddElarionDeviceIdentity_HostRegisteredOptionsFactory_FailsLoud() {
        var services = new ServiceCollection();
        services.AddSingleton(_ => new DeviceProvisioningOptions());

        var act = () => services.AddElarionDeviceIdentity();

        act.Should().Throw<InvalidOperationException>().WithMessage("*AddElarionDeviceIdentity*");
    }

    [Fact]
    public void AddElarionDeviceIdentity_RejectsUnsafeOptions() {
        var services = new ServiceCollection();

        var act = () => services.AddElarionDeviceIdentity(options => options.KeySizeBytes = 8);

        act.Should().Throw<ArgumentException>();
    }
}

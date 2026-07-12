using AwesomeAssertions;
using Elarion.Coordination.PostgreSql;
using Elarion.Tests.Actors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Elarion.Tests.Coordination;

/// <summary>
/// The ADR-0049 role lease protocol against real PostgreSQL, driven deterministically: the lease
/// methods take their clock from a shared <see cref="FakeTimeProvider"/> (the database clock is
/// never consulted), so acquisition, blocking, expiry takeover, and release are all exact.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgreSqlRoleLeaseIntegrationTests(PostgreSqlActorSnapshotStoreFixture fixture)
    : IClassFixture<PostgreSqlActorSnapshotStoreFixture> {
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Acquire_Renew_Block_TakeoverAfterExpiry() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var time = new FakeTimeProvider();
        await using var provider = CreateProvider();
        var role = NewRole();
        var first = CreateLease(provider, time, role, "instance-a");
        var second = CreateLease(provider, time, role, "instance-b");

        // A acquires; B is blocked while the hold is fresh and learns who holds it.
        (await first.TryAcquireOrRenewAsync(TestToken)).Should().BeTrue();
        first.IsHeld.Should().BeTrue();
        (await second.TryAcquireOrRenewAsync(TestToken)).Should().BeFalse();
        second.IsHeld.Should().BeFalse();
        second.CurrentHolder.Should().Be("instance-a");

        // A renews mid-hold; B stays blocked.
        time.Advance(TimeSpan.FromSeconds(10));
        (await first.TryAcquireOrRenewAsync(TestToken)).Should().BeTrue();
        (await second.TryAcquireOrRenewAsync(TestToken)).Should().BeFalse();

        // A stops renewing: IsHeld decays locally at the safety margin, and once the stored hold
        // expires B takes over. A's next renewal attempt observes the loss.
        time.Advance(TimeSpan.FromSeconds(31));
        first.IsHeld.Should().BeFalse();
        (await second.TryAcquireOrRenewAsync(TestToken)).Should().BeTrue();
        second.IsHeld.Should().BeTrue();
        (await first.TryAcquireOrRenewAsync(TestToken)).Should().BeFalse();
        first.CurrentHolder.Should().Be("instance-b");
    }

    [Fact]
    public async Task Release_HandsTheRoleOverImmediately() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var time = new FakeTimeProvider();
        await using var provider = CreateProvider();
        var role = NewRole();
        var first = CreateLease(provider, time, role, "instance-a");
        var second = CreateLease(provider, time, role, "instance-b");

        (await first.TryAcquireOrRenewAsync(TestToken)).Should().BeTrue();
        (await second.TryAcquireOrRenewAsync(TestToken)).Should().BeFalse();

        // Graceful shutdown: A expires its own row, so B takes over on its next attempt — no
        // LeaseDuration wait.
        await first.ReleaseAsync(TestToken);
        first.IsHeld.Should().BeFalse();
        time.Advance(TimeSpan.FromMilliseconds(1));
        (await second.TryAcquireOrRenewAsync(TestToken)).Should().BeTrue();
    }

    [Fact]
    public async Task IsHeld_TurnsFalseBeforeAnotherInstanceCanTakeOver() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var time = new FakeTimeProvider();
        await using var provider = CreateProvider();
        var role = NewRole();
        var first = CreateLease(provider, time, role, "instance-a");
        var second = CreateLease(provider, time, role, "instance-b");

        (await first.TryAcquireOrRenewAsync(TestToken)).Should().BeTrue();

        // Between (LeaseDuration - HeldSafetyMargin) and LeaseDuration: A already answers "not
        // held" locally while B still cannot take the row — the window that guarantees the old
        // holder stops acting before a new one can start.
        time.Advance(TimeSpan.FromSeconds(27));
        first.IsHeld.Should().BeFalse();
        (await second.TryAcquireOrRenewAsync(TestToken)).Should().BeFalse();
    }

    [Fact]
    public async Task HolderAddress_TravelsWithTheRow_AndFollowsTakeover() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var time = new FakeTimeProvider();
        await using var provider = CreateProvider();
        var role = NewRole();
        var first = CreateLease(provider, time, role, "instance-a", advertisedAddress: "http://10.0.0.1:8080");
        var second = CreateLease(provider, time, role, "instance-b", advertisedAddress: "http://10.0.0.2:8080");

        // The holder advertises; the blocked instance learns the holder's address from the row —
        // the whole "how do I reach the holder" story is this one column (ADR-0050).
        (await first.TryAcquireOrRenewAsync(TestToken)).Should().BeTrue();
        first.CurrentHolderAddress.Should().Be("http://10.0.0.1:8080");
        (await second.TryAcquireOrRenewAsync(TestToken)).Should().BeFalse();
        second.CurrentHolderAddress.Should().Be("http://10.0.0.1:8080");

        // Takeover: the address follows the role to its new holder.
        time.Advance(TimeSpan.FromSeconds(31));
        (await second.TryAcquireOrRenewAsync(TestToken)).Should().BeTrue();
        (await first.TryAcquireOrRenewAsync(TestToken)).Should().BeFalse();
        first.CurrentHolderAddress.Should().Be("http://10.0.0.2:8080");
    }

    [Fact]
    public async Task IndependentRoles_ElectIndependentHolders() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var time = new FakeTimeProvider();
        await using var provider = CreateProvider();
        var actorsRole = NewRole();
        var maintenanceRole = NewRole();
        var instanceA = CreateLease(provider, time, actorsRole, "instance-a");
        var instanceB = CreateLease(provider, time, maintenanceRole, "instance-b");

        // Two roles, two holders — one process can be the holder for one concern and not another.
        (await instanceA.TryAcquireOrRenewAsync(TestToken)).Should().BeTrue();
        (await instanceB.TryAcquireOrRenewAsync(TestToken)).Should().BeTrue();
        instanceA.Role.Should().Be(actorsRole);
        instanceB.Role.Should().Be(maintenanceRole);
    }

    private static string NewRole() => $"role-{Guid.CreateVersion7():N}";

    private PostgreSqlRoleLease<ActorSnapshotIntegrationDbContext> CreateLease(
        ServiceProvider provider, FakeTimeProvider time, string role, string instanceId,
        string? advertisedAddress = null) =>
        new(provider.GetRequiredService<IServiceScopeFactory>(),
            new RoleLeaseOptions {
                RoleName = role,
                InstanceId = instanceId,
                LeaseDuration = TimeSpan.FromSeconds(30),
                RenewInterval = TimeSpan.FromSeconds(10),
                HeldSafetyMargin = TimeSpan.FromSeconds(5),
                AdvertisedAddress = advertisedAddress
            },
            time,
            NullLogger<PostgreSqlRoleLease<ActorSnapshotIntegrationDbContext>>.Instance);

    private ServiceProvider CreateProvider() {
        var services = new ServiceCollection();
        services.AddDbContext<ActorSnapshotIntegrationDbContext>(options => options.UseNpgsql(fixture.ConnectionString));
        return services.BuildServiceProvider();
    }
}

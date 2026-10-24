using AwesomeAssertions;
using Elarion.Abstractions.Coordination;
using Elarion.Devices.EntityFrameworkCore;
using Elarion.Tests.Pipeline;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Elarion.Tests.Coordination;

public sealed class AdvisoryLockKeyTests {
    [Fact]
    public void Text_JoinsScopeAndKeyWithTheSeparator() {
        AdvisoryLockKey.Text("billing-account", "42").Should().Be("billing-account:42");
    }

    [Theory]
    [InlineData("")]
    [InlineData("a:b")]
    [InlineData("elarion.scheduler.job")]
    [InlineData("elarion.mine")]
    public void Text_RejectsEmptyAmbiguousAndReservedScopes(string scope) {
        var act = () => AdvisoryLockKey.Text(scope, "42");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void FrameworkScopes_AreReservedAndFormatLikeApplicationScopes() {
        AdvisoryLockKey.Scopes.SchedulerJob.Should().StartWith(AdvisoryLockKey.ReservedScopePrefix);
        AdvisoryLockKey.Scopes.DevicePairing.Should().StartWith(AdvisoryLockKey.ReservedScopePrefix);
        AdvisoryLockKey.FrameworkText(AdvisoryLockKey.Scopes.SchedulerJob, "nightly")
            .Should().Be("elarion.scheduler.job:nightly");
    }

    [Fact]
    public void DevicePairingStore_LocksTheDocumentedFrameworkKey() {
        // The devices package spells the key out (it does not depend on Elarion.Abstractions); this pins it.
        EfCoreDevicePairingCodeStore<UnitOfWorkDbContext>.PairingLockKey("device-7")
            .Should().Be(AdvisoryLockKey.FrameworkText(AdvisoryLockKey.Scopes.DevicePairing, "device-7"));
    }
}

public sealed class DbContextLockProviderTests {
    [Fact]
    public async Task Locks_OnAProviderOtherThanPostgreSql_AreRefused() {
        await using var context = new UnitOfWorkDbContext(
            new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<UnitOfWorkDbContext>()
                .UseSqlite("Data Source=:memory:")
                .Options);

        var act = async () => await Elarion.EntityFrameworkCore.UnitOfWork.DbContextLockExtensions.AdvisoryLockAsync(
            context, "billing-account", "1", TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<NotSupportedException>().WithMessage("*need PostgreSQL*");
    }
}

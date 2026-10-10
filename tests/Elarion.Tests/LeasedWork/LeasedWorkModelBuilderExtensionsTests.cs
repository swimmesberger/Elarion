using AwesomeAssertions;
using Elarion.EntityFrameworkCore.LeasedWork;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Xunit;

namespace Elarion.Tests.LeasedWork;

public sealed class LeasedWorkModelBuilderExtensionsTests {
    private static IModel Build(Action<ModelBuilder> configure) {
        var modelBuilder = new ModelBuilder(new ConventionSet());
        configure(modelBuilder);
        return modelBuilder.FinalizeModel();
    }

    [Fact]
    public void HasLeasedWork_MapsTheLeaseColumns_AndAPartialClaimIndex() {
        var model = Build(modelBuilder => modelBuilder.Entity<TestDelivery>(entity => {
            entity.ToTable("deliveries");
            entity.HasKey(delivery => delivery.Id);
            entity.HasLeasedWork(delivery => new { delivery.CreatedAtUtc, delivery.Id }, "completed_at_utc IS NULL");
        }));

        var delivery = model.FindEntityType(typeof(TestDelivery))!;
        delivery.FindProperty(nameof(ILeasedWorkRow.LockId))!.GetColumnName().Should().Be("lock_id");
        delivery.FindProperty(nameof(ILeasedWorkRow.LockedUntilUtc))!.GetColumnName().Should().Be("locked_until_utc");
        var claimIndex = delivery.GetIndexes().Single();
        claimIndex.Properties.Select(property => property.Name)
            .Should().Equal(nameof(TestDelivery.CreatedAtUtc), nameof(TestDelivery.Id));
        claimIndex.GetDatabaseName().Should().Be("ix_deliveries_claim");
        claimIndex.GetFilter().Should().Be("completed_at_utc IS NULL");
    }

    [Fact]
    public void HasLeasedWork_WithoutSnakeCase_UsesPascalCaseNames() {
        var model = Build(modelBuilder => modelBuilder.Entity<TestDelivery>(entity => {
            entity.ToTable("Deliveries");
            entity.HasKey(delivery => delivery.Id);
            entity.HasLeasedWork(
                delivery => new { delivery.CreatedAtUtc, delivery.Id },
                "\"CompletedAtUtc\" IS NULL",
                snakeCase: false);
        }));

        var delivery = model.FindEntityType(typeof(TestDelivery))!;
        delivery.FindProperty(nameof(ILeasedWorkRow.LockId))!.GetColumnName().Should().Be("LockId");
        delivery.FindProperty(nameof(ILeasedWorkRow.LockedUntilUtc))!.GetColumnName().Should().Be("LockedUntilUtc");
        delivery.GetIndexes().Single().GetDatabaseName().Should().Be("IX_Deliveries_Claim");
    }

    [Fact]
    public void HasLeasedWork_UsesAnExplicitClaimIndexName() {
        var model = Build(modelBuilder => modelBuilder.Entity<TestDelivery>(entity => {
            entity.ToTable("deliveries");
            entity.HasKey(delivery => delivery.Id);
            entity.HasLeasedWork(
                delivery => new { delivery.CreatedAtUtc, delivery.Id },
                "completed_at_utc IS NULL",
                claimIndexName: "deliveries_pending");
        }));

        model.FindEntityType(typeof(TestDelivery))!.GetIndexes().Single().GetDatabaseName()
            .Should().Be("deliveries_pending");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void HasLeasedWork_RequiresThePendingFilter(string filter) {
        var act = () => Build(modelBuilder => modelBuilder.Entity<TestDelivery>(entity => {
            entity.ToTable("deliveries");
            entity.HasLeasedWork(delivery => new { delivery.CreatedAtUtc, delivery.Id }, filter);
        }));

        act.Should().Throw<ArgumentException>();
    }
}

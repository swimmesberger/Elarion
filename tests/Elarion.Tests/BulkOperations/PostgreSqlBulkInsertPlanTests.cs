using AwesomeAssertions;
using Elarion.BulkOperations.PostgreSql;
using Elarion.EntityFrameworkCore.BulkOperations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Elarion.Tests.BulkOperations;

/// <summary>
/// Docker-free tests over model metadata only: which columns the COPY plan selects, and that
/// unsupported mapping shapes / a missing provider fail loud with actionable messages.
/// </summary>
public sealed class PostgreSqlBulkInsertPlanTests {
    private static BulkInsertDbContext CreateOfflineContext(bool withBulkOperations = true) {
        var builder = new DbContextOptionsBuilder<BulkInsertDbContext>()
            .UseNpgsql("Host=localhost;Database=never-connected;Username=u;Password=p");
        if (withBulkOperations) builder.UseElarionPostgreSqlBulkOperations();

        return new BulkInsertDbContext(builder.Options);
    }

    [Fact]
    public void Plan_SkipsIdentityAndStoreDefaultColumns() {
        using var context = CreateOfflineContext();
        var plan = PostgreSqlBulkInsertPlan<BulkAuditEvent>.Create(context);

        plan.CopyCommand.Should().Be("""COPY bulk_audit_events ("Message") FROM STDIN (FORMAT BINARY)""");
    }

    [Fact]
    public void Plan_IncludesClientGeneratedKeyAndAllValueColumns() {
        using var context = CreateOfflineContext();
        var plan = PostgreSqlBulkInsertPlan<BulkOrder>.Create(context);

        // The conventional Guid key is client-generated (no store default), so the caller's value is written.
        plan.CopyCommand.Should().Contain("\"Id\"");
        plan.CopyCommand.Should().Contain("\"Status\"");
        plan.ColumnWriters.Should().HaveCount(11);
        plan.RequiresExactRuntimeType.Should().BeFalse();
    }

    [Fact]
    public void Plan_ForTphType_CarriesDiscriminatorAndExactTypeGuard() {
        using var context = CreateOfflineContext();
        var dogPlan = PostgreSqlBulkInsertPlan<BulkDog>.Create(context);
        var basePlan = PostgreSqlBulkInsertPlan<BulkAnimal>.Create(context);

        dogPlan.CopyCommand.Should().Contain("kind");
        dogPlan.RequiresExactRuntimeType.Should().BeFalse("BulkDog has no derived types");
        basePlan.RequiresExactRuntimeType.Should().BeTrue("BulkAnimal has derived types");
    }

    [Fact]
    public async Task ExecuteInsert_WithoutProvider_ThrowsActionableError() {
        await using var context = CreateOfflineContext(false);
        var act = async () =>
            await context.Orders.ExecuteInsertAsync([new BulkOrder { Id = Guid.CreateVersion7(), Name = "x" }]);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*UseElarionPostgreSqlBulkOperations*");
    }

    [Fact]
    public void Resolver_UnmodeledType_Throws() {
        using var context = CreateOfflineContext();
        var act = () => BulkInsertTargetResolver.Resolve(context.Model, typeof(PostgreSqlBulkInsertPlanTests));

        act.Should().Throw<InvalidOperationException>().WithMessage("*not part of the EF Core model*");
    }

    [Fact]
    public void Resolver_ShadowProperty_Throws() {
        using var context = new UnsupportedShapesDbContext();
        var act = () => BulkInsertTargetResolver.Resolve(context.Model, typeof(ShadowRow));

        act.Should().Throw<NotSupportedException>().WithMessage("*shadow property*");
    }

    [Fact]
    public void Resolver_OwnedNavigation_Throws() {
        using var context = new UnsupportedShapesDbContext();
        var act = () => BulkInsertTargetResolver.Resolve(context.Model, typeof(OwnerRow));

        act.Should().Throw<NotSupportedException>().WithMessage("*owned*");
    }

    [Fact]
    public void Plan_FlattensComplexPropertiesIntoColumns() {
        using var context = CreateOfflineContext();
        var plan = PostgreSqlBulkInsertPlan<BulkShipment>.Create(context);

        // Id + Reference + Address.{Street, Note} + Address.Geo.{Latitude, Longitude}
        plan.ColumnWriters.Should().HaveCount(6);
        plan.CopyCommand.Should().Contain("Street").And.Contain("Latitude");
    }

    [Fact]
    public void Resolver_ComplexCollection_Throws() {
        using var context = new UnsupportedShapesDbContext();
        var act = () => BulkInsertTargetResolver.Resolve(context.Model, typeof(ComplexCollectionRow));

        act.Should().Throw<NotSupportedException>().WithMessage("*complex collection*");
    }

    [Fact]
    public async Task ExecuteInsert_UpsertConflictTargetWithoutUniqueConstraint_Throws() {
        await using var context = CreateOfflineContext();
        var act = async () => await context.Counters.ExecuteInsertAsync(
            [new BulkCounter { Id = Guid.CreateVersion7(), Key = "k", Count = 1 }],
            new BulkInsertOptions {
                OnConflict = BulkInsertConflictBehavior.Update,
                ConflictProperties = [nameof(BulkCounter.Count)]
            });

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*unique*");
    }

    [Fact]
    public async Task ExecuteInsert_UpsertUnknownConflictProperty_Throws() {
        await using var context = CreateOfflineContext();
        var act = async () => await context.Counters.ExecuteInsertAsync(
            [new BulkCounter { Id = Guid.CreateVersion7(), Key = "k", Count = 1 }],
            new BulkInsertOptions {
                OnConflict = BulkInsertConflictBehavior.DoNothing,
                ConflictProperties = ["Nope"]
            });

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not a property*");
    }

    [Fact]
    public void Resolver_TptChild_Throws() {
        using var context = new UnsupportedShapesDbContext();
        var act = () => BulkInsertTargetResolver.Resolve(context.Model, typeof(TptChild));

        act.Should().Throw<NotSupportedException>().WithMessage("*more than one table*");
    }

    private sealed class UnsupportedShapesDbContext() : DbContext(
        new DbContextOptionsBuilder<UnsupportedShapesDbContext>()
            .UseNpgsql("Host=localhost;Database=never-connected;Username=u;Password=p")
            .Options) {
        protected override void OnModelCreating(ModelBuilder modelBuilder) {
            modelBuilder.Entity<ShadowRow>(builder => {
                builder.HasKey(row => row.Id);
                builder.Property<string>("ShadowValue");
            });

            modelBuilder.Entity<OwnerRow>(builder => {
                builder.HasKey(row => row.Id);
                builder.OwnsOne(row => row.Address);
            });

            modelBuilder.Entity<ComplexCollectionRow>(builder => {
                builder.HasKey(row => row.Id);
                builder.ComplexCollection(row => row.Amounts).ToJson();
            });

            modelBuilder.Entity<TptBase>(builder => {
                builder.HasKey(row => row.Id);
                builder.ToTable("tpt_base");
            });
            modelBuilder.Entity<TptChild>(builder => builder.ToTable("tpt_child"));
        }
    }

    private sealed class ShadowRow {
        public Guid Id { get; set; }
    }

    private sealed class OwnerRow {
        public Guid Id { get; set; }

        public Address? Address { get; set; }
    }

    private sealed class Address {
        public string? Street { get; set; }
    }

    private sealed class ComplexCollectionRow {
        public Guid Id { get; set; }

        public required List<Money> Amounts { get; set; }
    }

    private sealed record Money(decimal Amount, string Currency);

    private class TptBase {
        public Guid Id { get; set; }
    }

    private sealed class TptChild : TptBase {
        public string? Extra { get; set; }
    }
}

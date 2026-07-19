using AwesomeAssertions;
using Elarion.Abstractions.Scheduling;
using Elarion.Scheduling;
using Elarion.Scheduling.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Elarion.Tests.Services;

public sealed class SchedulerClaimsRegistrationTests {
    [Fact]
    public void AddElarionScheduler_RegistersLocalCoordinatorByDefault() {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddElarionScheduler();

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IScheduledOccurrenceCoordinator>()
            .Should().BeOfType<LocalScheduledOccurrenceCoordinator>();
    }

    [Fact]
    public async Task LocalCoordinator_AlwaysClaims() {
        var coordinator = new LocalScheduledOccurrenceCoordinator();
        var occurrence = new ScheduledOccurrence {
            JobName = "job",
            DueTimeUtc = DateTimeOffset.UtcNow
        };

        (await coordinator.TryClaimAsync(occurrence, CancellationToken.None)).Should().BeTrue();
        (await coordinator.TryClaimAsync(occurrence, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public void AddElarionSchedulerEntityFrameworkCore_ReplacesCoordinator_EitherOrder() {
        var before = new ServiceCollection();
        before.AddLogging();
        before.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        before.AddDbContext<SchedulerClaimsDbContext>(options => options.UseNpgsql("Host=localhost;Database=elarion"));
        before.AddElarionSchedulerEntityFrameworkCore<SchedulerClaimsDbContext>();
        before.AddElarionScheduler();

        var after = new ServiceCollection();
        after.AddLogging();
        after.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        after.AddDbContext<SchedulerClaimsDbContext>(options => options.UseNpgsql("Host=localhost;Database=elarion"));
        after.AddElarionScheduler();
        after.AddElarionSchedulerEntityFrameworkCore<SchedulerClaimsDbContext>(options =>
            options.ClaimRetention = TimeSpan.FromDays(2));

        foreach (var services in new[] { before, after }) {
            using var provider = services.BuildServiceProvider();
            provider.GetRequiredService<IScheduledOccurrenceCoordinator>()
                .Should().BeOfType<EfCoreScheduledOccurrenceCoordinator<SchedulerClaimsDbContext>>();
            provider.GetServices<IHostedService>()
                .Should().Contain(service => service is SchedulerClaimPurgeService<SchedulerClaimsDbContext>);
        }
    }

    [Fact]
    public void UseElarionSchedulerClaims_Defaults_MapClaimsTable() {
        var modelBuilder = new ModelBuilder(new ConventionSet());
        modelBuilder.UseElarionSchedulerClaims();
        var model = modelBuilder.FinalizeModel();

        var claim = model.FindEntityType(typeof(SchedulerClaimEntity))!;
        claim.GetTableName().Should().Be("elarion_scheduler_claims");
        claim.FindPrimaryKey()!.Properties.Select(property => property.Name)
            .Should().Equal(nameof(SchedulerClaimEntity.JobName), nameof(SchedulerClaimEntity.OccurrenceUtc));
        claim.FindProperty(nameof(SchedulerClaimEntity.OccurrenceUtc))!.GetColumnName().Should().Be("occurrence_utc");
        claim.GetIndexes().Single().GetDatabaseName().Should().Be("ix_elarion_scheduler_claims_purge");
    }

    [Fact]
    public void UseElarionSchedulerClaims_OverridesAndPascalCase_AreApplied() {
        var modelBuilder = new ModelBuilder(new ConventionSet());
        modelBuilder.UseElarionSchedulerClaims("MyClaims", "jobs", false);
        var model = modelBuilder.FinalizeModel();

        var claim = model.FindEntityType(typeof(SchedulerClaimEntity))!;
        claim.GetTableName().Should().Be("MyClaims");
        claim.GetSchema().Should().Be("jobs");
        claim.FindProperty(nameof(SchedulerClaimEntity.OccurrenceUtc))!.GetColumnName().Should().Be("OccurrenceUtc");
        claim.GetIndexes().Single().GetDatabaseName().Should().Be("IX_MyClaims_Purge");
    }
}

/// <summary>EF Core context mapping the scheduler-claims table for registration/integration tests.</summary>
public sealed class SchedulerClaimsDbContext(DbContextOptions<SchedulerClaimsDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        modelBuilder.UseElarionSchedulerClaims();
    }
}

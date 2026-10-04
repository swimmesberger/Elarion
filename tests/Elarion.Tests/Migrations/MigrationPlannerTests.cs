using AwesomeAssertions;
using Elarion.Migrations;
using Xunit;

namespace Elarion.Tests.Migrations;

public sealed class MigrationPlannerTests {
    private sealed class StubCodeMigration(string version) : ICodeMigration {
        public string Version => version;

        public string Description => "stub";

        public Task ExecuteAsync(MigrationStepContext context, CancellationToken cancellationToken) {
            return Task.CompletedTask;
        }
    }

    private static MigrationStepCatalog Catalog(params string[] codeVersions) {
        var source = new CodeMigrationStepSource(codeVersions.Select(v => (ICodeMigration)new StubCodeMigration(v)));
        return MigrationStepCatalog.Build([source], EmptyServiceProvider.Instance);
    }

    private static AppliedMigrationRow Row(int rank, string kind, string? version, string outcome,
        string? checksum = null) {
        return new AppliedMigrationRow {
            InstalledRank = rank,
            Kind = kind,
            Version = version,
            Description = "d",
            StepName = "n",
            Checksum = checksum,
            Outcome = outcome
        };
    }

    [Fact]
    public void CodeSteps_AreOrderedByVersionAndSkippedOnceRecorded() {
        var catalog = Catalog("3", "1", "2");

        var plan = MigrationPlanner.Build(catalog, [Row(1, "code", "1", "applied")]);

        plan.PendingVersioned.Select(p => p.Version!.Text).Should().Equal("2", "3");
        plan.Errors.Should().BeEmpty();
    }

    [Fact]
    public void SatisfiedRow_CountsAsDone() {
        var plan = MigrationPlanner.Build(Catalog("1"), [Row(1, "code", "1", "satisfied")]);

        plan.PendingVersioned.Should().BeEmpty();
    }

    [Fact]
    public void VersionRecordedForAnotherKind_IsAnError() {
        var plan = MigrationPlanner.Build(Catalog("1"), [Row(1, "sql", "1", "applied", "abc")]);

        plan.Errors.Should().ContainSingle().Which.Message.Should().Contain("'sql' step").And.Contain("'code' step");
    }

    [Fact]
    public void BaselineRow_DoesNotPinTheKindOfTheStepAtThatVersion() {
        var plan = MigrationPlanner.Build(Catalog("1", "2"), [Row(1, "baseline", "1", "baseline")]);

        plan.Errors.Should().BeEmpty();
        plan.PendingVersioned.Select(p => p.Version!.Text).Should().Equal("2");
    }

    [Fact]
    public void CodeVersionsThatAreNotNumeric_AreCatalogErrors() {
        Catalog("v1").Errors.Should().ContainSingle().Which.Message.Should().Contain("invalid Version");
    }
}

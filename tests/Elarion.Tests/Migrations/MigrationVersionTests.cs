using AwesomeAssertions;
using Elarion.Migrations;
using Xunit;

namespace Elarion.Tests.Migrations;

public sealed class MigrationVersionTests {
    [Theory]
    [InlineData("1", "1")]
    [InlineData("1_2", "1.2")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("20260713093000", "20260713093000")]
    public void Parses_ToCanonicalDottedText(string input, string expected) {
        MigrationVersion.TryParse(input, out var version).Should().BeTrue();
        version!.Text.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("1..2")]
    [InlineData("1_")]
    [InlineData("_1")]
    [InlineData("1.-2")]
    [InlineData("99999999999999999999")]
    public void RejectsMalformedVersions(string input) {
        MigrationVersion.TryParse(input, out _).Should().BeFalse();
    }

    [Fact]
    public void ComparesSegmentWise_MissingSegmentsAsZero() {
        Parse("1.2").CompareTo(Parse("1.10")).Should().BeNegative();
        Parse("2").CompareTo(Parse("1.9")).Should().BePositive();
        Parse("1").CompareTo(Parse("1.0")).Should().Be(0);
        Parse("1").Should().Be(Parse("1.0"));
        Parse("1").GetHashCode().Should().Be(Parse("1.0").GetHashCode());
    }

    private static MigrationVersion Parse(string text) {
        MigrationVersion.TryParse(text, out var version).Should().BeTrue();
        return version!;
    }
}

using AwesomeAssertions;
using Elarion.Migrations.PostgreSql;
using Xunit;

namespace Elarion.Tests.Migrations;

public sealed class MigrationScriptDiscoveryTests {
    /// <summary>The scenario scripts embedded under <c>Migrations/Scripts/</c>.</summary>
    internal const string ScriptPrefix = "Elarion.Tests.Migrations.Scripts.";

    [Fact]
    public void DiscoversVersionedAndRepeatableScripts_Sorted() {
        var set = Discover("Basic.");

        set.Errors.Should().BeEmpty();
        set.Versioned.Select(s => s.Version!.Text).Should().Equal("1", "2");
        set.Versioned.Select(s => s.ScriptName).Should().Equal("V1__create_customers.sql", "V2__add_email.sql");
        set.Versioned[0].Description.Should().Be("create customers");
        set.Repeatable.Should().ContainSingle().Which.ScriptName.Should().Be("R__customer_view.sql");
        set.Versioned.Should().OnlyContain(s => !s.NoTransaction);
    }

    [Fact]
    public void ChecksumIsStableAcrossLineEndingsAndBom() {
        const string content = "SELECT 1;\nSELECT 2;\n";
        var lf = MigrationChecksum.Compute(MigrationChecksum.Normalize(content));
        var crlf = MigrationChecksum.Compute(MigrationChecksum.Normalize(content.Replace("\n", "\r\n")));
        var bom = MigrationChecksum.Compute(MigrationChecksum.Normalize("\uFEFF" + content));

        crlf.Should().Be(lf);
        bom.Should().Be(lf);
        MigrationChecksum.Compute(MigrationChecksum.Normalize("SELECT 3;\n")).Should().NotBe(lf);
    }

    [Fact]
    public void ParsesNoTransactionDirective() {
        var set = Discover("NoTx.");

        set.Errors.Should().BeEmpty();
        set.Versioned.Single(s => s.Version!.Text == "2").NoTransaction.Should().BeTrue();
        set.Versioned.Single(s => s.Version!.Text == "1").NoTransaction.Should().BeFalse();
    }

    [Fact]
    public void DirectiveBelowTheLeadingCommentBlockIsIgnored() {
        MigrationScriptDiscovery.TryParseDirectives("SELECT 1;\n-- elarion: no-transaction\n", out var noTransaction, out _)
            .Should().BeTrue();
        noTransaction.Should().BeFalse();
    }

    [Fact]
    public void UnknownDirectiveFailsClosed() {
        var set = Discover("BadDirective.");

        set.Errors.Should().ContainSingle().Which.Message.Should().Contain("no-transactoin");
    }

    [Fact]
    public void MalformedScriptNameFailsClosed_NamingTheResource() {
        var set = Discover("Malformed.");

        set.Errors.Should().ContainSingle();
        set.Errors[0].Message.Should().Contain("X__oops.sql");
    }

    [Fact]
    public void DuplicateVersionsFail_IncludingTrailingZeroEquivalence() {
        var set = Discover("Duplicate.");

        set.Errors.Should().ContainSingle().Which.Message.Should().Contain("Duplicate migration version 1");
    }

    [Theory]
    [InlineData("V1__init.sql", "1", "init", false)]
    [InlineData("V1_2__add_users.sql", "1.2", "add users", false)]
    [InlineData("V20260713093000__add_devices.sql", "20260713093000", "add devices", false)]
    [InlineData("R__customer_view.sql", null, "customer view", true)]
    public void ParsesScriptNames(string scriptName, string? version, string description, bool repeatable) {
        MigrationScriptDiscovery.TryParseScriptName(scriptName, out var parsed, out var parsedDescription, out _)
            .Should().BeTrue();
        (parsed?.Text).Should().Be(version);
        parsedDescription.Should().Be(description);
        (parsed is null).Should().Be(repeatable);
    }

    [Theory]
    [InlineData("V__missing.sql")]
    [InlineData("V1.sql")]
    [InlineData("V1__.sql")]
    [InlineData("R__.sql")]
    [InlineData("Vx__desc.sql")]
    [InlineData("notes.sql")]
    public void RejectsMalformedScriptNames(string scriptName) {
        MigrationScriptDiscovery.TryParseScriptName(scriptName, out _, out _, out var error).Should().BeFalse();
        error.Should().Contain(scriptName);
    }

    internal static MigrationScriptSet Discover(string scenario) =>
        MigrationScriptDiscovery.Discover([
            new MigrationScriptSource(typeof(MigrationScriptDiscoveryTests).Assembly, ScriptPrefix + scenario),
        ]);
}

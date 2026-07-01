using AwesomeAssertions;
using Elarion.Abstractions.Substitution;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Elarion.Tests.Abstractions;

public sealed class VariableSubstitutionTests {
    private static IVariableSource Source(params (string Key, string? Value)[] values) =>
        new DictionaryVariableSource(values.ToDictionary(pair => pair.Key, pair => pair.Value));

    [Theory]
    [InlineData("${a}", true)]
    [InlineData("${a:-b}", true)]
    [InlineData("literal", false)]
    [InlineData("${a}x", false)]
    [InlineData("x${a}", false)]
    public void IsPlaceholder_DetectsWholeValuePlaceholders(string value, bool expected) {
        VariableSubstitution.IsPlaceholder(value).Should().Be(expected);
    }

    [Theory]
    [InlineData("pre${a}post", true)]
    [InlineData("plain", false)]
    public void ContainsPlaceholder_DetectsEmbeddedPlaceholders(string value, bool expected) {
        VariableSubstitution.ContainsPlaceholder(value).Should().Be(expected);
    }

    [Fact]
    public void Resolve_LiteralReturnedUnchanged() {
        VariableSubstitution.Resolve("just-a-literal", Source()).Should().Be("just-a-literal");
    }

    [Fact]
    public void Resolve_PlaceholderResolvedFromSource() {
        VariableSubstitution.Resolve("${greeting}", Source(("greeting", "hello"))).Should().Be("hello");
    }

    [Fact]
    public void Resolve_UsesInlineDefaultWhenKeyMissing() {
        VariableSubstitution.Resolve("${greeting:-hi}", Source()).Should().Be("hi");
    }

    [Fact]
    public void Resolve_UsesInlineDefaultWhenValueIsBlank() {
        VariableSubstitution.Resolve("${greeting:-hi}", Source(("greeting", "  "))).Should().Be("hi");
    }

    [Fact]
    public void Resolve_ConfiguredValueOverridesDefault() {
        VariableSubstitution.Resolve("${greeting:-hi}", Source(("greeting", "hey"))).Should().Be("hey");
    }

    [Fact]
    public void Resolve_ReturnsNullWhenUnresolvedAndNoDefault() {
        VariableSubstitution.Resolve("${greeting}", Source()).Should().BeNull();
    }

    [Theory]
    [InlineData("${}")]
    [InlineData("${:-fallback}")]
    public void Resolve_EmptyKeyThrowsFormatException(string value) {
        var act = () => VariableSubstitution.Resolve(value, Source());

        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void ResolveRequired_ThrowsWhenUnresolved() {
        var act = () => VariableSubstitution.ResolveRequired("${greeting}", Source());

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Substitute_ReturnsTemplateUnchangedWhenNoPlaceholders() {
        VariableSubstitution.Substitute("plain text", Source()).Should().Be("plain text");
    }

    [Fact]
    public void Substitute_ReplacesEmbeddedPlaceholders() {
        var source = Source(("host", "db.local"), ("port", "5432"));

        VariableSubstitution.Substitute("postgres://${host}:${port}/app", source)
            .Should().Be("postgres://db.local:5432/app");
    }

    [Fact]
    public void Substitute_UsesInlineDefaults() {
        VariableSubstitution.Substitute("${scheme:-https}://${host:-localhost}", Source(("host", "example.com")))
            .Should().Be("https://example.com");
    }

    [Fact]
    public void Substitute_ThrowsWhenPlaceholderUnresolvedAndNoDefault() {
        var act = () => VariableSubstitution.Substitute("value=${missing}", Source());

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Substitute_EmitsUnterminatedPlaceholderVerbatim() {
        VariableSubstitution.Substitute("price is ${dollars", Source()).Should().Be("price is ${dollars");
    }

    [Fact]
    public void Substitute_RejectsNestedPlaceholder() {
        var act = () => VariableSubstitution.Substitute("db=${a:${b}}", Source(("a", "x"), ("b", "y")));

        act.Should().Throw<FormatException>().WithMessage("*nested*");
    }

    [Fact]
    public void Resolve_RejectsNestedPlaceholder() {
        var act = () => VariableSubstitution.Resolve("${a:-${b}}", Source(("b", "y")));

        act.Should().Throw<FormatException>().WithMessage("*nested*");
    }

    [Fact]
    public void ConfigurationVariableSource_ResolvesFromConfiguration() {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Jobs:Interval"] = "30s" })
            .Build();
        var source = new ConfigurationVariableSource(configuration);

        VariableSubstitution.Resolve("${Jobs:Interval:-15s}", source).Should().Be("30s");
        VariableSubstitution.Resolve("${Jobs:Missing:-15s}", source).Should().Be("15s");
    }

    private sealed class DictionaryVariableSource(Dictionary<string, string?> values) : IVariableSource {
        public bool TryGetValue(string key, out string? value) => values.TryGetValue(key, out value);
    }
}

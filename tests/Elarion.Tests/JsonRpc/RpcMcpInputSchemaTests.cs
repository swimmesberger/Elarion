using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AwesomeAssertions;
using Elarion.JsonRpc.Mcp;
using Xunit;

namespace Elarion.Tests.JsonRpc;

/// <summary>Tests for <see cref="RpcMcpInputSchema"/> — MCP tool input-schema generation + description injection.</summary>
public sealed class RpcMcpInputSchemaTests {
    private sealed record SchemaCommand {
        public required string DisplayName { get; init; }
        public required string Secret { get; init; }
    }

    private sealed record ConstrainedCommand {
        [System.ComponentModel.DataAnnotations.StringLength(50, MinimumLength = 2)]
        public required string DisplayName { get; init; }

        [System.ComponentModel.DataAnnotations.Range(1, 10)]
        public required int Priority { get; init; }
    }

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    [Fact]
    public void Build_InjectsDescriptionByClrName_UnderCamelCasePolicy() {
        var schema = RpcMcpInputSchema.Build(
            typeof(SchemaCommand),
            Options,
            [new RpcMcpParameterDescriptor("DisplayName", "Human-readable client name.")]);

        var properties = schema.GetProperty("properties");

        // The JSON property name is camelCased ("displayName") yet the description — keyed by the CLR name
        // "DisplayName" — still attaches. This is the naming-policy-robustness guarantee.
        properties.GetProperty("displayName").GetProperty("description").GetString()
            .Should().Be("Human-readable client name.");

        // A property with no descriptor carries no description.
        properties.GetProperty("secret").TryGetProperty("description", out _).Should().BeFalse();
    }

    [Fact]
    public void Build_WithoutDescriptions_ProducesObjectSchema() {
        var schema = RpcMcpInputSchema.Build(typeof(SchemaCommand), Options, []);

        schema.GetProperty("type").GetString().Should().Be("object");
        schema.GetProperty("properties").TryGetProperty("displayName", out _).Should().BeTrue();
    }

    [Fact]
    public void Build_InjectsDataAnnotationConstraints_ComposedWithDescriptions() {
        var schema = RpcMcpInputSchema.Build(
            typeof(ConstrainedCommand),
            Options,
            [new RpcMcpParameterDescriptor("DisplayName", "Shown in the tool list.")]);

        var properties = schema.GetProperty("properties");

        // Constraint keywords compose with (not instead of) the descriptor-supplied description.
        var displayName = properties.GetProperty("displayName");
        displayName.GetProperty("minLength").GetInt32().Should().Be(2);
        displayName.GetProperty("maxLength").GetInt32().Should().Be(50);
        displayName.GetProperty("description").GetString().Should().Be("Shown in the tool list.");

        var priority = properties.GetProperty("priority");
        priority.GetProperty("minimum").GetDecimal().Should().Be(1);
        priority.GetProperty("maximum").GetDecimal().Should().Be(10);
    }

    [Fact]
    public void Build_WithoutDescriptions_StillInjectsDataAnnotationConstraints() {
        var schema = RpcMcpInputSchema.Build(typeof(ConstrainedCommand), Options, []);

        schema.GetProperty("properties").GetProperty("displayName")
            .GetProperty("maxLength").GetInt32().Should().Be(50);
    }
}

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
}

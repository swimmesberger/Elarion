using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AwesomeAssertions;
using Elarion;
using Elarion.AspNetCore.Mcp;
using Elarion.JsonRpc;
using Elarion.JsonRpc.Mcp;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Elarion.Tests.AspNetCore;

/// <summary>Tests for the Elarion MCP adapter — tool projection, result mapping, and JSON-RPC-endpoint independence.</summary>
public sealed class ElarionMcpTests {
    private sealed record CreateClientCommand {
        public required string DisplayName { get; init; }
        public required string Secret { get; init; }
    }

    private sealed record CreateClientResponse(Guid Id);

    private sealed record PurgeCommand;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    // The metadata table only ever contains MCP-surfaced methods (the generator filters out JSON-RPC-only ones),
    // so a JSON-RPC-only handler like "admin.purge" simply never appears here.
    private static RpcMcpMetadataSource BuildMetadata() =>
        new([
            new RpcMcpMethodMetadata {
                MethodName = "clients.create",
                RequestType = typeof(CreateClientCommand),
                ToolName = "create_client",
                Description = "Creates a client.",
                Parameters = [new RpcMcpParameterDescriptor("DisplayName", "The display name.")],
            },
        ]);

    [Fact]
    public void BuildTools_ProjectsMetadata() {
        var tools = ElarionMcpServiceExtensions.BuildTools(
            BuildMetadata(), Options, new ElarionMcpOptions { ServerName = "Test" });

        tools.Should().ContainSingle();
        var tool = tools[0].ProtocolTool;
        tool.Name.Should().Be("create_client");          // ToolName override honored
        tool.Description.Should().Be("Creates a client.");

        var properties = tool.InputSchema.GetProperty("properties");
        properties.GetProperty("displayName").GetProperty("description").GetString()
            .Should().Be("The display name.");
        properties.GetProperty("secret").TryGetProperty("description", out _).Should().BeFalse();
    }

    [Fact]
    public void BuildTools_DerivesToolName_WhenNoOverride() {
        var metadata = new RpcMcpMetadataSource([
            new RpcMcpMethodMetadata { MethodName = "clients.create", RequestType = typeof(CreateClientCommand) },
            new RpcMcpMethodMetadata { MethodName = "admin.purge", RequestType = typeof(PurgeCommand) },
        ]);

        var tools = ElarionMcpServiceExtensions.BuildTools(
            metadata, Options, new ElarionMcpOptions { ServerName = "Test" });

        tools.Select(t => t.ProtocolTool.Name)
            .Should().BeEquivalentTo(["clients_create", "admin_purge"]); // dots → underscores
    }

    [Fact]
    public void BuildTools_Throws_OnDuplicateToolName() {
        // "a.b" and "a_b" both resolve to the tool name "a_b" under the default transform.
        var metadata = new RpcMcpMetadataSource([
            new RpcMcpMethodMetadata { MethodName = "a.b", RequestType = typeof(PurgeCommand) },
            new RpcMcpMethodMetadata { MethodName = "a_b", RequestType = typeof(PurgeCommand) },
        ]);

        var act = () => ElarionMcpServiceExtensions.BuildTools(
            metadata, Options, new ElarionMcpOptions { ServerName = "Test" });

        act.Should().Throw<InvalidOperationException>().WithMessage("*a_b*");
    }

    [Fact]
    public void ToCallToolResult_Success_WrapsResultText() {
        var tool = new ElarionMcpServerTool("clients.create", new Tool { Name = "create_client" }, includeErrorDetails: true);

        var result = tool.ToCallToolResult(
            new RpcToolResult { IsError = false, Text = "{\"id\":\"x\"}" }, Options);

        result.IsError.Should().NotBe(true);
        result.Content.Should().ContainSingle()
            .Which.Should().BeOfType<TextContentBlock>()
            .Which.Text.Should().Be("{\"id\":\"x\"}");
    }

    [Fact]
    public void ToCallToolResult_Error_SetsIsErrorAndStructuredContent() {
        var tool = new ElarionMcpServerTool("clients.create", new Tool { Name = "create_client" }, includeErrorDetails: true);

        var result = tool.ToCallToolResult(
            new RpcToolResult { IsError = true, Text = "client not found", ErrorCode = -32001 }, Options);

        result.IsError.Should().Be(true);
        result.Content.Should().ContainSingle()
            .Which.Should().BeOfType<TextContentBlock>()
            .Which.Text.Should().Be("client not found");
        result.StructuredContent.Should().NotBeNull();
        result.StructuredContent!.Value.GetProperty("code").GetInt32().Should().Be(-32001);
    }

    [Fact]
    public void ToCallToolResult_Error_OmitsStructuredContent_WhenDisabled() {
        var tool = new ElarionMcpServerTool("clients.create", new Tool { Name = "create_client" }, includeErrorDetails: false);

        var result = tool.ToCallToolResult(
            new RpcToolResult { IsError = true, Text = "nope", ErrorCode = -32001 }, Options);

        result.IsError.Should().Be(true);
        result.StructuredContent.Should().BeNull();
    }

    [Fact]
    public void AddElarionJsonRpcDispatcher_RegistersFrozenUsableDispatcher() {
        var services = new ServiceCollection();
        services.AddElarionJsonRpcDispatcher(
            Options, d => d.MapHandler<CreateClientCommand, CreateClientResponse>("clients.create"));

        using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<JsonRpcDispatcher>();

        // MethodNames reads the frozen registry — proves the dispatcher was registered, mapped, and frozen.
        dispatcher.MethodNames.Should().Contain("clients.create");
    }

    [Fact]
    public void AddElarionMcp_IsSelfContained_WithoutJsonRpcEndpoint() {
        var services = new ServiceCollection();

        var builder = services.AddElarionMcp(
            BuildMetadata(),
            Options,
            d => d.MapHandler<CreateClientCommand, CreateClientResponse>("clients.create"),
            o => o.ServerName = "MyApp");

        builder.Should().NotBeNull();
        using var provider = services.BuildServiceProvider();
        // No AddJsonRpc / AddElarionJsonRpcDispatcher anywhere: MCP owns a dedicated dispatcher and never registers
        // the unkeyed /rpc JsonRpcDispatcher.
        provider.GetRequiredService<ElarionMcpOptions>().ServerName.Should().Be("MyApp");
        provider.GetRequiredService<McpDispatcher>().Inner.MethodNames.Should().Contain("clients.create");
        provider.GetService<JsonRpcDispatcher>().Should().BeNull();
    }

    [Fact]
    public void AddElarionMcp_Throws_WhenServerNameMissing() {
        var act = () => new ServiceCollection().AddElarionMcp(BuildMetadata(), Options, d => d, _ => { });

        act.Should().Throw<InvalidOperationException>();
    }
}

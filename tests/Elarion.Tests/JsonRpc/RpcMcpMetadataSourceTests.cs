using AwesomeAssertions;
using Elarion.JsonRpc.Mcp;
using Xunit;

namespace Elarion.Tests.JsonRpc;

/// <summary>Tests for the generated-metadata lookup <see cref="RpcMcpMetadataSource"/>.</summary>
public sealed class RpcMcpMetadataSourceTests {
    [Fact]
    public void Get_LooksUpByMethodName_CaseInsensitive() {
        var source = new RpcMcpMetadataSource([
            new RpcMcpMethodMetadata {
                MethodName = "clients.create",
                RequestType = typeof(object),
                ToolName = "create_client",
                Description = "Creates a client.",
                Parameters = [new RpcMcpParameterDescriptor("DisplayName", "The name.")],
            },
            new RpcMcpMethodMetadata { MethodName = "admin.purge", RequestType = typeof(object), Enabled = false },
        ]);

        source.Get("clients.create").Should().NotBeNull();
        source.Get("CLIENTS.CREATE").Should().NotBeNull(); // OrdinalIgnoreCase mirrors the dispatcher
        source.Get("clients.create")!.ToolName.Should().Be("create_client");
        source.Get("clients.create")!.Parameters.Should().ContainSingle()
            .Which.PropertyName.Should().Be("DisplayName");
        source.Get("admin.purge")!.Enabled.Should().BeFalse();
        source.Get("does.not.exist").Should().BeNull();
        source.All.Should().HaveCount(2);
    }

    [Fact]
    public void RpcMcpMethodMetadata_DefaultsEnabledTrueWithNoParameters() {
        var metadata = new RpcMcpMethodMetadata { MethodName = "x.y", RequestType = typeof(object) };

        metadata.Enabled.Should().BeTrue();
        metadata.Parameters.Should().BeEmpty();
        metadata.ToolName.Should().BeNull();
        metadata.Description.Should().BeNull();
    }
}

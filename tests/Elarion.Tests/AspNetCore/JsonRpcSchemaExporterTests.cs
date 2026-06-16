using System.Text.Json;
using AwesomeAssertions;
using Elarion.AspNetCore;
using Xunit;

namespace Elarion.Tests.AspNetCore;

public sealed class JsonRpcSchemaExporterTests {
    [Fact]
    public void Generate_FrozenDispatcher_ExportsRegisteredMethods() {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var dispatcher = new JsonRpcDispatcher(options)
            .Map<PingRequest, PingResponse>(
                "sample.ping",
                (request, _, _) => Task.FromResult(RpcResult<PingResponse>.Success(new PingResponse(request.Message))))
            .Freeze();

        var schema = JsonRpcSchemaExporter.Generate(dispatcher, options);

        schema.Should().Contain("\"sample.ping\"");
        schema.Should().Contain("\"params\"");
        schema.Should().Contain("\"result\"");
        schema.Should().Contain("\"message\"");
    }

    [Fact]
    public void Generate_UnfrozenDispatcher_ThrowsActionableError() {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var dispatcher = new JsonRpcDispatcher(options)
            .Map<PingRequest, PingResponse>(
                "sample.ping",
                (request, _, _) => Task.FromResult(RpcResult<PingResponse>.Success(new PingResponse(request.Message))));

        var act = () => JsonRpcSchemaExporter.Generate(dispatcher, options);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*dispatcher is not frozen*");
    }

    [Fact]
    public void Generate_EmptyDispatcher_ThrowsActionableError() {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var dispatcher = new JsonRpcDispatcher(options).Freeze();

        var act = () => JsonRpcSchemaExporter.Generate(dispatcher, options);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*no registered methods*");
    }

    private sealed record PingRequest(string Message);

    private sealed record PingResponse(string Message);
}

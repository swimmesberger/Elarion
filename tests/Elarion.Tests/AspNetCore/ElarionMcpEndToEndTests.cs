using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AwesomeAssertions;
using Elarion;
using Elarion.Abstractions;
using Elarion.AspNetCore;
using Elarion.AspNetCore.Mcp;
using Elarion.JsonRpc;
using Elarion.JsonRpc.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Elarion.Tests.AspNetCore;

/// <summary>
/// End-to-end test: boots a real Kestrel host with <c>MapElarionMcp()</c> (and deliberately no <c>MapJsonRpc()</c>),
/// then drives it with a real MCP client over Streamable HTTP — proving the MCP surface works on its own.
/// </summary>
public sealed class ElarionMcpEndToEndTests {
    private sealed record EchoCommand {
        public required string Name { get; init; }
    }

    private sealed record EchoResponse(string Greeting);

    private sealed class EchoHandler : IHandler<EchoCommand, Result<EchoResponse>> {
        public ValueTask<Result<EchoResponse>> HandleAsync(EchoCommand request, CancellationToken ct) =>
            request.Name == "boom"
                ? ValueTask.FromResult<Result<EchoResponse>>(AppError.NotFound("no such name"))
                : ValueTask.FromResult<Result<EchoResponse>>(new EchoResponse($"Hello {request.Name}"));
    }

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    [Fact]
    public async Task McpServer_ListsAndCallsTools_OverHttp_WithoutJsonRpcEndpoint() {
        var ct = TestContext.Current.CancellationToken;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0"); // ephemeral port
        builder.Logging.ClearProviders();

        builder.Services.AddScoped<IHandler<EchoCommand, Result<EchoResponse>>, EchoHandler>();
        builder.Services.AddJsonRpc(Options, d => d.MapHandler<EchoCommand, EchoResponse>("echo"));
        builder.Services.AddElarionMcp(
            new RpcMcpMetadataSource([
                new RpcMcpMethodMetadata {
                    MethodName = "echo",
                    RequestType = typeof(EchoCommand),
                    Description = "Echoes a greeting.",
                },
            ]),
            Options,
            o => o.ServerName = "Test");

        await using var app = builder.Build();
        app.MapElarionMcp(); // /mcp only — MapJsonRpc() is intentionally NOT called
        await app.StartAsync(ct);

        try {
            var baseAddress = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First();

            await using var transport = new HttpClientTransport(
                new HttpClientTransportOptions {
                    Endpoint = new Uri($"{baseAddress}/mcp"),
                    TransportMode = HttpTransportMode.StreamableHttp,
                },
                NullLoggerFactory.Instance);

            await using var client = await McpClient.CreateAsync(
                transport,
                new McpClientOptions { ClientInfo = new Implementation { Name = "test", Version = "1.0" } },
                NullLoggerFactory.Instance,
                ct);

            var tools = await client.ListToolsAsync(cancellationToken: ct);
            var echo = tools.Should().ContainSingle().Subject;
            echo.Name.Should().Be("echo");
            echo.Description.Should().Be("Echoes a greeting.");

            var success = await client.CallToolAsync(
                "echo", new Dictionary<string, object?> { ["name"] = "World" }, cancellationToken: ct);
            success.IsError.Should().NotBe(true);
            success.Content.OfType<TextContentBlock>().Single().Text.Should().Contain("Hello World");

            var failure = await client.CallToolAsync(
                "echo", new Dictionary<string, object?> { ["name"] = "boom" }, cancellationToken: ct);
            failure.IsError.Should().Be(true);
            failure.Content.OfType<TextContentBlock>().Single().Text.Should().Be("no such name");
        } finally {
            await app.StopAsync(ct);
        }
    }
}

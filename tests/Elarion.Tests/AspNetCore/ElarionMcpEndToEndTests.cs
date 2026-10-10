using System.Text.Json;
using AwesomeAssertions;
using Elarion;
using Elarion.Abstractions;
using Elarion.Abstractions.Dispatch;
using Elarion.Abstractions.Serialization;
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
        public ValueTask<Result<EchoResponse>> HandleAsync(EchoCommand request, CancellationToken ct) {
            return request.Name == "boom"
                ? ValueTask.FromResult<Result<EchoResponse>>(AppError.NotFound("no such name"))
                : ValueTask.FromResult<Result<EchoResponse>>(new EchoResponse($"Hello {request.Name}"));
        }
    }

    [Fact]
    public async Task McpServer_RefusesCrossSiteTextPlainPost_With415() {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await StartMcpOnlyHostAsync(ct);

        try {
            var baseAddress = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First();
            using var client = new HttpClient { BaseAddress = new Uri(baseAddress) };

            // A cross-site no-cors fetch with a text/plain body that is an MCP envelope: Accept is a CORS-safelisted
            // header, so the browser sends this without a preflight and with the site's cookies. The MCP transport
            // refuses the non-JSON content type itself (like /rpc does), so MapElarionMcp needs no extra check.
            using var formPost = new HttpRequestMessage(HttpMethod.Post, "/mcp") {
                Content = new StringContent(
                    """{"jsonrpc":"2.0","method":"tools/call","params":{"name":"echo","arguments":{"name":"x"}},"id":1,"x":"="}""",
                    System.Text.Encoding.UTF8,
                    "text/plain")
            };
            formPost.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
            formPost.Headers.TryAddWithoutValidation("Origin", "https://attacker.example");
            formPost.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");
            var response = await client.SendAsync(formPost, ct);

            response.StatusCode.Should().Be(System.Net.HttpStatusCode.UnsupportedMediaType);
        }
        finally {
            await app.StopAsync(ct);
        }
    }

    [Fact]
    public async Task McpServer_ListsAndCallsTools_OverHttp_WithoutJsonRpcEndpoint() {
        var ct = TestContext.Current.CancellationToken;
        await using var app = await StartMcpOnlyHostAsync(ct);

        try {
            var baseAddress = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First();

            await using var transport = new HttpClientTransport(
                new HttpClientTransportOptions {
                    Endpoint = new Uri($"{baseAddress}/mcp"),
                    TransportMode = HttpTransportMode.StreamableHttp
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
        }
        finally {
            await app.StopAsync(ct);
        }
    }

    private static async Task<WebApplication> StartMcpOnlyHostAsync(CancellationToken ct) {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0"); // ephemeral port
        builder.Logging.ClearProviders();

        builder.Services.AddScoped<IHandler<EchoCommand, Result<EchoResponse>>, EchoHandler>();
        // The plain test DTOs are not in a source-gen context, so opt the canonical serializer into reflection.
        builder.Services.ConfigureElarionJson(o => o.EnableReflectionFallback = true);

        // Both transports must share ONE registration delegate — the bus is a single shared singleton.
        static HandlerDispatcher RegisterHandlers(HandlerDispatcher dispatcher) {
            return dispatcher.Map<EchoCommand, EchoResponse>("echo");
        }

        builder.Services.AddElarionJsonRpc(RegisterHandlers);
        builder.Services.AddElarionMcp(
            new RpcMcpMetadataSource([
                new RpcMcpMethodMetadata {
                    MethodName = "echo",
                    RequestType = typeof(EchoCommand),
                    Description = "Echoes a greeting."
                }
            ]),
            RegisterHandlers,
            o => o.ServerName = "Test");

        var app = builder.Build();
        app.MapElarionMcp(); // /mcp only — MapJsonRpc() is intentionally NOT called
        await app.StartAsync(ct);
        return app;
    }
}

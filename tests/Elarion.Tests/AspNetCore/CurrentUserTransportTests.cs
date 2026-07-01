using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Dispatch;
using Elarion.Abstractions.Identity;
using Elarion.AspNetCore;
using Elarion.AspNetCore.Identity;
using Elarion.JsonRpc;
using Elarion.JsonRpc.Mcp;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Elarion.Tests.AspNetCore;

/// <summary>
/// Regression for the feedback bug: a handler injecting <c>ICurrentUser</c> must see the authenticated
/// principal under <b>every</b> transport — plain HTTP endpoints, JSON-RPC (single and batch), and MCP —
/// even though the dispatchers run each call in a fresh DI child scope. No <c>IHttpContextAccessor</c>,
/// no <c>AsyncLocal</c>: JSON-RPC/HTTP inherit the request-scope snapshot; MCP seeds from the per-message
/// principal.
/// </summary>
public sealed class CurrentUserTransportTests {
    [Fact]
    public async Task HttpEndpoint_HandlerInRequestScope_ReadsCurrentUser() {
        await using var provider = BuildProvider();
        using var requestScope = provider.CreateScope();
        var context = CreateContext(requestScope.ServiceProvider, body: "", userId: "user-http");
        await SeedRequestScopeAsync(context);

        // A minimal-API [HttpEndpoint] resolves its handler from the request scope (no child scope) — exactly
        // how the generated lambda binds [FromServices] — so it reads the middleware-built snapshot directly.
        var handler = requestScope.ServiceProvider.GetRequiredService<IHandler<WhoAmIQuery, Result<WhoAmIResponse>>>();
        var result = await handler.HandleAsync(new WhoAmIQuery(), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Value.UserId.Should().Be("user-http");
        result.Value.Authenticated.Should().BeTrue();
    }

    [Fact]
    public async Task JsonRpc_Single_HandlerReadsAuthenticatedCurrentUser() {
        await using var provider = BuildProvider();
        // The endpoint captures ctx.User into the dispatch context, so no middleware is required here.
        var context = CreateContext(
            provider,
            """{ "jsonrpc": "2.0", "method": "whoami", "params": {}, "id": 1 }""",
            "user-42");

        await JsonRpcEndpoint.HandleRpc(context);

        var result = ReadResponse(context).RootElement.GetProperty("result");
        result.GetProperty("userId").GetString().Should().Be("user-42");
        result.GetProperty("authenticated").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task JsonRpc_Batch_EachItemReadsAuthenticatedCurrentUser() {
        await using var provider = BuildProvider();
        var context = CreateContext(
            provider,
            """
            [
              { "jsonrpc": "2.0", "method": "whoami", "params": {}, "id": 1 },
              { "jsonrpc": "2.0", "method": "whoami", "params": {}, "id": 2 }
            ]
            """,
            "user-99");

        await JsonRpcEndpoint.HandleRpc(context);

        var response = ReadResponse(context);
        response.RootElement.GetArrayLength().Should().Be(2);
        foreach (var item in response.RootElement.EnumerateArray()) {
            item.GetProperty("result").GetProperty("userId").GetString().Should().Be("user-99");
        }
    }

    [Fact]
    public async Task Mcp_ToolCall_HandlerReadsAuthenticatedCurrentUser() {
        await using var provider = BuildProvider();
        var dispatcher = provider.GetRequiredService<JsonRpcDispatcher>();
        // MCP has no request scope; ElarionMcpServerTool captures RequestContext.User into the context, and the
        // current-user initializer seeds ICurrentUser from that captured principal — the same path as JSON-RPC.
        var context = new DispatchScopeContext();
        context.Set<ClaimsPrincipal>(Authenticated("user-mcp"));

        var result = await RpcToolInvoker.InvokeAsync(
            dispatcher.Registry, HandlerTransports.Mcp, "whoami",
            JsonSerializer.SerializeToElement(new { }, SerializerOptions), provider, SerializerOptions, context,
            TestContext.Current.CancellationToken);

        result.IsError.Should().BeFalse();
        using var doc = JsonDocument.Parse(result.Text);
        doc.RootElement.GetProperty("userId").GetString().Should().Be("user-mcp");
        doc.RootElement.GetProperty("authenticated").GetBoolean().Should().BeTrue();
    }

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web) {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private static ServiceProvider BuildProvider() {
        var dispatcher = new JsonRpcDispatcher(SerializerOptions)
            .Map<WhoAmIQuery, WhoAmIResponse>("whoami")
            .Freeze();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.ClearProviders());
        services.AddElarionJsonRpc();
        services.AddSingleton(dispatcher);
        services.AddElarionCurrentUser();
        services.AddScoped<IHandler<WhoAmIQuery, Result<WhoAmIResponse>>, WhoAmIHandler>();
        return services.BuildServiceProvider();
    }

    private static ClaimsPrincipal Authenticated(string userId) =>
        new(new ClaimsIdentity([new Claim("sub", userId)], authenticationType: "test"));

    private static DefaultHttpContext CreateContext(IServiceProvider requestScope, string body, string userId) {
        var context = new DefaultHttpContext {
            RequestServices = requestScope,
            User = Authenticated(userId),
        };
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        return context;
    }

    // Simulates app.UseElarionCurrentUser(): the middleware seeds the current-user snapshot into the request
    // scope (context.RequestServices), where the HTTP handler resolves it.
    private static Task SeedRequestScopeAsync(HttpContext context) =>
        new CurrentUserMiddleware(_ => Task.CompletedTask).InvokeAsync(context);

    private static JsonDocument ReadResponse(HttpContext context) {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        return JsonDocument.Parse(reader.ReadToEnd());
    }

    private sealed record WhoAmIQuery;

    private sealed record WhoAmIResponse(string UserId, bool Authenticated);

    private sealed class WhoAmIHandler(ICurrentUser user) : IHandler<WhoAmIQuery, Result<WhoAmIResponse>> {
        public ValueTask<Result<WhoAmIResponse>> HandleAsync(WhoAmIQuery request, CancellationToken ct) =>
            ValueTask.FromResult<Result<WhoAmIResponse>>(new WhoAmIResponse(user.UserId, user.IsAuthenticated));
    }
}

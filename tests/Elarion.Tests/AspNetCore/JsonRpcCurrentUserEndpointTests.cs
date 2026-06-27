using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Identity;
using Elarion.AspNetCore;
using Elarion.AspNetCore.Identity;
using Elarion.JsonRpc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Elarion.Tests.AspNetCore;

/// <summary>
/// End-to-end regression for the feedback bug: a JSON-RPC handler that injects <c>ICurrentUser</c> must see
/// the authenticated principal, even though <see cref="JsonRpcEndpoint"/> dispatches each call in a fresh
/// child scope. Drives the real endpoint with a <see cref="DefaultHttpContext"/> carrying an authenticated
/// user — no <c>IHttpContextAccessor</c> involved.
/// </summary>
public sealed class JsonRpcCurrentUserEndpointTests {
    [Fact]
    public async Task HandleRpc_Single_HandlerReadsAuthenticatedCurrentUser() {
        await using var provider = BuildProvider();
        var context = CreateContext(
            provider,
            """{ "jsonrpc": "2.0", "method": "whoami", "params": {}, "id": 1 }""",
            "user-42");

        await JsonRpcEndpoint.HandleRpc(context);

        var response = ReadResponse(context);
        var result = response.RootElement.GetProperty("result");
        result.GetProperty("userId").GetString().Should().Be("user-42");
        result.GetProperty("authenticated").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task HandleRpc_Batch_EachItemReadsAuthenticatedCurrentUser() {
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

    private static ServiceProvider BuildProvider() {
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        };
        var dispatcher = new JsonRpcDispatcher(jsonOptions)
            .MapHandler<WhoAmIQuery, WhoAmIResponse>("whoami")
            .Freeze();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.ClearProviders());
        services.AddElarionJsonRpc(options => options.SerializerOptions = jsonOptions);
        services.AddSingleton(dispatcher);
        services.AddElarionCurrentUser();
        services.AddScoped<IHandler<WhoAmIQuery, Result<WhoAmIResponse>>, WhoAmIHandler>();
        return services.BuildServiceProvider();
    }

    private static DefaultHttpContext CreateContext(IServiceProvider provider, string body, string userId) {
        var context = new DefaultHttpContext {
            RequestServices = provider,
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", userId)], authenticationType: "test")),
        };
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static JsonDocument ReadResponse(HttpContext context) {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        return JsonDocument.Parse(reader.ReadToEnd());
    }

    private sealed record WhoAmIQuery;

    private sealed record WhoAmIResponse(string UserId, bool Authenticated);

    private sealed class WhoAmIHandler(ICurrentUser user)
        : IHandler<WhoAmIQuery, Result<WhoAmIResponse>> {
        public ValueTask<Result<WhoAmIResponse>> HandleAsync(WhoAmIQuery request, CancellationToken ct) =>
            ValueTask.FromResult<Result<WhoAmIResponse>>(
                new WhoAmIResponse(user.UserId, user.IsAuthenticated));
    }
}

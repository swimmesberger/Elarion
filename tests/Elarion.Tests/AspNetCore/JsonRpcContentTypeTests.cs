using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.AspNetCore;
using Elarion.JsonRpc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Elarion.Tests.AspNetCore;

/// <summary>
/// <c>/rpc</c> only accepts a JSON <c>Content-Type</c> by default. A cross-site <c>&lt;form enctype="text/plain"&gt;</c>
/// whose field spells out a JSON-RPC envelope (or a <c>no-cors</c> fetch with an untyped body) is a CORS simple
/// request that carries the site's cookies, so dispatching it would be CSRF for a cookie-authenticated host.
/// </summary>
public sealed class JsonRpcContentTypeTests {
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web) {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    private const string Envelope = """{ "jsonrpc": "2.0", "method": "things.create", "params": {}, "id": 1 }""";

    [Theory]
    [InlineData("text/plain")]
    [InlineData("text/plain; charset=utf-8")]
    [InlineData("application/x-www-form-urlencoded")]
    [InlineData("multipart/form-data; boundary=x")]
    [InlineData(null)]
    public async Task NonJsonContentType_RefusedWith415_WithoutDispatching(string? contentType) {
        var calls = new CallCounter();
        await using var provider = BuildProvider(calls);
        var context = CreateContext(provider, contentType);

        await JsonRpcEndpoint.HandleRpc(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status415UnsupportedMediaType);
        using var doc = ReadResponse(context);
        var error = doc.RootElement.GetProperty("error");
        error.GetProperty("code").GetInt32().Should().Be(-32600);
        error.GetProperty("data").GetProperty("code").GetString().Should().Be(RpcErrorCodes.InvalidRequest);
        error.GetProperty("message").GetString().Should().Contain("application/json");
        calls.Count.Should().Be(0);
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("application/vnd.example+json")]
    public async Task JsonContentType_Dispatched(string contentType) {
        var calls = new CallCounter();
        await using var provider = BuildProvider(calls);
        var context = CreateContext(provider, contentType);

        await JsonRpcEndpoint.HandleRpc(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        using var doc = ReadResponse(context);
        doc.RootElement.GetProperty("result").GetProperty("value").GetString().Should().Be("created");
        calls.Count.Should().Be(1);
    }

    [Fact]
    public async Task RequireJsonContentTypeDisabled_DispatchesTextPlain() {
        var calls = new CallCounter();
        await using var provider = BuildProvider(calls, o => o.RequireJsonContentType = false);
        var context = CreateContext(provider, "text/plain");

        await JsonRpcEndpoint.HandleRpc(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        calls.Count.Should().Be(1);
    }

    [Fact]
    public void RequireJsonContentType_DefaultsToTrue() {
        new JsonRpcOptions().RequireJsonContentType.Should().BeTrue();
    }

    [Fact]
    public async Task CrossSiteTextPlainFormPost_OverHttp_Refused_AndJsonAccepted() {
        var ct = TestContext.Current.CancellationToken;
        var calls = new CallCounter();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(BuildDispatcher(calls));
        builder.Services.AddElarionJsonRpc();

        await using var app = builder.Build();
        app.MapElarionJsonRpc();
        await app.StartAsync(ct);

        try {
            var baseAddress = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First();
            using var client = new HttpClient { BaseAddress = new Uri(baseAddress) };

            // What a browser sends for <form method="post" enctype="text/plain" action=".../rpc"> on another site
            // whose single field is named '{"jsonrpc":"2.0","method":"things.create","params":{},"id":1,"x":"' and
            // valued '"}': the body is name=value, which is valid JSON.
            using var formPost = new HttpRequestMessage(HttpMethod.Post, "/rpc") {
                Content = new StringContent(
                    """{"jsonrpc":"2.0","method":"things.create","params":{},"id":1,"x":"="}""",
                    Encoding.UTF8,
                    "text/plain")
            };
            formPost.Headers.TryAddWithoutValidation("Origin", "https://attacker.example");
            formPost.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");
            formPost.Headers.TryAddWithoutValidation("Cookie", "session=victim");
            var refused = await client.SendAsync(formPost, ct);

            refused.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
            calls.Count.Should().Be(0);

            using var jsonPost = new HttpRequestMessage(HttpMethod.Post, "/rpc") {
                Content = new StringContent(Envelope, Encoding.UTF8, "application/json")
            };
            var accepted = await client.SendAsync(jsonPost, ct);

            accepted.StatusCode.Should().Be(HttpStatusCode.OK);
            (await accepted.Content.ReadAsStringAsync(ct)).Should().Contain("created");
            calls.Count.Should().Be(1);
        }
        finally {
            await app.StopAsync(ct);
        }
    }

    private sealed class CallCounter {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Increment() {
            Interlocked.Increment(ref _count);
        }
    }

    private sealed record ProbeCommand;

    private sealed record ProbeResponse(string Value);

    private static JsonRpcDispatcher BuildDispatcher(CallCounter calls) {
        return new JsonRpcDispatcher(SerializerOptions)
            .MapDelegate<ProbeCommand, ProbeResponse>(
                "things.create",
                (_, _, _) => {
                    calls.Increment();
                    return new ValueTask<Result<ProbeResponse>>(
                        Result<ProbeResponse>.Success(new ProbeResponse("created")));
                })
            .Freeze();
    }

    private static ServiceProvider BuildProvider(CallCounter calls, Action<JsonRpcOptions>? configure = null) {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.ClearProviders());
        services.AddElarionJsonRpc(configure);
        services.AddSingleton(BuildDispatcher(calls));
        return services.BuildServiceProvider();
    }

    private static DefaultHttpContext CreateContext(IServiceProvider provider, string? contentType) {
        var context = new DefaultHttpContext {
            RequestServices = provider
        };
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = contentType;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(Envelope));
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static JsonDocument ReadResponse(HttpContext context) {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8);
        return JsonDocument.Parse(reader.ReadToEnd());
    }
}

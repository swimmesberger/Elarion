using System.Net;
using AwesomeAssertions;
using Elarion.Abstractions.Coordination;
using Elarion.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Elarion.Tests.AspNetCore;

/// <summary>
/// The ADR-0050 role-holder proxy, end-to-end over two real Kestrel hosts: a "holder" app and a
/// "web node" whose proxy forwards prefixed requests to it through a fake lease.
/// </summary>
public sealed class RoleHolderProxyTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NotHolder_ProxiesPrefixedRequests_MarkedAndWithAuthPassthrough() {
        await using var holder = await StartHolderAsync();
        var lease = new FakeRoleLease { IsHeld = false, CurrentHolderAddress = holder.Address };
        await using var node = await StartWebNodeAsync(lease);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/quotes/ELN");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer token-1");
        var response = await node.Client.SendAsync(request, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        // Served by the holder, carrying the loop marker and the caller's auth header verbatim —
        // same app on both ends, so authorization needs no translation.
        (await response.Content.ReadAsStringAsync(Ct))
            .Should().Be("holder:ELN:proxied=True:auth=Bearer token-1");
    }

    [Fact]
    public async Task Holder_ServesLocally_TheProxyIsOneLeaseCheck() {
        await using var holder = await StartHolderAsync();
        var lease = new FakeRoleLease { IsHeld = true, CurrentHolderAddress = holder.Address };
        await using var node = await StartWebNodeAsync(lease);

        (await node.Client.GetStringAsync("/quotes/ELN", Ct)).Should().Be("local:ELN");
    }

    [Fact]
    public async Task PartitionProxy_ResolvesTheTargetFromEachRequestKey() {
        await using var holder = await StartHolderAsync();
        var partition = new FakeRolePartition(holder.Address);
        await using var node = await StartPartitionedWebNodeAsync(partition);

        (await node.Client.GetStringAsync("/quotes/ELN", Ct))
            .Should().StartWith("holder:ELN:proxied=True");
        (await node.Client.GetStringAsync("/quotes/LOCAL", Ct)).Should().Be("local:LOCAL");
        partition.ResolvedKeys.Should().BeEquivalentTo("ELN", "LOCAL");
    }

    [Fact]
    public async Task ScopedPartitionProxy_UsesTheSameTwoComponentHashAsActorPlacement() {
        await using var holder = await StartHolderAsync();
        var partition = new FakeRolePartition(holder.Address);
        const string actorName = "Order";
        const string actorKey = "order-42";
        await using var node = await StartPartitionedWebNodeAsync(partition, actorName);

        var response = await node.Client.GetAsync($"/quotes/{actorKey}", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        partition.ResolvedScopedKeys.Should().ContainSingle().Which.Should().Be((actorName, actorKey));
        partition.ResolvedPartitions.Should().ContainSingle().Which.Should().Be(
            RolePartitionHash.GetPartition(actorName, actorKey, partition.PartitionCount));
    }

    [Fact]
    public async Task NonPrefixedPaths_ServeLocally_EvenWhenNotHolder() {
        await using var holder = await StartHolderAsync();
        var lease = new FakeRoleLease { IsHeld = false, CurrentHolderAddress = holder.Address };
        await using var node = await StartWebNodeAsync(lease);

        (await node.Client.GetStringAsync("/other", Ct)).Should().Be("local:other");
    }

    [Fact]
    public async Task AlreadyProxiedRequest_IsNeverReForwarded() {
        // Mid-failover: the addressed instance is no longer the holder. One hop, then a bounded 503.
        await using var holder = await StartHolderAsync();
        var lease = new FakeRoleLease { IsHeld = false, CurrentHolderAddress = holder.Address };
        await using var node = await StartWebNodeAsync(lease);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/quotes/ELN");
        request.Headers.TryAddWithoutValidation("Elarion-Role-Proxied", "actors");
        var response = await node.Client.SendAsync(request, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Headers.TryGetValues("Retry-After", out var retryAfter).Should().BeTrue();
        retryAfter.Should().ContainSingle().Which.Should().Be("5");
    }

    [Fact]
    public async Task UnknownHolderAddress_Returns503WithGuidance() {
        var lease = new FakeRoleLease { IsHeld = false, CurrentHolderAddress = null };
        await using var node = await StartWebNodeAsync(lease);

        var response = await node.Client.GetAsync("/quotes/ELN", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("AddElarionInstanceAddress");
    }

    [Fact]
    public async Task UnreachableHolder_Returns503() {
        var lease = new FakeRoleLease { IsHeld = false, CurrentHolderAddress = "http://127.0.0.1:1" };
        await using var node = await StartWebNodeAsync(lease);

        var response = await node.Client.GetAsync("/quotes/ELN", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("unreachable");
    }

    [Fact]
    public async Task RequestBodies_AreForwarded() {
        await using var holder = await StartHolderAsync();
        var lease = new FakeRoleLease { IsHeld = false, CurrentHolderAddress = holder.Address };
        await using var node = await StartWebNodeAsync(lease);

        var response = await node.Client.PostAsync("/quotes/echo", new StringContent("streamed body"), Ct);

        (await response.Content.ReadAsStringAsync(Ct)).Should().Be("STREAMED BODY");
    }

    [Fact]
    public async Task NoLeaseRegistered_TheProxyInstallsNothing() {
        // Single-instance mode: UseElarionRoleHolderProxy is a no-op — the pipeline stays untouched.
        await using var node = await StartWebNodeAsync(lease: null);

        (await node.Client.GetStringAsync("/quotes/ELN", Ct)).Should().Be("local:ELN");
    }

    [Fact]
    public async Task ServerAddressProvider_ResolvesWildcardHosts() {
        var services = new ServiceCollection();
        services.AddSingleton<IServer>(new FakeServer("http://0.0.0.0:5210"));
        services.AddElarionInstanceAddress();
        await using var provider = services.BuildServiceProvider();

        var address = provider.GetRequiredService<IInstanceAddressProvider>().GetInstanceAddress();

        // Non-deterministic host (the machine's own IPv4), deterministic everything else.
        address.Should().NotBeNull();
        address.Should().StartWith("http://").And.EndWith(":5210");
        address.Should().NotContain("0.0.0.0");
    }

    [Fact]
    public async Task ServerAddressProvider_KeepsConcreteHosts_AndExplicitAddressWins() {
        var services = new ServiceCollection();
        services.AddSingleton<IServer>(new FakeServer("http://10.1.2.3:8080"));
        services.AddElarionInstanceAddress();
        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IInstanceAddressProvider>().GetInstanceAddress()
            .Should().Be("http://10.1.2.3:8080");

        var explicitServices = new ServiceCollection();
        explicitServices.AddElarionInstanceAddress("https://edge.example.com/");
        await using var explicitProvider = explicitServices.BuildServiceProvider();
        explicitProvider.GetRequiredService<IInstanceAddressProvider>().GetInstanceAddress()
            .Should().Be("https://edge.example.com");
    }

    [Fact]
    public async Task BlackHoledHolder_AnswersBounded503_InsteadOfHanging() {
        // Regression: no connect/overall timeout — a holder that accepted the connection but never answered
        // (crashed node, dropped SYNs) hung the proxied request forever instead of the documented 503.
        var lease = new FakeRoleLease { IsHeld = false, CurrentHolderAddress = "http://127.0.0.1:9" };
        var middleware = new RoleHolderProxyMiddleware(
            lease,
            [new PathString("/quotes")],
            new HttpMessageInvoker(new NeverRespondingHandler()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            responseHeadersTimeout: TimeSpan.FromMilliseconds(200));

        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/quotes/ELN";
        context.Response.Body = new MemoryStream();

        var invoke = middleware.InvokeAsync(context, static _ => Task.CompletedTask);
        var completed = await Task.WhenAny(invoke, Task.Delay(TimeSpan.FromSeconds(10), Ct));
        completed.Should().BeSameAs(invoke, "the proxy must answer within its response-headers timeout, not hang");
        await invoke;

        context.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        context.Response.Headers.RetryAfter.ToString().Should().Be("5");
        context.Response.Body.Position = 0;
        (await new StreamReader(context.Response.Body).ReadToEndAsync(Ct)).Should().Contain("did not respond");
    }

    [Fact]
    public async Task ConnectionNominatedHeaders_AreStrippedBothWays() {
        // RFC 9110 §7.6.1: headers nominated by the Connection header are hop-by-hop and must be stripped in
        // addition to the static list — on the forwarded request and on the relayed response.
        var handler = new CapturingHandler(static () => {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK) {
                Content = new StringContent("ok"),
            };
            response.Headers.TryAddWithoutValidation("Connection", "X-Hop-Response");
            response.Headers.TryAddWithoutValidation("X-Hop-Response", "secret");
            response.Headers.TryAddWithoutValidation("X-Keep-Response", "kept");
            return response;
        });
        var lease = new FakeRoleLease { IsHeld = false, CurrentHolderAddress = "http://127.0.0.1:9" };
        var middleware = new RoleHolderProxyMiddleware(
            lease,
            [new PathString("/quotes")],
            new HttpMessageInvoker(handler),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/quotes/ELN";
        context.Request.Headers.Connection = "X-Hop-Request";
        context.Request.Headers["X-Hop-Request"] = "secret";
        context.Request.Headers["X-Keep-Request"] = "kept";
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context, static _ => Task.CompletedTask);

        handler.SentHeaderNames.Should().NotContain("Connection");
        handler.SentHeaderNames.Should().NotContain("X-Hop-Request");
        handler.SentHeaderNames.Should().Contain("X-Keep-Request");

        context.Response.Headers.Should().NotContainKey("Connection");
        context.Response.Headers.Should().NotContainKey("X-Hop-Response");
        context.Response.Headers["X-Keep-Response"].ToString().Should().Be("kept");
    }

    private sealed class NeverRespondingHandler : HttpMessageHandler {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class CapturingHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler {
        public List<string> SentHeaderNames { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) {
            SentHeaderNames.AddRange(request.Headers.Select(static header => header.Key));
            return Task.FromResult(respond());
        }
    }

    private sealed class FakeRoleLease : IRoleLease {
        public string Role => "actors";

        public bool IsHeld { get; set; }

        public string? CurrentHolder { get; set; }

        public string? CurrentHolderAddress { get; set; }
    }

    private sealed class FakeRolePartition(string holderAddress) : IRolePartition {
        public string Name => "actors";
        public int PartitionCount => 2;
        public List<string> ResolvedKeys { get; } = [];
        public List<(string Scope, string Key)> ResolvedScopedKeys { get; } = [];
        public List<int> ResolvedPartitions { get; } = [];

        public RolePartitionTarget Resolve(string affinityKey) {
            ResolvedKeys.Add(affinityKey);
            var held = affinityKey == "LOCAL";
            return new(held ? 0 : 1, $"actors:partition-{(held ? 0 : 1)}", held, null, holderAddress);
        }

        public RolePartitionTarget Resolve(string affinityScope, string affinityKey) {
            ResolvedScopedKeys.Add((affinityScope, affinityKey));
            var resolved = RolePartitionHash.GetPartition(affinityScope, affinityKey, PartitionCount);
            ResolvedPartitions.Add(resolved);
            var held = resolved == 0;
            return new(resolved, $"actors:partition-{resolved}", held, null, holderAddress);
        }
    }

    private sealed class FakeServer(params string[] addresses) : IServer {
        public IFeatureCollection Features { get; } = BuildFeatures(addresses);

        public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken)
            where TContext : notnull => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose() { }

        private static IFeatureCollection BuildFeatures(string[] addresses) {
            var features = new FeatureCollection();
            var feature = new ServerAddressesFeature();
            foreach (var address in addresses) {
                feature.Addresses.Add(address);
            }

            features.Set<IServerAddressesFeature>(feature);
            return features;
        }
    }

    private static async Task<TestHost> StartHolderAsync() {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.MapGet("/quotes/{symbol}", static (string symbol, HttpRequest request) =>
            $"holder:{symbol}:proxied={request.Headers.ContainsKey("Elarion-Role-Proxied")}"
            + $":auth={request.Headers.Authorization.ToString()}");
        app.MapPost("/quotes/echo", static async (HttpRequest request) => {
            using var reader = new StreamReader(request.Body);
            return (await reader.ReadToEndAsync()).ToUpperInvariant();
        });
        await app.StartAsync(Ct);
        return new TestHost(app);
    }

    private static async Task<TestHost> StartWebNodeAsync(FakeRoleLease? lease) {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        if (lease is not null) {
            builder.Services.AddKeyedSingleton<IRoleLease>("actors", lease);
        }

        var app = builder.Build();
        app.UseElarionRoleHolderProxy("actors", "/quotes");
        app.MapGet("/quotes/{symbol}", static (string symbol) => $"local:{symbol}");
        app.MapGet("/other", static () => "local:other");
        await app.StartAsync(Ct);
        return new TestHost(app);
    }

    private static async Task<TestHost> StartPartitionedWebNodeAsync(
        IRolePartition partition,
        string? affinityScope = null) {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddKeyedSingleton<IRolePartition>(partition.Name, partition);

        var app = builder.Build();
        if (affinityScope is null) {
            app.UseElarionPartitionHolderProxy(
                partition.Name,
                static context => context.Request.Path.Value?.Split('/').LastOrDefault(),
                "/quotes");
        }
        else {
            app.UseElarionPartitionHolderProxy(
                partition.Name,
                affinityScope,
                static context => context.Request.Path.Value?.Split('/').LastOrDefault(),
                "/quotes");
        }
        app.MapGet("/quotes/{symbol}", static (string symbol) => $"local:{symbol}");
        await app.StartAsync(Ct);
        return new TestHost(app);
    }

    private sealed class TestHost(WebApplication app) : IAsyncDisposable {
        public string Address { get; } = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();

        public HttpClient Client { get; } = new() {
            BaseAddress = new Uri(app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First())
        };

        public async ValueTask DisposeAsync() {
            Client.Dispose();
            await app.DisposeAsync();
        }
    }
}

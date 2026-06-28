using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Dispatch;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.Dispatch;

/// <summary>
/// Unit tests for the transport-neutral named request/reply dispatcher (the bus): per-route transport-flag
/// filtering, DI-resolved and delegate-backed registration, dispatch, and the freeze contract.
/// </summary>
public sealed class HandlerDispatcherTests {
    private sealed record Ping(string Value);

    private sealed record Pong(string Value);

    private sealed class PingHandler : IHandler<Ping, Result<Pong>> {
        public ValueTask<Result<Pong>> HandleAsync(Ping request, CancellationToken ct) =>
            ValueTask.FromResult<Result<Pong>>(new Pong($"pong:{request.Value}"));
    }

    [Fact]
    public void TryGetRoute_RespectsTransportFlags() {
        var registry = new HandlerDispatcher()
            .Map<Ping, Pong>("rpc.only", HandlerTransports.JsonRpc)
            .Map<Ping, Pong>("mcp.only", HandlerTransports.Mcp)
            .Map<Ping, Pong>("both", HandlerTransports.All)
            .Freeze();

        registry.TryGetRoute("rpc.only", HandlerTransports.JsonRpc, out _).Should().BeTrue();
        registry.TryGetRoute("rpc.only", HandlerTransports.Mcp, out _).Should().BeFalse();
        registry.TryGetRoute("mcp.only", HandlerTransports.Mcp, out _).Should().BeTrue();
        registry.TryGetRoute("mcp.only", HandlerTransports.JsonRpc, out _).Should().BeFalse();
        registry.TryGetRoute("both", HandlerTransports.JsonRpc, out _).Should().BeTrue();
        registry.TryGetRoute("both", HandlerTransports.Mcp, out _).Should().BeTrue();
    }

    [Fact]
    public void RoutesFor_ReturnsOnlyTheFlaggedSubset() {
        var registry = new HandlerDispatcher()
            .Map<Ping, Pong>("rpc.only", HandlerTransports.JsonRpc)
            .Map<Ping, Pong>("mcp.only", HandlerTransports.Mcp)
            .Map<Ping, Pong>("both", HandlerTransports.All)
            .Freeze();

        registry.RoutesFor(HandlerTransports.JsonRpc).Select(route => route.Name)
            .Should().BeEquivalentTo("rpc.only", "both");
        registry.RoutesFor(HandlerTransports.Mcp).Select(route => route.Name)
            .Should().BeEquivalentTo("mcp.only", "both");
        registry.AllRoutes.Select(route => route.Name)
            .Should().BeEquivalentTo("rpc.only", "mcp.only", "both");
    }

    [Fact]
    public async Task DispatchAsync_ResolvesDecoratedHandlerFromScope_AndBoxesResult() {
        var registry = new HandlerDispatcher().Map<Ping, Pong>("ping").Freeze();
        using var provider = new ServiceCollection()
            .AddScoped<IHandler<Ping, Result<Pong>>, PingHandler>()
            .BuildServiceProvider();
        using var scope = provider.CreateScope();

        var result = await registry.DispatchAsync(
            "ping", new Ping("hi"), scope.ServiceProvider, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeOfType<Pong>().Which.Value.Should().Be("pong:hi");
    }

    [Fact]
    public async Task DispatchAsync_UnknownName_ReturnsNotFound() {
        var registry = new HandlerDispatcher().Map<Ping, Pong>("ping").Freeze();
        using var provider = new ServiceCollection().BuildServiceProvider();

        var result = await registry.DispatchAsync(
            "absent", new Ping("hi"), provider, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.NotFound);
    }

    [Fact]
    public async Task MapDelegate_RegistersDelegateBackedRoute_WithoutDependencyInjection() {
        var registry = new HandlerDispatcher()
            .MapDelegate<Ping, Pong>(
                "echo",
                (request, _, _) => ValueTask.FromResult<Result<Pong>>(new Pong(request.Value)))
            .Freeze();
        using var provider = new ServiceCollection().BuildServiceProvider();

        var result = await registry.DispatchAsync(
            "echo", new Ping("raw"), provider, TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeOfType<Pong>().Which.Value.Should().Be("raw");
    }

    [Fact]
    public void Reads_BeforeFreeze_Throw() {
        var registry = new HandlerDispatcher().Map<Ping, Pong>("ping");

        var act = () => registry.TryGetRoute("ping", out _);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Freeze()*");
    }

    [Fact]
    public void Map_AfterFreeze_Throws() {
        var registry = new HandlerDispatcher().Map<Ping, Pong>("ping").Freeze();

        var act = () => registry.Map<Ping, Pong>("late");

        act.Should().Throw<InvalidOperationException>().WithMessage("*after Freeze()*");
    }
}

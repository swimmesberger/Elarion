using System.Security.Claims;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Connections.Simulation;
using Elarion.Idempotency;
using Elarion.Identity;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.Connections;

/// <summary>
/// The deterministic allocation gate for the low-alloc dispatch profile (ADR-0066): after warmup, a
/// per-connection-scope dispatch of a singleton, telemetry-free handler must allocate nothing per message.
/// BenchmarkDotNet stays the manual measurement tool; this runs in CI so a regression (a new per-message
/// allocation on the hot path) fails the normal test suite instead of surfacing months later in a profile.
/// </summary>
public sealed class ConnectionDispatchAllocationTests {
    private sealed record Echo(int Value) : IQuery<Echo, int>;

    private sealed class EchoHandler : IHandler<Echo, Result<int>> {
        public ValueTask<Result<int>> HandleAsync(Echo request, CancellationToken ct) {
            Result<int> result = request.Value + 1;
            return new ValueTask<Result<int>>(result);
        }
    }

    [Fact]
    public async Task LowAllocProfileDispatch_AllocatesNothingPerMessageAfterWarmup() {
        // The generator's emission for [Handler(Scope = Singleton)] + [HandlerTelemetry(None)]: singleton
        // concrete, singleton chain, no observability decorator — plus the framework scope initializers a
        // production connection carries.
        using var provider = new ServiceCollection()
            .AddSingleton<EchoHandler>()
            .AddSingleton<IHandler<Echo, Result<int>>>(static sp => sp.GetRequiredService<EchoHandler>())
            .AddElarionClaimsCurrentUser()
            .AddElarionIdempotency()
            .BuildServiceProvider();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "gate-user")], "test"));
        await using var invoker = new ConnectionHandlerInvoker(
            provider,
            new SimulatedClientConnection("gate-user", principal: principal),
            new ConnectionHandlerInvokerOptions { ScopeMode = ConnectionDispatchScopeMode.PerConnection });
        var request = new Echo(7);

        for (var i = 0; i < 1_000; i++)
            (await invoker.InvokeAsync(request, TestContext.Current.CancellationToken))
                .Value.Should().Be(8);

        const int iterations = 10_000;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
            await invoker.InvokeAsync(request, TestContext.Current.CancellationToken);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Zero expected; the slack absorbs one-off runtime infrastructure (tiering, GC bookkeeping) without
        // letting a real per-message allocation (≥ 24 B each, ≥ 240 KB total) sneak past.
        var bytesPerOp = allocated / (double)iterations;
        bytesPerOp.Should().BeLessThan(1.0,
            $"the low-alloc dispatch profile must not allocate per message, but measured {bytesPerOp:F2} B/op");
    }
}

using System.Security.Claims;
using BenchmarkDotNet.Attributes;
using Elarion.Abstractions.Connections;
using Elarion.Connections;
using Elarion.Connections.Simulation;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.Benchmarks.Connections;

/// <summary>
/// Registry lifecycle costs — the late-authentication question in numbers: an authenticated
/// register/unregister cycle (connect-time auth, the baseline) against an anonymous register → promote →
/// unregister cycle (framed late auth). The delta is what one atomic identity promotion costs: snapshot
/// validation and cloning, the CAS publish, and the promotion observer notification.
/// </summary>
[MemoryDiagnoser]
public class ConnectionLifecycleBenchmarks {
    private ServiceProvider _provider = null!;
    private IClientConnectionRegistry _registry = null!;
    private ClaimsPrincipal _authenticated = null!;
    private ClaimsPrincipal _anonymous = null!;
    private ClientConnectionIdentity _promotion = null!;
    private long _sequence;

    [GlobalSetup]
    public void Setup() {
        _provider = new ServiceCollection().AddElarionConnections().BuildServiceProvider();
        _registry = _provider.GetRequiredService<IClientConnectionRegistry>();
        _authenticated = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "bench-device")], authenticationType: "bench"));
        _anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        // Promotion inputs are cloned by the registry per call, so one template instance is reusable.
        _promotion = new ClientConnectionIdentity {
            Principal = _authenticated,
            PrincipalId = "bench-device",
        };
    }

    [GlobalCleanup]
    public async Task Cleanup() => await _provider.DisposeAsync();

    [Benchmark(Baseline = true)]
    public async Task RegisterUnregister_ConnectTimeAuth() {
        var connectionId = NextConnectionId();
        await _registry.RegisterAsync(new SimulatedClientConnection(
            principalId: "bench-device", connectionId: connectionId, principal: _authenticated));
        await _registry.UnregisterAsync(connectionId);
    }

    [Benchmark]
    public async Task RegisterPromoteUnregister_LateAuth() {
        var connectionId = NextConnectionId();
        await _registry.RegisterAsync(new SimulatedClientConnection(
            connectionId: connectionId, principal: _anonymous));
        await _registry.PromoteAsync(connectionId, _promotion);
        await _registry.UnregisterAsync(connectionId);
    }

    private string NextConnectionId() => "bench-" + Interlocked.Increment(ref _sequence);
}

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.JsonRpc;
using Elarion.JsonRpc.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.JsonRpc;

/// <summary>
/// Tests for the transport-neutral per-call scope-seeding rail: <see cref="DispatchScopeContext"/>,
/// <see cref="ServiceProviderDispatchScopeExtensions.CreateDispatchScope"/>, and the
/// <see cref="IDispatchScopeInitializer"/> seam carried through <see cref="RpcToolInvoker"/> (the MCP path).
/// </summary>
public sealed class DispatchScopeTests {
    [Fact]
    public void DispatchScopeContext_SetThenTryGet_RoundTrips() {
        var context = new DispatchScopeContext();
        context.Set("hello");

        context.TryGet<string>(out var value).Should().BeTrue();
        value.Should().Be("hello");
    }

    [Fact]
    public void DispatchScopeContext_TryGet_MissingType_ReturnsFalse() {
        var context = new DispatchScopeContext();

        context.TryGet<int>(out var value).Should().BeFalse();
        value.Should().Be(0);
    }

    [Fact]
    public void DispatchScopeContext_Set_OverwritesExistingEntry() {
        var context = new DispatchScopeContext();
        context.Set("first");
        context.Set("second");

        context.TryGet<string>(out var value).Should().BeTrue();
        value.Should().Be("second");
    }

    [Fact]
    public void CreateDispatchScope_RunsRegisteredInitializers_AgainstChildScope() {
        var services = new ServiceCollection()
            .AddScoped<ProbeState>()
            .AddSingleton<IDispatchScopeInitializer, SeedProbeInitializer>()
            .BuildServiceProvider();
        var context = new DispatchScopeContext();
        context.Set("seeded-value");

        using var scope = services.CreateDispatchScope(context);

        scope.ServiceProvider.GetRequiredService<ProbeState>().Value.Should().Be("seeded-value");
    }

    [Fact]
    public void CreateDispatchScope_NoInitializers_IsNoOp() {
        var services = new ServiceCollection()
            .AddScoped<ProbeState>()
            .BuildServiceProvider();

        using var scope = services.CreateDispatchScope();

        scope.ServiceProvider.GetRequiredService<ProbeState>().Value.Should().BeNull();
    }

    [Fact]
    public async Task RpcToolInvoker_SeedsScopedStateFromContext_VisibleToHandler() {
        var dispatcher = new JsonRpcDispatcher(Options).MapHandler<ProbeQuery, ProbeResponse>("probe").Freeze();
        var services = new ServiceCollection()
            .AddScoped<ProbeState>()
            .AddSingleton<IDispatchScopeInitializer, SeedProbeInitializer>()
            .AddScoped<IHandler<ProbeQuery, Result<ProbeResponse>>, ProbeHandler>()
            .BuildServiceProvider();
        var context = new DispatchScopeContext();
        context.Set("from-context");

        var result = await RpcToolInvoker.InvokeAsync(
            dispatcher, "probe", JsonSerializer.SerializeToElement(new { }, Options), services, context,
            TestContext.Current.CancellationToken);

        result.IsError.Should().BeFalse();
        using var doc = JsonDocument.Parse(result.Text);
        doc.RootElement.GetProperty("seen").GetString().Should().Be("from-context");
    }

    [Fact]
    public void AddDispatchScopeInherited_CopiesOriginatingInstanceIntoChildScope_WithoutRebuilding() {
        var services = new ServiceCollection()
            .AddScoped<CopyableProbe>()
            .AddDispatchScopeInherited<CopyableProbe>()
            .BuildServiceProvider();
        // Originating "request" scope with a built instance.
        using var request = services.CreateScope();
        var origin = request.ServiceProvider.GetRequiredService<CopyableProbe>();
        origin.Value = "built-once";

        using var call = request.ServiceProvider.CreateDispatchScope(inheritFrom: request.ServiceProvider);

        var copied = call.ServiceProvider.GetRequiredService<CopyableProbe>();
        copied.Value.Should().Be("built-once");
        copied.CopyCount.Should().Be(1);
        origin.CopyCount.Should().Be(0);
    }

    [Fact]
    public void AddDispatchScopeInherited_NoInheritFrom_IsNoOp() {
        var services = new ServiceCollection()
            .AddScoped<CopyableProbe>()
            .AddDispatchScopeInherited<CopyableProbe>()
            .BuildServiceProvider();

        // No originating scope (e.g. MCP) — nothing to copy.
        using var call = services.CreateDispatchScope();

        var probe = call.ServiceProvider.GetRequiredService<CopyableProbe>();
        probe.Value.Should().BeNull();
        probe.CopyCount.Should().Be(0);
    }

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private sealed class ProbeState {
        public string? Value { get; set; }
    }

    private sealed class CopyableProbe : IScopeCopyable<CopyableProbe> {
        public string? Value { get; set; }
        public int CopyCount { get; private set; }

        public void CopyFrom(CopyableProbe source) {
            Value = source.Value;
            CopyCount++;
        }
    }

    private sealed class SeedProbeInitializer : IDispatchScopeInitializer {
        public void Initialize(IServiceProvider callScope, IServiceProvider? inheritFrom, DispatchScopeContext context) {
            if (context.TryGet<string>(out var value)) {
                callScope.GetRequiredService<ProbeState>().Value = value;
            }
        }
    }

    private sealed record ProbeQuery;

    private sealed record ProbeResponse(string Seen);

    private sealed class ProbeHandler(ProbeState state) : IHandler<ProbeQuery, Result<ProbeResponse>> {
        public ValueTask<Result<ProbeResponse>> HandleAsync(ProbeQuery request, CancellationToken ct) =>
            ValueTask.FromResult<Result<ProbeResponse>>(new ProbeResponse(state.Value ?? "<unseeded>"));
    }
}

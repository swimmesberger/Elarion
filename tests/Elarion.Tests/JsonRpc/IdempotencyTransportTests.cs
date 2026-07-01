using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Dispatch;
using Elarion.Abstractions.Idempotency;
using Elarion.Idempotency;
using Elarion.JsonRpc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.JsonRpc;

/// <summary>
/// Tests the JSON-RPC idempotency wiring: the <c>idempotent</c> flag flows from the route to the exported
/// schema, and a per-call key at <c>params._meta</c> is seeded into the scope for an idempotent operation.
/// </summary>
public sealed class IdempotencyTransportTests {
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private sealed record ProbeCommand {
        public string? Name { get; init; }
    }

    private sealed record ProbeResponse(string Key);

    [Fact]
    public void GetRegisteredMethods_CarriesIdempotentFlag() {
        var dispatcher = new JsonRpcDispatcher(Options)
            .Map<ProbeCommand, ProbeResponse>("things.create", idempotent: true)
            .Map<ProbeCommand, ProbeResponse>("things.get")
            .Freeze();

        var methods = dispatcher.GetRegisteredMethods().ToDictionary(m => m.MethodName, m => m.Idempotent);

        methods["things.create"].Should().BeTrue();
        methods["things.get"].Should().BeFalse();
    }

    [Fact]
    public void SchemaExport_EmitsIdempotentFlagOnlyForIdempotentMethods() {
#pragma warning disable IL2026, IL3050 // build-time schema export reflects over types (documented)
        var dispatcher = new JsonRpcDispatcher(Options)
            .Map<ProbeCommand, ProbeResponse>("things.create", idempotent: true)
            .Map<ProbeCommand, ProbeResponse>("things.get")
            .Freeze();

        var schema = JsonNode.Parse(JsonRpcSchemaExporter.Generate(dispatcher, Options))!;
#pragma warning restore IL2026, IL3050

        var methods = schema["methods"]!.AsObject();
        methods["things.create"]!["idempotent"]!.GetValue<bool>().Should().BeTrue();
        methods["things.get"]!.AsObject().ContainsKey("idempotent").Should().BeFalse();
    }

    [Fact]
    public async Task Dispatch_SeedsIdempotencyKeyFromParamsMeta_ForIdempotentOperation() {
        var dispatcher = new JsonRpcDispatcher(Options)
            .MapDelegate<ProbeCommand, ProbeResponse>(
                "things.create",
                (_, sp, _) => {
                    sp.GetRequiredService<IIdempotencyKeyAccessor>().TryGetKey(out var key);
                    return new ValueTask<Result<ProbeResponse>>(Result<ProbeResponse>.Success(new ProbeResponse(key ?? "<none>")));
                },
                idempotent: true)
            .Freeze();

        var services = new ServiceCollection().AddElarionIdempotency().BuildServiceProvider();
        await using var scope = services.CreateDispatchScope();

        var request = new JsonRpcRequest {
            Jsonrpc = "2.0",
            Method = "things.create",
            Id = "1",
            Params = JsonSerializer.SerializeToElement(
                new Dictionary<string, object> {
                    ["name"] = "x",
                    ["_meta"] = new Dictionary<string, string> {
                        [IdempotencyKeyNames.MetaKey] = "key-from-meta",
                    },
                },
                Options),
        };

        var response = await dispatcher.DispatchAsync(request, scope.ServiceProvider, TestContext.Current.CancellationToken);

        response.Error.Should().BeNull();
        response.Result.Should().BeOfType<ProbeResponse>().Which.Key.Should().Be("key-from-meta");
    }

    [Fact]
    public async Task Dispatch_WithoutMetaKey_LeavesAccessorEmpty() {
        var dispatcher = new JsonRpcDispatcher(Options)
            .MapDelegate<ProbeCommand, ProbeResponse>(
                "things.create",
                (_, sp, _) => {
                    var has = sp.GetRequiredService<IIdempotencyKeyAccessor>().TryGetKey(out var key);
                    return new ValueTask<Result<ProbeResponse>>(Result<ProbeResponse>.Success(new ProbeResponse(has ? key! : "<none>")));
                },
                idempotent: true)
            .Freeze();

        var services = new ServiceCollection().AddElarionIdempotency().BuildServiceProvider();
        await using var scope = services.CreateDispatchScope();

        var request = new JsonRpcRequest {
            Jsonrpc = "2.0",
            Method = "things.create",
            Id = "1",
            Params = JsonSerializer.SerializeToElement(new { name = "x" }, Options),
        };

        var response = await dispatcher.DispatchAsync(request, scope.ServiceProvider, TestContext.Current.CancellationToken);

        response.Result.Should().BeOfType<ProbeResponse>().Which.Key.Should().Be("<none>");
    }
}

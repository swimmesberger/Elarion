using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Serialization;
using Elarion.JsonRpc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.JsonRpc;

/// <summary>
/// ADR-0082: the exported schema and the runtime serializer follow one requiredness rule. A nullable member is
/// optional to send and binds <see langword="null"/> when omitted — including a constructor parameter without a
/// default — while a non-nullable member without a default is required.
/// </summary>
public sealed partial class RequirednessAgreementTests {
    public sealed record SearchRequest(string Query, string? Note, int? Limit, int Page = 1);

    public sealed record SearchResponse(string Query, string? Note, int? Limit, int Page);

    public sealed record InitRequest {
        public required string Name { get; init; }
        public required string? Tag { get; init; }
        [JsonRequired] public string? Marker { get; init; }
    }

    [JsonSerializable(typeof(SearchRequest))]
    [JsonSerializable(typeof(SearchResponse))]
    [JsonSerializable(typeof(InitRequest))]
    private sealed partial class AgreementJsonContext : JsonSerializerContext;

    private static JsonSerializerOptions CanonicalOptions(bool reflection) {
        var services = new ServiceCollection();
        services.AddElarionJson();
        services.ConfigureElarionJson(o => {
            if (reflection) o.EnableReflectionFallback = true;
            else o.TypeInfoResolvers.Add(AgreementJsonContext.Default);
        });
        return services.BuildServiceProvider().GetRequiredService<IElarionJsonSerialization>().Options;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Runtime_BindsOmittedNullableConstructorParametersAsNull(bool reflection) {
        var options = CanonicalOptions(reflection);

        var request = JsonSerializer.Deserialize<SearchRequest>("""{"query":"q"}""", options);

        request.Should().Be(new SearchRequest("q", null, null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Runtime_StillRejectsAnOmittedNonNullableParameter(bool reflection) {
        var options = CanonicalOptions(reflection);

        var act = () => JsonSerializer.Deserialize<SearchRequest>("""{"note":"n"}""", options);

        act.Should().Throw<JsonException>().Which.Message.Should().Contain("query");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Runtime_StillRejectsExplicitNullForNonNullable(bool reflection) {
        var options = CanonicalOptions(reflection);

        var act = () => JsonSerializer.Deserialize<SearchRequest>("""{"query":null}""", options);

        act.Should().Throw<JsonException>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Runtime_TreatsNullableRequiredModifierAndJsonRequiredAsOptional(bool reflection) {
        var options = CanonicalOptions(reflection);

        var request = JsonSerializer.Deserialize<InitRequest>("""{"name":"n"}""", options);

        request!.Name.Should().Be("n");
        request.Tag.Should().BeNull();
        request.Marker.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SchemaAndDispatcher_AgreeOnTheSameRule(bool reflection) {
        var options = CanonicalOptions(reflection);
        var dispatcher = new JsonRpcDispatcher(options)
            .MapDelegate<SearchRequest, SearchResponse>("search", (r, _, _) =>
                ValueTask.FromResult<Result<SearchResponse>>(new SearchResponse(r.Query, r.Note, r.Limit, r.Page)))
            .Freeze();

        var schema = JsonNode.Parse(JsonRpcSchemaExporter.Generate(dispatcher, options))!.AsObject();
        var required = schema["methods"]!["search"]!["params"]!["required"]!.AsArray()
            .Select(n => n!.GetValue<string>()).ToArray();
        required.Should().Equal("query");

        await using var provider = new ServiceCollection().BuildServiceProvider();
        var omitted = await dispatcher.DispatchAsync(
            Call("""{"query":"q"}"""), provider, TestContext.Current.CancellationToken);
        omitted.Error.Should().BeNull();
        omitted.Result.Should().BeEquivalentTo(new SearchResponse("q", null, null, 1));

        var missingRequired = await dispatcher.DispatchAsync(
            Call("""{"note":"n"}"""), provider, TestContext.Current.CancellationToken);
        missingRequired.Error!.Code.Should().Be(-32602);
    }

    public enum Medium {
        Water,
        Power
    }

    public sealed record CreateMeterRequest(string Label, Medium? Medium, Medium Kind);

    public sealed record MeterResponse(string Label, Medium? Medium, Medium Kind);

    [JsonSourceGenerationOptions(UseStringEnumConverter = true)]
    [JsonSerializable(typeof(CreateMeterRequest))]
    [JsonSerializable(typeof(MeterResponse))]
    private sealed partial class StringEnumJsonContext : JsonSerializerContext;

    /// <summary>
    /// A nullable enum under a string-enum converter is exported as <c>{"enum":["Water","Power",null]}</c> with no
    /// <c>type</c> keyword. It must still count as nullable — optional to send, optional in the result — exactly like
    /// the runtime, which binds an omitted nullable enum as <see langword="null"/>.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NullableEnum_IsOptionalInTheSchema_AndBindsNullWhenOmitted(bool stringEnums) {
        var services = new ServiceCollection();
        services.AddElarionJson();
        services.ConfigureElarionJson(o => {
            if (stringEnums) o.TypeInfoResolvers.Add(StringEnumJsonContext.Default);
            else o.EnableReflectionFallback = true;
        });
        var options = services.BuildServiceProvider().GetRequiredService<IElarionJsonSerialization>().Options;
        var dispatcher = new JsonRpcDispatcher(options)
            .MapDelegate<CreateMeterRequest, MeterResponse>("meters.create", (r, _, _) =>
                ValueTask.FromResult<Result<MeterResponse>>(new MeterResponse(r.Label, r.Medium, r.Kind)))
            .Freeze();

        var method = JsonNode.Parse(JsonRpcSchemaExporter.Generate(dispatcher, options))!["methods"]!["meters.create"]!;
        if (stringEnums)
            method["params"]!["properties"]!["medium"]!["enum"]!.AsArray().Should().Contain(n => n == null);
        Names(method["params"]!["required"]).Should().Equal("label", "kind");
        Names(method["result"]!["required"]).Should().Equal("label", "kind");

        await using var provider = new ServiceCollection().BuildServiceProvider();
        var kind = stringEnums ? "\"Power\"" : "1";
        var omitted = await dispatcher.DispatchAsync(
            Call($$"""{"label":"l","kind":{{kind}}}""", "meters.create"), provider,
            TestContext.Current.CancellationToken);
        omitted.Error.Should().BeNull();
        omitted.Result.Should().BeEquivalentTo(new MeterResponse("l", null, Medium.Power));
    }

    private static string[] Names(JsonNode? required) {
        return required!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
    }

    private static JsonRpcRequest Call(string paramsJson, string method = "search") {
        return new JsonRpcRequest {
            Jsonrpc = "2.0", Method = method, Id = "1", Params = JsonDocument.Parse(paramsJson).RootElement
        };
    }
}

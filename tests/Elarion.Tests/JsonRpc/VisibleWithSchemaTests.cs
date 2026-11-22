using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Authorization;
using Elarion.JsonRpc;
using Xunit;

namespace Elarion.Tests.JsonRpc;

public sealed class VisibleWithSchemaTests {
    private static readonly JsonSerializerOptions Options = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true
    };

    public sealed record Show;

    public sealed record Booking(Guid Id);

    public sealed record Line {
        public required int Row { get; init; }

        [VisibleWith("banking", Verbs.Read)]
        public Booking? Resembles { get; init; }
    }

    public sealed record Preview(
        IReadOnlyList<Line> Lines,
        [VisibleWith("banking.read")] decimal? Opening,
        [property: VisibleWith(AnyOf = ["settings.read", "settings.write", "banking.read"])] decimal? Balance,
        [property: VisibleWith("banking", Verbs.Write, AnyOf = ["banking.read"])] string? Iban);

    [Fact]
    public void Schema_NamesThePermissionOfAMemberVisibleByPermission() {
        var dispatcher = new JsonRpcDispatcher(Options);
        dispatcher.MapDelegate<Show, Preview>("banking.preview",
            (_, _, _) => ValueTask.FromResult(Result<Preview>.Success(new Preview([], null, null, null))));
        dispatcher.Freeze();

        var schema = JsonNode.Parse(JsonRpcSchemaExporter.Generate(dispatcher, Options))!;
        var result = schema["methods"]!["banking.preview"]!["result"]!;

        // On a property, and on a positional record's constructor parameter.
        result["properties"]!["opening"]!["x-elarion-visible-with"]!.GetValue<string>().Should().Be("banking.read");
        var line = result["properties"]!["lines"]!["items"]!;
        line["properties"]!["resembles"]!["x-elarion-visible-with"]!.GetValue<string>().Should().Be("banking.read");
        line["properties"]!["row"]!.AsObject().ContainsKey("x-elarion-visible-with").Should().BeFalse();
    }

    [Fact]
    public void Schema_ListsThePermissionsAnyOneOfWhichShowsAMember() {
        var dispatcher = new JsonRpcDispatcher(Options);
        dispatcher.MapDelegate<Show, Preview>("banking.preview",
            (_, _, _) => ValueTask.FromResult(Result<Preview>.Success(new Preview([], null, null, null))));
        dispatcher.Freeze();

        var schema = JsonNode.Parse(JsonRpcSchemaExporter.Generate(dispatcher, Options))!;
        var properties = schema["methods"]!["banking.preview"]!["result"]!["properties"]!;

        properties["balance"]!["x-elarion-visible-with"]!.AsArray().Select(static p => p!.GetValue<string>())
            .Should().Equal("settings.read", "settings.write", "banking.read");
        // The constructor's permission first, then its alternatives.
        properties["iban"]!["x-elarion-visible-with"]!.AsArray().Select(static p => p!.GetValue<string>())
            .Should().Equal("banking.write", "banking.read");
    }
}

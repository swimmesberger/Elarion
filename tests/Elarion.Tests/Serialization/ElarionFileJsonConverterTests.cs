using System.Text.Json;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.Serialization;

/// <summary>
/// Tests the canonical base64 JSON envelope for <see cref="ElarionFile"/>: it must resolve through the canonical
/// accessor with no app context and no reflection fallback (the framework context seeds it), write the fixed
/// camelCase envelope, and round-trip a payload for the JSON transports and the idempotency/cache replay stores.
/// </summary>
public sealed class ElarionFileJsonConverterTests {
    private static IElarionJsonSerialization Resolve() {
        var services = new ServiceCollection();
        services.AddElarionJson();
        return services.BuildServiceProvider().GetRequiredService<IElarionJsonSerialization>();
    }

    [Fact]
    public void Serialize_BufferedFile_WritesFixedEnvelope_UnderStrictSourceGenOptions() {
        var json = Resolve();
        var file = new ElarionFile(new byte[] { 1, 2, 3 }, "text/csv") { FileName = "clients.csv" };

        // No app JsonSerializerContext registered and no reflection fallback: the framework context must carry it.
        var wire = JsonSerializer.Serialize(file, json.GetTypeInfo<ElarionFile>());

        wire.Should().Contain("\"contentType\":\"text/csv\"");
        wire.Should().Contain("\"fileName\":\"clients.csv\"");
        wire.Should().Contain($"\"data\":\"{Convert.ToBase64String([1, 2, 3])}\"");
        // Unset conditional-request knobs stay off the wire.
        wire.Should().NotContain("lastModified").And.NotContain("etag").And.NotContain("enableRangeProcessing");
    }

    [Fact]
    public void RoundTrip_PreservesContentAndMetadata() {
        var json = Resolve();
        var file = new ElarionFile(new byte[] { 9, 8, 7 }, "application/pdf") { FileName = "report.pdf" };

        var wire = JsonSerializer.Serialize(file, json.GetTypeInfo<ElarionFile>());
        var replayed = JsonSerializer.Deserialize(wire, json.GetTypeInfo<ElarionFile>())!;

        replayed.Bytes.ToArray().Should().Equal(9, 8, 7);
        replayed.ContentType.Should().Be("application/pdf");
        replayed.FileName.Should().Be("report.pdf");
    }

    [Fact]
    public void Deserialize_SkipsUnknownProperties() {
        var json = Resolve();
        var wire = $$"""
            {"contentType":"text/plain","future":{"nested":[1,2]},"data":"{{Convert.ToBase64String([1])}}"}
            """;

        var file = JsonSerializer.Deserialize(wire, json.GetTypeInfo<ElarionFile>())!;

        file.ContentType.Should().Be("text/plain");
        file.Bytes.ToArray().Should().Equal(1);
    }

    [Fact]
    public void Deserialize_MissingData_Throws() {
        var json = Resolve();

        var act = () => JsonSerializer.Deserialize("""{"contentType":"text/plain"}""", json.GetTypeInfo<ElarionFile>());

        act.Should().Throw<JsonException>().WithMessage("*data*");
    }

    [Fact]
    public void Deserialize_MissingContentType_Throws() {
        var json = Resolve();

        var act = () => JsonSerializer.Deserialize("""{"data":"AQ=="}""", json.GetTypeInfo<ElarionFile>());

        act.Should().Throw<JsonException>().WithMessage("*contentType*");
    }

    [Fact]
    public void Deserialize_MalformedBase64Data_ThrowsJsonException() {
        var json = Resolve();

        // A client typo must surface as JsonException (invalid input), never FormatException — transports
        // map JsonException to invalid-params (-32602) instead of internal error (-32603).
        var act = () => JsonSerializer.Deserialize(
            """{"contentType":"text/plain","data":"not base64!!!"}""", json.GetTypeInfo<ElarionFile>());

        act.Should().Throw<JsonException>().WithMessage("*base64*");
    }

    [Theory]
    [InlineData("""{"contentType":"text/plain","data":123}""")]
    [InlineData("""{"contentType":"text/plain","data":{"nested":true}}""")]
    [InlineData("""{"contentType":"text/plain","data":null}""")]
    [InlineData("""{"contentType":123,"data":"AQ=="}""")]
    [InlineData("""{"contentType":"text/plain","fileName":42,"data":"AQ=="}""")]
    public void Deserialize_WrongTokenType_ThrowsJsonException(string wire) {
        var json = Resolve();

        var act = () => JsonSerializer.Deserialize(wire, json.GetTypeInfo<ElarionFile>());

        // Never InvalidOperationException from GetString()/GetBytesFromBase64 on a mistyped token.
        act.Should().Throw<JsonException>();
    }
}

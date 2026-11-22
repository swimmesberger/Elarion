using System.Text.Json;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.Serialization;

/// <summary>
/// The wire form of <see cref="UploadedFile"/>: a plain JSON string, resolvable through the canonical accessor with no
/// app context (the framework context seeds it).
/// </summary>
public sealed class UploadedFileJsonConverterTests {
    private static IElarionJsonSerialization Resolve() {
        var services = new ServiceCollection();
        services.AddElarionJson();
        return services.BuildServiceProvider().GetRequiredService<IElarionJsonSerialization>();
    }

    [Fact]
    public void Serialize_WritesTheBlobIdAsAString() {
        var json = Resolve();

        var wire = JsonSerializer.Serialize(new UploadedFile { Id = "0193-blob" }, json.GetTypeInfo<UploadedFile>());

        wire.Should().Be("\"0193-blob\"");
    }

    [Fact]
    public void RoundTrip_PreservesTheId() {
        var json = Resolve();

        var file = JsonSerializer.Deserialize("\"0193-blob\"", json.GetTypeInfo<UploadedFile>());

        file.Should().Be(new UploadedFile { Id = "0193-blob" });
    }

    [Theory]
    [InlineData("""{"id":"0193-blob"}""")]
    [InlineData("42")]
    [InlineData("[]")]
    public void Deserialize_AnythingButAString_ThrowsJsonException(string wire) {
        var json = Resolve();

        // JsonException, never another exception, so transports answer invalid params rather than an internal error.
        var act = () => JsonSerializer.Deserialize(wire, json.GetTypeInfo<UploadedFile>());

        act.Should().Throw<JsonException>().WithMessage("*string*");
    }
}

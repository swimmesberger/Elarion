using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.Serialization;

public sealed class ElarionJsonSerializationTests {
    private static IElarionJsonSerialization Resolve(IServiceCollection services) =>
        services.BuildServiceProvider().GetRequiredService<IElarionJsonSerialization>();

    [Fact]
    public void Materializes_DefaultKnobs() {
        var services = new ServiceCollection();
        services.AddElarionJson();

        var options = Resolve(services).Options;

        options.PropertyNamingPolicy.Should().Be(JsonNamingPolicy.CamelCase);
        options.PropertyNameCaseInsensitive.Should().BeTrue();
        options.DefaultIgnoreCondition.Should().Be(JsonIgnoreCondition.WhenWritingNull);
    }

    [Fact]
    public void ConfigureElarionJson_AppliesKnobs() {
        var services = new ServiceCollection();
        services.ConfigureElarionJson(o => {
            o.PropertyNamingPolicy = null;
            o.PropertyNameCaseInsensitive = false;
            o.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        });

        var options = Resolve(services).Options;

        options.PropertyNamingPolicy.Should().BeNull();
        options.PropertyNameCaseInsensitive.Should().BeFalse();
        options.DefaultIgnoreCondition.Should().Be(JsonIgnoreCondition.Never);
    }

    [Fact]
    public void Options_IsFrozen_AndStableAcrossAccesses() {
        var services = new ServiceCollection();
        services.AddElarionJson();
        var accessor = Resolve(services);

        var first = accessor.Options;
        var second = accessor.Options;

        first.IsReadOnly.Should().BeTrue();
        second.Should().BeSameAs(first);
    }

    [Fact]
    public void Resolvers_ComposeInRegistrationOrder_FirstMatchWins() {
        var services = new ServiceCollection();
        services.ConfigureElarionJson(o => o.TypeInfoResolvers.Add(SampleJsonContext.Default));
        services.ConfigureElarionJson(o => o.TypeInfoResolvers.Add(new ThrowingResolver()));

        var options = Resolve(services).Options;

        // The source-gen context is first in the chain, so it wins — the throwing resolver is never consulted.
        // The chain also carries the always-seeded framework context (appended last), hence three.
        options.TypeInfoResolverChain.Should().HaveCount(3);
        var info = options.GetTypeInfo(typeof(SampleDto));
        info.Should().NotBeNull();
    }

    [Fact]
    public void GetTypeInfo_ResolvesFromSourceGenContext() {
        var services = new ServiceCollection();
        services.ConfigureElarionJson(o => o.TypeInfoResolvers.Add(SampleJsonContext.Default));

        var accessor = Resolve(services);
        var info = accessor.GetTypeInfo<SampleDto>();

        info.Type.Should().Be(typeof(SampleDto));

        var json = JsonSerializer.Serialize(new SampleDto { Name = "hi", Count = 5 }, info);
        json.Should().Contain("\"name\":\"hi\"");
    }

    [Fact]
    public void GetTypeInfo_AotStrict_ThrowsForUnmappedType() {
        var services = new ServiceCollection();
        services.AddElarionJson();

        var accessor = Resolve(services);

        // No source-gen context, no reflection fallback => an unmapped type cannot be resolved.
        var act = () => accessor.GetTypeInfo<SampleDto>();
        act.Should().Throw<Exception>();
    }

    [Fact]
    public void FrameworkErrorPayload_ResolvesUnderSourceGeneration_ByDefault() {
        var services = new ServiceCollection();
        services.AddElarionJson(); // no contributed context, reflection off (AOT-strict default)

        var accessor = Resolve(services);

        // The framework seeds its own context (ElarionFrameworkJsonContext), so ValidationErrorData resolves
        // without a per-app [JsonSerializable] and without the reflection fallback — the fix for the reflection-off 500.
        var info = accessor.GetTypeInfo<ValidationErrorData>();
        info.Type.Should().Be(typeof(ValidationErrorData));

        var json = JsonSerializer.Serialize(new ValidationErrorData { Errors = ["name is required"] }, info);
        json.Should().Be("{\"errors\":[\"name is required\"]}");
    }

    [Fact]
    public void GetTypeInfo_ReflectionFallback_ResolvesUnmappedType() {
        var services = new ServiceCollection();
        services.ConfigureElarionJson(o => o.EnableReflectionFallback = true);

        var accessor = Resolve(services);
        var info = accessor.GetTypeInfo<SampleDto>();

        info.Type.Should().Be(typeof(SampleDto));
    }

    [Fact]
    public void PostConfigure_RunsAfterResolvers() {
        var converter = new SampleConverter();
        var services = new ServiceCollection();
        services.ConfigureElarionJson(o => o.PostConfigure = options => options.Converters.Add(converter));

        var options = Resolve(services).Options;

        options.Converters.Should().Contain(converter);
    }

    [Fact]
    public void AddElarionJson_IsIdempotent() {
        var services = new ServiceCollection();
        services.AddElarionJson();
        services.AddElarionJson();

        services.Count(d => d.ServiceType == typeof(IElarionJsonSerialization)).Should().Be(1);
    }

    [Fact]
    public void AddElarionJson_DoesNotRegisterBareJsonSerializerOptions() {
        var services = new ServiceCollection();
        services.AddElarionJson();

        services.Should().NotContain(d => d.ServiceType == typeof(JsonSerializerOptions));
    }

    private sealed class ThrowingResolver : IJsonTypeInfoResolver {
        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) =>
            throw new InvalidOperationException("should not be consulted");
    }

    private sealed class SampleConverter : JsonConverter<SampleDto> {
        public override SampleDto Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, SampleDto value, JsonSerializerOptions options) =>
            throw new NotSupportedException();
    }
}

internal sealed record SampleDto {
    public string Name { get; init; } = string.Empty;
    public int Count { get; init; }
}

[JsonSerializable(typeof(SampleDto))]
internal sealed partial class SampleJsonContext : JsonSerializerContext;

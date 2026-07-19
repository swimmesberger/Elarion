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
    private static IElarionJsonSerialization Resolve(IServiceCollection services) {
        return services.BuildServiceProvider().GetRequiredService<IElarionJsonSerialization>();
    }

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
    public void ValidationFieldErrors_SerializeUnderSourceGeneration_ReflectionOff() {
        var services = new ServiceCollection();
        services.AddElarionJson(); // no contributed context, reflection off (AOT-strict default)

        var accessor = Resolve(services);
        var data = new ValidationErrorData {
            Errors = ["Street is required"],
            FieldErrors = new Dictionary<string, string[]> { ["address.street"] = ["Street is required"] }
        };

        // The field-keyed dictionary member is statically reachable from ValidationErrorData, so the framework
        // context covers it; keys stay wire-named verbatim (no DictionaryKeyPolicy re-mapping).
        var json = JsonSerializer.Serialize(data, accessor.GetTypeInfo<ValidationErrorData>());
        json.Should()
            .Be("{\"errors\":[\"Street is required\"],\"fieldErrors\":{\"address.street\":[\"Street is required\"]}}");
    }

    [Fact]
    public void StoredIdempotencyResult_RoundTrips_UnderSourceGeneration_ReflectionOff() {
        // The idempotency replay envelope must serialize AOT-strict: the framework context registers the
        // non-generic StoredResult (and AppError), while the success value goes through the module context's
        // GetTypeInfo(typeof(T)). No closed StoredResult<T> and no reflection fallback are ever needed (C2).
        var services = new ServiceCollection();
        services.ConfigureElarionJson(o => o.TypeInfoResolvers.Add(SampleJsonContext.Default)); // reflection off

        var accessor = Resolve(services);
        var options = accessor.Options;

        // Success path — mirrors the generated policy's Serialize(Result<SampleDto>).
        var value = new SampleDto { Name = "receipt", Count = 7 };
        var stored = new Elarion.Abstractions.Idempotency.StoredResult {
            Ok = true,
            Value = JsonSerializer.SerializeToElement(value, options.GetTypeInfo(typeof(SampleDto)))
        };
        var payload = JsonSerializer.Serialize(stored, ElarionFrameworkJsonContext.Default.StoredResult);

        var back = JsonSerializer.Deserialize(payload, ElarionFrameworkJsonContext.Default.StoredResult)!;
        back.Ok.Should().BeTrue();
        var roundTripped = JsonSerializer.Deserialize(
            back.Value!.Value, (JsonTypeInfo<SampleDto>)options.GetTypeInfo(typeof(SampleDto)))!;
        roundTripped.Should().Be(value);

        // Failure path — the AppError must resolve through the framework context, reflection off.
        var failure = new Elarion.Abstractions.Idempotency.StoredResult {
            Ok = false,
            Error = AppError.BusinessRule("declined")
        };
        var failurePayload = JsonSerializer.Serialize(failure, ElarionFrameworkJsonContext.Default.StoredResult);
        var failureBack = JsonSerializer.Deserialize(
            failurePayload, ElarionFrameworkJsonContext.Default.StoredResult)!;
        failureBack.Ok.Should().BeFalse();
        failureBack.Error!.Kind.Should().Be(ErrorKind.BusinessRule);
        failureBack.Error.Message.Should().Be("declined");
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
    public void OverrideResolvers_WinOverTransportStyleInsertAtZero() {
        static string Serialize(bool withOverride) {
            var services = new ServiceCollection();
            // Simulates a transport contribution: inserted at index 0 of the ordinary list, the position that
            // beats every other TypeInfoResolvers entry regardless of registration order.
            services.ConfigureElarionJson(o => o.TypeInfoResolvers.Insert(0, new TaggingResolver("transport")));
            if (withOverride)
                // The host override is registered later, yet must win first-match for the shared type.
                services.ConfigureElarionJson(o => o.OverrideTypeInfoResolvers.Add(new TaggingResolver("override")));

            var accessor = Resolve(services);
            return JsonSerializer.Serialize(new OverrideProbeDto(), accessor.GetTypeInfo<OverrideProbeDto>());
        }

        Serialize(false).Should().Be("\"transport\"");
        Serialize(true).Should().Be("\"override\"");
    }

    [Fact]
    public void OverrideResolvers_ComposeAheadOfEveryContributedResolver() {
        var overrideResolver = new TaggingResolver("override");
        var services = new ServiceCollection();
        services.ConfigureElarionJson(o => o.TypeInfoResolvers.Add(SampleJsonContext.Default));
        services.ConfigureElarionJson(o => o.OverrideTypeInfoResolvers.Add(overrideResolver));

        var options = Resolve(services).Options;

        options.TypeInfoResolverChain[0].Should().BeSameAs(overrideResolver);
        options.TypeInfoResolverChain[1].Should().BeSameAs(SampleJsonContext.Default);
    }

    [Fact]
    public void AddElarionJson_DoesNotRegisterBareJsonSerializerOptions() {
        var services = new ServiceCollection();
        services.AddElarionJson();

        services.Should().NotContain(d => d.ServiceType == typeof(JsonSerializerOptions));
    }

    private sealed class ThrowingResolver : IJsonTypeInfoResolver {
        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) {
            throw new InvalidOperationException("should not be consulted");
        }
    }

    /// <summary>Resolves <see cref="OverrideProbeDto"/> with a converter that writes a fixed tag, so a test can
    /// observe which chain segment won first-match resolution for a contested type.</summary>
    private sealed class TaggingResolver(string tag) : IJsonTypeInfoResolver {
        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) {
            return type == typeof(OverrideProbeDto)
                ? JsonMetadataServices.CreateValueInfo<OverrideProbeDto>(options, new TagConverter(tag))
                : null;
        }

        private sealed class TagConverter(string tag) : JsonConverter<OverrideProbeDto> {
            public override OverrideProbeDto Read(ref Utf8JsonReader reader, Type typeToConvert,
                JsonSerializerOptions options) {
                throw new NotSupportedException();
            }

            public override void Write(Utf8JsonWriter writer, OverrideProbeDto value, JsonSerializerOptions options) {
                writer.WriteStringValue(tag);
            }
        }
    }

    private sealed class SampleConverter : JsonConverter<SampleDto> {
        public override SampleDto Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
            throw new NotSupportedException();
        }

        public override void Write(Utf8JsonWriter writer, SampleDto value, JsonSerializerOptions options) {
            throw new NotSupportedException();
        }
    }
}

internal sealed record SampleDto {
    public string Name { get; init; } = string.Empty;
    public int Count { get; init; }
}

[JsonSerializable(typeof(SampleDto))]
internal sealed partial class SampleJsonContext : JsonSerializerContext;

internal sealed record OverrideProbeDto;

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Elarion.Abstractions.Serialization;

/// <summary>
/// The default <see cref="IElarionJsonSerialization"/>. Composes every registered
/// <see cref="ElarionJsonConfigurator"/> into a single <see cref="ElarionJsonOptions"/>, materializes the
/// canonical <see cref="JsonSerializerOptions"/> from it, and freezes them on first access.
/// </summary>
internal sealed class ElarionJsonSerialization : IElarionJsonSerialization {
    private readonly Lazy<JsonSerializerOptions> _options;

    public ElarionJsonSerialization(IEnumerable<ElarionJsonConfigurator> configurators) {
        // Capture the configurators; materialize lazily so all layers have contributed by first use.
        // LazyThreadSafetyMode.ExecutionAndPublication (the default) guarantees a single frozen instance.
        _options = new Lazy<JsonSerializerOptions>(() => Build(configurators));
    }

    public JsonSerializerOptions Options => _options.Value;

    public JsonTypeInfo<T> GetTypeInfo<T>() => (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T));

    public JsonTypeInfo GetTypeInfo(Type type) => Options.GetTypeInfo(type);

    private static JsonSerializerOptions Build(IEnumerable<ElarionJsonConfigurator> configurators) {
        var config = new ElarionJsonOptions();
        foreach (var configurator in configurators) {
            configurator.Apply(config);
        }

        var options = new JsonSerializerOptions {
            PropertyNamingPolicy = config.PropertyNamingPolicy,
            PropertyNameCaseInsensitive = config.PropertyNameCaseInsensitive,
            DefaultIgnoreCondition = config.DefaultIgnoreCondition,
        };

        // Ordered, first-match-wins. Transport envelopes contributed first, module/host contexts after.
        foreach (var resolver in config.TypeInfoResolvers) {
            options.TypeInfoResolverChain.Add(resolver);
        }

        // AOT-strict by default: only append the reflection fallback when explicitly opted in.
        if (config.EnableReflectionFallback) {
            options.TypeInfoResolverChain.Add(CreateReflectionFallbackResolver());
        }

        if (options.TypeInfoResolverChain.Count == 0) {
            // Nothing has contributed a context yet and there is no reflection fallback. MakeReadOnly requires a
            // resolver, so attach an empty one: freezing still succeeds, and any type request fails loudly
            // (AOT-strict), surfacing a missing source-generated context rather than silently reflecting.
            options.TypeInfoResolverChain.Add(EmptyJsonTypeInfoResolver.Instance);
        }

        config.PostConfigure?.Invoke(options);
        options.MakeReadOnly();
        return options;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "The reflection fallback is an explicit, documented opt-in via ElarionJsonOptions.EnableReflectionFallback; " +
                        "AOT/trimmed hosts leave it off and rely on source-generated contexts.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "The reflection fallback is an explicit, documented opt-in via ElarionJsonOptions.EnableReflectionFallback; " +
                        "AOT/trimmed hosts leave it off and rely on source-generated contexts.")]
    private static IJsonTypeInfoResolver CreateReflectionFallbackResolver() => new DefaultJsonTypeInfoResolver();

    private sealed class EmptyJsonTypeInfoResolver : IJsonTypeInfoResolver {
        public static readonly EmptyJsonTypeInfoResolver Instance = new();

        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) => null;
    }
}

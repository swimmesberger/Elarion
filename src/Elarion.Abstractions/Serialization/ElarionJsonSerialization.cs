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

    public JsonTypeInfo<T> GetTypeInfo<T>() {
        return (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T));
    }

    public JsonTypeInfo GetTypeInfo(Type type) {
        return Options.GetTypeInfo(type);
    }

    private static JsonSerializerOptions Build(IEnumerable<ElarionJsonConfigurator> configurators) {
        var config = new ElarionJsonOptions();
        foreach (var configurator in configurators) configurator.Apply(config);

        var options = new JsonSerializerOptions {
            PropertyNamingPolicy = config.PropertyNamingPolicy,
            PropertyNameCaseInsensitive = config.PropertyNameCaseInsensitive,
            DefaultIgnoreCondition = config.DefaultIgnoreCondition
        };

        // Ordered, first-match-wins. Host overrides win over everything the framework and transports
        // contributed; then transport envelopes (contributed first within the ordinary list), then
        // module/host contexts.
        foreach (var resolver in config.OverrideTypeInfoResolvers) options.TypeInfoResolverChain.Add(resolver);

        foreach (var resolver in config.TypeInfoResolvers) options.TypeInfoResolverChain.Add(resolver);

        // The framework's own types that no app/module context would register (e.g. the ValidationErrorData behind
        // AppError.Data's polymorphic object slot) must always be resolvable, so a failed Result serializes its
        // typed error data under source generation even when the host contributed no context for them. Appended
        // last so any host/module context still wins first-match for an overlapping type; reflection-free, so it
        // keeps core AOT-strict. It also guarantees the chain is never empty, so MakeReadOnly always has a
        // resolver to freeze.
        options.TypeInfoResolverChain.Add(ElarionFrameworkJsonContext.Default);

        // AOT-strict by default: only append the reflection fallback when explicitly opted in.
        if (config.EnableReflectionFallback) options.TypeInfoResolverChain.Add(CreateReflectionFallbackResolver());

        config.PostConfigure?.Invoke(options);
        options.MakeReadOnly();
        return options;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification =
            "The reflection fallback is an explicit, documented opt-in via ElarionJsonOptions.EnableReflectionFallback; " +
            "AOT/trimmed hosts leave it off and rely on source-generated contexts.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification =
            "The reflection fallback is an explicit, documented opt-in via ElarionJsonOptions.EnableReflectionFallback; " +
            "AOT/trimmed hosts leave it off and rely on source-generated contexts.")]
    private static IJsonTypeInfoResolver CreateReflectionFallbackResolver() {
        return new DefaultJsonTypeInfoResolver();
    }
}

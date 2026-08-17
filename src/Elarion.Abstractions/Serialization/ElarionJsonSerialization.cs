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
        return (JsonTypeInfo<T>)GetTypeInfo(typeof(T));
    }

    public JsonTypeInfo GetTypeInfo(Type type) {
        ArgumentNullException.ThrowIfNull(type);

        // TryGetTypeInfo rather than GetTypeInfo: the BCL's failure message names only the type, which leaves the
        // caller guessing at the AOT-strict contract. Composing a module without its JSON context is the common
        // cause (see AddJsonTypeInfoResolver in the generated ConfigureDefaultServices), so say so.
        if (Options.TryGetTypeInfo(type, out var info))
            return info;

        throw new InvalidOperationException(
            $"No JSON metadata for '{type}': it is in none of the resolvers composed into Elarion's canonical " +
            "JsonSerializerOptions, and the reflection fallback is off. Add the type to a source-generated " +
            "JsonSerializerContext and contribute that context with ConfigureElarionJson(o => " +
            "o.TypeInfoResolvers.Add(MyJsonContext.Default)) — a module's context is contributed automatically by " +
            "its generated ConfigureDefaultServices, so a container composed without that module's registration " +
            "(or without AddElarion) never sees it.");
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
        //
        // The same resolver instance may be contributed twice — a module contributes its own context through the
        // generated ConfigureDefaultServices, and the host bootstrapper contributes every enabled module's context
        // again through GetAllJsonTypeInfoResolvers. Appending it once keeps the chain (and its first-match order)
        // exactly what a single contribution would produce. Identity is by instance, which is why a contribution
        // should hand over a stable resolver — a generated context's .Default singleton is one.
        foreach (var resolver in config.OverrideTypeInfoResolvers) AddOnce(options, resolver);

        foreach (var resolver in config.TypeInfoResolvers) AddOnce(options, resolver);

        // The framework's own types that no app/module context would register (e.g. the ValidationErrorData behind
        // AppError.Data's polymorphic object slot) must always be resolvable, so a failed Result serializes its
        // typed error data under source generation even when the host contributed no context for them. Appended
        // last so any host/module context still wins first-match for an overlapping type; reflection-free, so it
        // keeps core AOT-strict. It also guarantees the chain is never empty, so MakeReadOnly always has a
        // resolver to freeze. Appended through the same once-only path as every contribution, so a host that
        // also contributes this context explicitly keeps it at the position it asked for rather than twice.
        AddOnce(options, ElarionFrameworkJsonContext.Default);

        // AOT-strict by default: only append the reflection fallback when explicitly opted in.
        if (config.EnableReflectionFallback) options.TypeInfoResolverChain.Add(CreateReflectionFallbackResolver());

        config.PostConfigure?.Invoke(options);
        options.MakeReadOnly();
        return options;
    }

    /// <summary>Appends <paramref name="resolver"/> unless that exact instance is already in the chain.</summary>
    private static void AddOnce(JsonSerializerOptions options, IJsonTypeInfoResolver resolver) {
        foreach (var existing in options.TypeInfoResolverChain)
            if (ReferenceEquals(existing, resolver))
                return;

        options.TypeInfoResolverChain.Add(resolver);
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

using Microsoft.CodeAnalysis;

namespace Elarion.Generators;

/// <summary>
/// Compile-time lifetime classification for singleton-handler verification (ADR-0066, ELSG011/ELSG012): what
/// the generator can PROVE about a constructor dependency's registered lifetime. Sources, in order: the
/// curated framework tables below (verified against the actual <c>AddElarion*</c>/<c>TryAdd*</c>
/// registrations), then in-compilation <c>[Service(Scope = …)]</c> facts. Anything else is Unknown — and
/// unknown fails the build, because a captive scoped dependency inside a singleton handler must be impossible
/// to ship, not a runtime surprise.
/// </summary>
internal static class ServiceLifetimeKnowledge {
    internal enum Classification {
        Singleton,
        ScopedOrTransient,
        Unknown
    }

    // Framework services registered singleton (verified registrations). Fully-qualified format WITHOUT the
    // global:: prefix is not used here — entries match ISymbol.ToDisplayString(FullyQualifiedFormat).
    private static readonly string[] SingletonExact = [
        "global::System.TimeProvider",
        "global::Elarion.Abstractions.Serialization.IElarionJsonSerialization",
        "global::Elarion.Abstractions.Resilience.IResiliencePipelineRunner",
        "global::Elarion.Abstractions.Resilience.IResiliencePolicyCatalog",
        "global::Microsoft.Extensions.Logging.ILoggerFactory",
        "global::Microsoft.Extensions.Configuration.IConfiguration",
        "global::System.IServiceProvider"
    ];

    // Open-generic framework singletons, matched on the constructed name's prefix.
    private static readonly string[] SingletonPrefixes = [
        "global::Microsoft.Extensions.Logging.ILogger<",
        "global::Microsoft.Extensions.Options.IOptions<",
        "global::Microsoft.Extensions.Options.IOptionsMonitor<",
        "global::Microsoft.Extensions.Options.IOptionsFactory<"
    ];

    // Framework services registered scoped (or otherwise per-dispatch): a singleton handler holding one is a
    // captive-dependency bug. Verified against the framework's registrations.
    private static readonly string[] ScopedExact = [
        "global::Elarion.Abstractions.Pipeline.IUnitOfWork",
        "global::Elarion.Abstractions.Identity.ICurrentUser",
        "global::Elarion.Abstractions.Idempotency.IIdempotencyKeyAccessor",
        "global::Elarion.Abstractions.Idempotency.IIdempotencyKeySeed",
        "global::Elarion.Abstractions.Authorization.IAuthorizer",
        "global::Elarion.Abstractions.Validation.IRequestValidator",
        "global::Elarion.Abstractions.Caching.IHandlerCache",
        "global::Elarion.Abstractions.Auditing.IAuditTrail",
        "global::Elarion.Abstractions.Auditing.IAuditScope",
        "global::Elarion.Abstractions.Diagnostics.IHandlerContextEnricher",
        "global::Elarion.Abstractions.IHandlerSender"
    ];

    // Scoped per-dispatch open generics (validators, typed handlers resolved mid-chain).
    private static readonly string[] ScopedPrefixes = [
        "global::Elarion.Abstractions.IHandler<",
        "global::Elarion.Abstractions.IStreamHandler<",
        "global::Microsoft.Extensions.Options.IOptionsSnapshot<"
    ];

    /// <summary>
    /// Classifies one constructor-dependency type. <paramref name="serviceScopeFacts"/> maps contract FQN →
    /// <c>ServiceScope</c> value for unambiguous in-compilation <c>[Service]</c> declarations.
    /// </summary>
    public static Classification Classify(
        ITypeSymbol dependency,
        SymbolDisplayFormat fmt,
        IReadOnlyDictionary<string, int> serviceScopeFacts) {
        // EF DbContext-derived dependencies are per-scope by construction (AddDbContext default), regardless of
        // how the app names them.
        for (var current = dependency as INamedTypeSymbol; current is not null; current = current.BaseType)
            if (current.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ==
                "global::Microsoft.EntityFrameworkCore.DbContext")
                return Classification.ScopedOrTransient;

        var fqn = dependency.ToDisplayString(fmt);
        if (SingletonExact.Contains(fqn, StringComparer.Ordinal))
            return Classification.Singleton;

        foreach (var prefix in SingletonPrefixes)
            if (fqn.StartsWith(prefix, StringComparison.Ordinal))
                return Classification.Singleton;

        if (ScopedExact.Contains(fqn, StringComparer.Ordinal))
            return Classification.ScopedOrTransient;

        foreach (var prefix in ScopedPrefixes)
            if (fqn.StartsWith(prefix, StringComparison.Ordinal))
                return Classification.ScopedOrTransient;

        if (serviceScopeFacts.TryGetValue(fqn, out var scope))
            // ServiceScope: Scoped = 0, Singleton = 1, Transient = 2. Only Singleton is safe to capture.
            return scope == 1 ? Classification.Singleton : Classification.ScopedOrTransient;

        return Classification.Unknown;
    }
}

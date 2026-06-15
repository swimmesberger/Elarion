using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Elarion.Generators;

public sealed partial class HandlerRegistrationGenerator {
    private const string HandlerInterfaceMetadataName = "Elarion.Abstractions.IHandler`2";
    private const string DecoratorListAttributeMetadataName = "Elarion.Abstractions.Pipeline.DecoratorListAttribute";
    private const string AppModuleAttributeMetadataName = "Elarion.Abstractions.Modules.AppModuleAttribute";
    private const string TriggerAttributeMetadataName = "Elarion.Abstractions.GenerateModuleHandlersAttribute";
    private const string CacheableAttributeMetadataName = "Elarion.Abstractions.Caching.CacheableAttribute";
    private const string CacheInvalidateAttributeMetadataName = "Elarion.Abstractions.Caching.CacheInvalidateAttribute";
    private const string ResilientAttributeMetadataName = "Elarion.Abstractions.Resilience.ResilientAttribute";

    private sealed record HandlerInfo(
        string HandlerFqn,
        string HandlerName,
        string RequestFqn,
        string ResponseFqn,
        string Namespace,
        ImmutableArray<DecoratorInfo> Decorators,
        string? ResiliencePolicyName,
        CacheableInfo? Cacheable,
        CacheInvalidationInfo? CacheInvalidation,
        ImmutableArray<CacheDiagnosticInfo> Diagnostics
    );

    private sealed record DecoratorInfo(
        string DecoratorFqn,
        ImmutableArray<string> ExtraDependencyFqns
    );

    private sealed record CacheableInfo(
        ImmutableArray<string> Tags,
        int DurationSeconds,
        int ScopeValue,
        ImmutableArray<CacheKeyPropertyInfo> KeyProperties,
        string? ResultValueFqn
    );

    private sealed record CacheInvalidationInfo(
        ImmutableArray<string> Tags,
        int ScopeValue
    );

    private sealed record CacheKeyPropertyInfo(string Name);

    private sealed record CacheDiagnosticInfo(
        DiagnosticDescriptor Descriptor,
        Location? Location,
        object?[] MessageArgs
    );

    private sealed record ModuleInfo(string Name, string Namespace);

    private static readonly DiagnosticDescriptor CacheableAndInvalidatingDescriptor = new(
        "WIMCACHE001",
        "Handler cannot be both cacheable and cache-invalidating",
        "Handler '{0}' cannot use both CacheableAttribute and CacheInvalidateAttribute",
        "Elarion.Abstractions.Caching",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor EmptyCacheTagsDescriptor = new(
        "WIMCACHE002",
        "Handler cache tags are required",
        "Handler '{0}' must define at least one non-empty cache tag",
        "Elarion.Abstractions.Caching",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidCacheTagDescriptor = new(
        "WIMCACHE003",
        "Handler cache tag is invalid",
        "Handler '{0}' contains invalid cache tag '{1}'",
        "Elarion.Abstractions.Caching",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidCacheDurationDescriptor = new(
        "WIMCACHE004",
        "Handler cache duration is invalid",
        "Handler '{0}' must define a positive cache duration",
        "Elarion.Abstractions.Caching",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}

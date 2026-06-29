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
    private const string HandlerMetadataTypeName = "Elarion.Abstractions.Pipeline.HandlerMetadata";
    private const string RequireClaimAttributeMetadataName = "Elarion.Abstractions.Authorization.RequireClaimAttribute";
    private const string RequirePermissionAttributeMetadataName = "Elarion.Abstractions.Authorization.RequirePermissionAttribute";
    private const string RequireRoleAttributeMetadataName = "Elarion.Abstractions.Authorization.RequireRoleAttribute";
    private const string RequirePolicyAttributeMetadataName = "Elarion.Abstractions.Authorization.RequirePolicyAttribute";
    private const string RequireResourceAttributeMetadataName = "Elarion.Abstractions.Authorization.RequireResourceAttribute";
    private const string AllowAnonymousAttributeMetadataName = "Elarion.Abstractions.Authorization.AllowAnonymousAttribute";
    private const string AuthorizationDefaultsAttributeMetadataName = "Elarion.Abstractions.Authorization.ElarionAuthorizationDefaultsAttribute";
    private const string FeatureGateAttributeMetadataName = "Elarion.Abstractions.Features.FeatureGateAttribute";
    private const string ResultFailureFactoryMetadataName = "Elarion.Abstractions.IResultFailureFactory`1";

    private sealed record HandlerInfo(
        string HandlerFqn,
        string HandlerName,
        string RequestFqn,
        string ResponseFqn,
        string Namespace,
        EquatableArray<DecoratorInfo> Decorators,
        string? ResiliencePolicyName,
        CacheableInfo? Cacheable,
        CacheInvalidationInfo? CacheInvalidation,
        bool HasAuthorization,
        bool RequireAuthenticatedByDefault,
        EquatableArray<ResourceBindingInfo> ResourceBindings,
        bool HasFeatureGates,
        EquatableArray<DiagnosticInfo> Diagnostics
    );

    // A [RequireResource] binding: the resource type, the operation, and the compile-checked request path the
    // generator validated and emits as a typed accessor (ADR-0012, Tier 1).
    private sealed record ResourceBindingInfo(
        string ResourceTypeFqn,
        string Operation,
        string IdPath
    );

    private sealed record DecoratorInfo(
        string DecoratorFqn,
        EquatableArray<DecoratorDependency> ExtraDependencies,
        bool HasAppliesTo
    );

    // A constructor dependency of a pipeline decorator (besides the inner handler). A regular service is
    // resolved from DI; HandlerMetadata is supplied by the generator with the concrete handler type, so
    // attribute-driven decorators see the true handler regardless of their position in the chain.
    private sealed record DecoratorDependency(
        string Fqn,
        bool IsHandlerMetadata
    );

    private sealed record CacheableInfo(
        EquatableArray<string> Tags,
        int DurationSeconds,
        int ScopeValue,
        EquatableArray<CacheKeyPropertyInfo> KeyProperties,
        string? ResultValueFqn
    );

    private sealed record CacheInvalidationInfo(
        EquatableArray<string> Tags,
        int ScopeValue
    );

    private sealed record CacheKeyPropertyInfo(string Name);

    private static readonly DiagnosticDescriptor CacheableAndInvalidatingDescriptor = new(
        "ELCACHE001",
        "Handler cannot be both cacheable and cache-invalidating",
        "Handler '{0}' cannot use both CacheableAttribute and CacheInvalidateAttribute",
        "Elarion.Abstractions.Caching",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor EmptyCacheTagsDescriptor = new(
        "ELCACHE002",
        "Handler cache tags are required",
        "Handler '{0}' must define at least one non-empty cache tag",
        "Elarion.Abstractions.Caching",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidCacheTagDescriptor = new(
        "ELCACHE003",
        "Handler cache tag is invalid",
        "Handler '{0}' contains invalid cache tag '{1}'",
        "Elarion.Abstractions.Caching",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidCacheDurationDescriptor = new(
        "ELCACHE004",
        "Handler cache duration is invalid",
        "Handler '{0}' must define a positive cache duration",
        "Elarion.Abstractions.Caching",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor AuthorizationResponseNotFailureCapable = new(
        "ELAUTH001",
        "Authorized handler response cannot represent failure",
        "Handler '{0}' declares an authorization requirement but its response type '{1}' does not implement "
        + "IResultFailureFactory<T>, so the authorization check cannot short-circuit and would be silently "
        + "skipped; return Result<T> or Result",
        "Elarion.Abstractions.Authorization",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor ResourceIdPathNotFound = new(
        "ELAUTH002",
        "RequireResource id path does not resolve",
        "Handler '{0}' declares [RequireResource] with Id path '{1}', which does not resolve to a property on "
        + "request type '{2}'; use nameof(Request.Id) or a dotted path of existing properties",
        "Elarion.Abstractions.Authorization",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor FeatureGateResponseNotFailureCapable = new(
        "ELFEAT001",
        "Feature-gated handler response cannot represent failure",
        "Handler '{0}' declares a [FeatureGate] but its response type '{1}' does not implement "
        + "IResultFailureFactory<T>, so the gate cannot short-circuit and would be silently skipped; "
        + "return Result<T> or Result",
        "Elarion.Abstractions.Features",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor EmptyFeatureGateDescriptor = new(
        "ELFEAT002",
        "FeatureGate declares no feature name",
        "Handler '{0}' declares a [FeatureGate] with no feature name (or a blank one); the gate has no effect",
        "Elarion.Abstractions.Features",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);
}

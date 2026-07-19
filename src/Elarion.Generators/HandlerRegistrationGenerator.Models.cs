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

    private const string ResiliencePolicyAttributeMetadataName =
        "Elarion.Abstractions.Resilience.ResiliencePolicyAttribute";

    private const string HandlerMetadataTypeName = "Elarion.Abstractions.Pipeline.HandlerMetadata";
    private const string RequireClaimAttributeMetadataName = "Elarion.Abstractions.Authorization.RequireClaimAttribute";

    private const string RequirePermissionAttributeMetadataName =
        "Elarion.Abstractions.Authorization.RequirePermissionAttribute";

    private const string RequireRoleAttributeMetadataName = "Elarion.Abstractions.Authorization.RequireRoleAttribute";

    private const string RequirePolicyAttributeMetadataName =
        "Elarion.Abstractions.Authorization.RequirePolicyAttribute";

    private const string RequireResourceAttributeMetadataName =
        "Elarion.Abstractions.Authorization.RequireResourceAttribute";

    private const string AllowAnonymousAttributeMetadataName =
        "Elarion.Abstractions.Authorization.AllowAnonymousAttribute";

    private const string AuthorizationDefaultsAttributeMetadataName =
        "Elarion.Abstractions.Authorization.ElarionAuthorizationDefaultsAttribute";

    private const string FeatureGateAttributeMetadataName = "Elarion.Abstractions.Features.FeatureGateAttribute";
    private const string FeatureVariantAttributeMetadataName = "Elarion.Abstractions.Features.FeatureVariantAttribute";
    private const string IdempotentAttributeMetadataName = "Elarion.Abstractions.Idempotency.IdempotentAttribute";
    private const string AuditableAttributeMetadataName = "Elarion.Abstractions.Auditing.AuditableAttribute";

    private const string AuditDefaultsAttributeMetadataName =
        "Elarion.Abstractions.Auditing.ElarionAuditDefaultsAttribute";

    private const string AuditTrailMetadataName = "Elarion.Abstractions.Auditing.IAuditTrail";
    private const string CommandMarkerMetadataName = "Elarion.Abstractions.ICommand";

    private const string AllowDuplicatesAttributeMetadataName =
        "Elarion.Abstractions.Messaging.AllowDuplicatesAttribute";

    private const string HandlerTelemetryAttributeMetadataName =
        "Elarion.Abstractions.HandlerTelemetryAttribute";

    private const string ElarionValidationExtensionsMetadataName =
        "Elarion.Validation.ElarionValidationServiceCollectionExtensions";

    private const string ResultFailureFactoryMetadataName = "Elarion.Abstractions.IResultFailureFactory`1";
    private const string DomainEventMetadataName = "Elarion.Abstractions.Messaging.IDomainEvent";
    private const string IntegrationEventMetadataName = "Elarion.Abstractions.Messaging.IIntegrationEvent";

    internal sealed record HandlerInfo(
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
        bool HasValidation,
        IdempotentInfo? Idempotent,
        AuditInfo? Audit,
        EquatableArray<string> VariantContractDeps,
        // The DI service key for event-consumer handlers (their FQN); null for commands/queries, which stay
        // registered unkeyed so exactly one handler resolves per request. Keying event consumers lets multiple
        // consumers (incl. actor relays) coexist for one event without colliding on IHandler<TEvent, Result<Unit>>.
        string? EventConsumerKey,
        // The effective [HandlerTelemetry] resolution (handler over module over assembly, default Full): false
        // means the observability decorator is not generated at all for this handler (ADR-0066).
        bool EmitObservability,
        // [Handler(Scope = …)] as the ServiceScope enum value (Scoped = 0, Singleton = 1, Transient = 2) —
        // the registration lifetime the emit uses. Singleton is compile-time verified (ELSG011-013).
        int ScopeValue,
        EquatableArray<DiagnosticInfo> Diagnostics
    );

    // One in-compilation [Service(Scope = …)] fact: a contract (or implementation) FQN and its declared scope.
    // Consumed by the singleton-handler dependency verification (ELSG011/ELSG012).
    internal sealed record ServiceScopeFact(
        string Fqn,
        int ScopeValue
    );

    // Audit attachment (ADR-0045): Action is the compile-resolved wire name ("{module}.{operation}" — an
    // explicit [Handler("…")] name verbatim, else inferred exactly like the RPC map so audit records and the
    // schema agree); Module is the owning module's name (null when unscoped).
    internal sealed record AuditInfo(
        string Action,
        string? Module
    );

    // Owner is the Consumer-scope owner discriminator (the consuming handler's identity) baked into the generated
    // policy for the inbox (ADR-0022); null for command idempotency, whose owner derives from the caller.
    internal sealed record IdempotentInfo(
        int RetentionHours,
        bool KeyRequired,
        int ScopeValue,
        bool Fingerprint,
        int ConflictBehaviorValue,
        int StoreFailuresValue,
        string? ResultValueFqn,
        string? Owner
    );

    // A [ResiliencePolicy] declaration in the current compilation, reduced to what ELPIPE004 needs: the policy
    // name and whether the declaration configures retry (any retry option present with an effective
    // MaxRetryAttempts other than zero). Names declared only without retry are provably safe to pair with a
    // command that has no [Idempotent]; everything else — retrying, unknown (referenced assembly, imperative
    // registration), or ambiguous duplicates — is conservatively treated as retrying.
    internal sealed record PolicyRetryCandidate(
        string Name,
        bool HasRetry
    );

    // A [RequireResource] binding: the resource type, the operation, and the compile-checked request path the
    // generator validated and emits as a typed accessor (ADR-0012, Tier 1).
    internal sealed record ResourceBindingInfo(
        string ResourceTypeFqn,
        string Operation,
        string IdPath,
        string? ResourceTypeName
    );

    internal sealed record DecoratorInfo(
        string DecoratorFqn,
        string OpenGenericFqn,
        EquatableArray<DecoratorDependency> ExtraDependencies,
        bool HasAppliesTo
    );

    // A constructor dependency of a pipeline decorator (besides the inner handler). A regular service is
    // resolved from DI; HandlerMetadata is supplied by the generator with the concrete handler type, so
    // attribute-driven decorators see the true handler regardless of their position in the chain.
    internal sealed record DecoratorDependency(
        string Fqn,
        bool IsHandlerMetadata
    );

    internal sealed record CacheableInfo(
        EquatableArray<string> Tags,
        int DurationSeconds,
        int ScopeValue,
        EquatableArray<CacheKeyPropertyInfo> KeyProperties,
        string? ResultValueFqn
    );

    internal sealed record CacheInvalidationInfo(
        EquatableArray<string> Tags,
        int ScopeValue
    );

    internal sealed record CacheKeyPropertyInfo(string Name);

    private static readonly DiagnosticDescriptor CacheableAndInvalidatingDescriptor = new(
        "ELCACHE001",
        "Handler cannot be both cacheable and cache-invalidating",
        "Handler '{0}' cannot use both CacheableAttribute and CacheInvalidateAttribute",
        "Elarion.Abstractions.Caching",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor EmptyCacheTagsDescriptor = new(
        "ELCACHE002",
        "Handler cache tags are required",
        "Handler '{0}' must define at least one non-empty cache tag",
        "Elarion.Abstractions.Caching",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor InvalidCacheTagDescriptor = new(
        "ELCACHE003",
        "Handler cache tag is invalid",
        "Handler '{0}' contains invalid cache tag '{1}'",
        "Elarion.Abstractions.Caching",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor InvalidCacheDurationDescriptor = new(
        "ELCACHE004",
        "Handler cache duration is invalid",
        "Handler '{0}' must define a positive cache duration",
        "Elarion.Abstractions.Caching",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor CacheableOnEventConsumerDescriptor = new(
        "ELCACHE005",
        "Event-consumer handler cannot be cacheable",
        "Handler '{0}' handles event '{1}', so [Cacheable] would cache the fan-out result and silently skip the "
        + "side effect on a legitimate re-delivery; remove [Cacheable] from the event consumer",
        "Elarion.Abstractions.Caching",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor UnsupportedCacheKeyPropertyTypeDescriptor = new(
        "ELCACHE006",
        "Cache-key property type is not supported",
        "Handler '{0}' includes request property '{1}' of type '{2}' in its cache key, but that type has no stable "
        + "key formatting (it would fall back to object.ToString(), colliding across values and risking a "
        + "cross-request cache leak); use a scalar property type (primitive, string, char, bool, Guid, enum, "
        + "DateTime/DateTimeOffset/DateOnly/TimeOnly/TimeSpan, decimal, or a Nullable of those), or restrict the "
        + "cache key with the KeyProperties argument of [Cacheable]",
        "Elarion.Abstractions.Caching",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor CacheKeyPropertyNotFoundDescriptor = new(
        "ELCACHE007",
        "Cache-key property does not exist",
        "Handler '{0}' declares [Cacheable(KeyProperties = ...)] naming '{1}', which is not a public instance "
        + "property on request type '{2}'; the name would be silently dropped from the key. Reference an existing "
        + "property (e.g. nameof(Request.Id)).",
        "Elarion.Abstractions.Caching",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor ResilientOnDomainEventDescriptor = new(
        "ELPIPE003",
        "Domain-event consumer cannot be resilient",
        "Handler '{0}' consumes domain event '{1}', which is dispatched inline within the publisher's "
        + "transaction; [Resilient] would let Polly retry the handler inside that live transaction, re-applying "
        + "tracked mutations. Remove [Resilient] (integration-event consumers run on a fresh scope and may be "
        + "resilient).",
        "Elarion.Abstractions.Resilience",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor ResilientCommandWithoutIdempotentDescriptor = new(
        "ELPIPE004",
        "Retrying resilient command handler is not idempotent",
        "Handler '{0}' applies resilience policy '{1}' to command '{2}' without [Idempotent]. A retrying policy "
        + "can execute the command twice: the resilience decorator wraps the transaction, whose finalizing "
        + "commit deliberately runs uncancellable — a per-attempt timeout abandons the attempt without waiting "
        + "for it, the in-flight commit still completes in the background, the timeout is not a cancellation so "
        + "the retry fires, and the command executes and commits again. Add [Idempotent] so a retry replays the "
        + "first committed outcome instead of re-executing. Timeout-only policies declared in this assembly are "
        + "recognized and do not warn; suppress this diagnostic if the policy is declared elsewhere and never "
        + "retries.",
        "Elarion.Abstractions.Resilience",
        DiagnosticSeverity.Warning,
        true);

    private static readonly DiagnosticDescriptor AuthorizationResponseNotFailureCapable = new(
        "ELAUTH001",
        "Authorized handler response cannot represent failure",
        "Handler '{0}' declares an authorization requirement but its response type '{1}' does not implement "
        + "IResultFailureFactory<T>, so the authorization check cannot short-circuit and would be silently "
        + "skipped; return Result<T> or Result",
        "Elarion.Abstractions.Authorization",
        DiagnosticSeverity.Error,
        true);

    internal static readonly DiagnosticDescriptor ResourceIdPathNotFound = new(
        "ELAUTH002",
        "RequireResource id path does not resolve",
        "Handler '{0}' declares [RequireResource] with Id path '{1}', which does not resolve to a property on "
        + "request type '{2}'; use nameof(Request.Id) or a dotted path of existing properties",
        "Elarion.Abstractions.Authorization",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor FeatureGateResponseNotFailureCapable = new(
        "ELFEAT001",
        "Feature-gated handler response cannot represent failure",
        "Handler '{0}' declares a [FeatureGate] but its response type '{1}' does not implement "
        + "IResultFailureFactory<T>, so the gate cannot short-circuit and would be silently skipped; "
        + "return Result<T> or Result",
        "Elarion.Abstractions.Features",
        DiagnosticSeverity.Error,
        true);

    internal static readonly DiagnosticDescriptor EmptyFeatureGateDescriptor = new(
        "ELFEAT002",
        "FeatureGate declares no feature name",
        "Handler '{0}' declares a [FeatureGate] with no feature name (or a blank one); the gate has no effect",
        "Elarion.Abstractions.Features",
        DiagnosticSeverity.Warning,
        true);

    private static readonly DiagnosticDescriptor ValidationResponseNotFailureCapable = new(
        "ELVAL001",
        "Validated handler response cannot represent failure",
        "Handler '{0}' has a request type carrying validation constraints but its response type '{1}' does not "
        + "implement IResultFailureFactory<T>, so the validation check cannot short-circuit and would be "
        + "silently skipped; return Result<T> or Result",
        "Elarion.Abstractions.Validation",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor IdempotentResponseNotFailureCapable = new(
        "ELIDEM001",
        "Idempotent handler response cannot represent failure",
        "Handler '{0}' declares [Idempotent] but its response type '{1}' does not implement "
        + "IResultFailureFactory<T>, so the idempotency decorator cannot synthesize the 400/409/422/replay "
        + "outcomes and would be silently skipped; return Result<T> or Result",
        "Elarion.Abstractions.Idempotency",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor IdempotentOnNonCommandDescriptor = new(
        "ELIDEM002",
        "[Idempotent] handler is not a command",
        "Handler '{0}' declares [Idempotent] but its request type '{1}' is not an ICommand; idempotency only "
        + "applies to state-changing commands, so the attribute has no effect",
        "Elarion.Abstractions.Idempotency",
        DiagnosticSeverity.Warning,
        true);

    private static readonly DiagnosticDescriptor InvalidIdempotentRetentionDescriptor = new(
        "ELIDEM003",
        "[Idempotent] retention is invalid",
        "Handler '{0}' must declare a positive RetentionHours on [Idempotent]",
        "Elarion.Abstractions.Idempotency",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor IdempotentAndCacheableDescriptor = new(
        "ELIDEM004",
        "Handler cannot be both idempotent and cacheable",
        "Handler '{0}' cannot use both [Idempotent] and [Cacheable]; caching is for queries, idempotency for "
        + "commands",
        "Elarion.Abstractions.Idempotency",
        DiagnosticSeverity.Warning,
        true);

    private static readonly DiagnosticDescriptor SingletonHandlerScopedDependencyDescriptor = new(
        "ELSG011",
        "Singleton handler dependency must be singleton",
        "Handler '{0}' declares [Handler(Scope = ServiceScope.Singleton)], but constructor dependency '{1}' is "
        + "registered {2}. A singleton handler is constructed once from the root provider, so a per-dispatch "
        + "dependency would be captured for the application lifetime; make the dependency singleton or keep "
        + "the handler scoped.",
        "Elarion.Generators",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor SingletonHandlerUnverifiableDependencyDescriptor = new(
        "ELSG012",
        "Singleton handler dependency lifetime cannot be verified",
        "Handler '{0}' declares [Handler(Scope = ServiceScope.Singleton)], but constructor dependency '{1}' "
        + "cannot be verified as singleton at compile time. Annotate its implementation with "
        + "[Service(Scope = ServiceScope.Singleton)] in this assembly, or keep the handler scoped — a "
        + "captive scoped dependency must be impossible to ship, not a runtime surprise.",
        "Elarion.Generators",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor SingletonHandlerScopedPipelineDescriptor = new(
        "ELSG013",
        "Singleton handler cannot use a scope-dependent pipeline feature",
        "Handler '{0}' declares [Handler(Scope = ServiceScope.Singleton)] but its pipeline attaches {1}, "
        + "which resolves per-dispatch services when the chain is built; remove the feature or keep the "
        + "handler scoped",
        "Elarion.Generators",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor AllowDuplicatesOnNonIntegrationEventDescriptor = new(
        "ELINBX001",
        "[AllowDuplicates] handler is not an integration-event consumer",
        "Handler '{0}' declares [AllowDuplicates] but its request type '{1}' is not an IIntegrationEvent; only "
        + "handler-form integration-event consumers have a default-on inbox to opt out of (domain events run "
        + "inline in the publisher's transaction and are exactly-once by atomicity), so the attribute has no effect",
        "Elarion.Abstractions.Messaging",
        DiagnosticSeverity.Warning,
        true);
}

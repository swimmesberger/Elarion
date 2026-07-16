using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Elarion.Generators;

public sealed partial class HandlerRegistrationGenerator {
    /// <summary>
    /// Stage-two resolution: binds each cached candidate against the CURRENT compilation (symbol-table lookups
    /// only, never a tree scan) and parses the full pipeline. The compilation is combined in deliberately — the
    /// effective [DecoratorList] can come from the handler, its [AppModule], a pipeline-attribute class, or the
    /// assembly, and [Require*]/[FeatureGate] can sit on base classes in other files, so this state must stay
    /// fresh. The result is value-equatable, so emission is skipped when no handler model changed.
    /// </summary>
    private static EquatableArray<HandlerInfo> ResolveHandlers(
        EquatableArray<string> candidates,
        EquatableArray<ModuleScanner.Module> modules,
        EquatableArray<string> variantContracts,
        EquatableArray<string> noRetryPolicies,
        Compilation compilation,
        CancellationToken ct) {
        if (candidates.IsEmpty)
            return EquatableArray<HandlerInfo>.Empty;

        var (moduleDecoratorLists, moduleAuthDefaults, moduleAuditDefaults) = BuildModuleMaps(compilation, modules, ct);
        var assemblyRequireAuthenticated = ResolveAssemblyAuthorizationDefault(compilation);
        var assemblyAuditDefault = ResolveAssemblyAuditDefault(compilation);
        var variantContractSet = variantContracts.IsEmpty
            ? null
            : new HashSet<string>(variantContracts.AsImmutableArray, StringComparer.Ordinal);
        var noRetryPolicySet = noRetryPolicies.IsEmpty
            ? null
            : new HashSet<string>(noRetryPolicies.AsImmutableArray, StringComparer.Ordinal);
        // ValidationDecorator auto-attach is conditional on the compilation referencing Elarion.Validation (the
        // enforcement package). Without it, no decorator attaches — the ValidationResolverGenerator reports
        // ELVAL002 so the unenforced attributes are a visible choice. The walk context memoizes the validatable
        // graph across all handlers of this pass.
        var validationWalk = compilation.GetTypeByMetadataName(ElarionValidationExtensionsMetadataName) is null
            ? null
            : new ValidatableTypeWalker.Context(compilation.Assembly);
        var builder = ImmutableArray.CreateBuilder<HandlerInfo>();
        foreach (var metadataName in candidates) {
            ct.ThrowIfCancellationRequested();
            // Source-assembly lookup: handlers are declared in this compilation, and the assembly-scoped lookup
            // cannot be shadowed by a same-named type in a referenced assembly.
            if (compilation.Assembly.GetTypeByMetadataName(metadataName) is not { } classSymbol)
                continue;

            var classDecl = FindDeclaration(classSymbol, ct);
            if (classDecl is null)
                continue;

            var info = GetHandlerInfo(
                classDecl, classSymbol, compilation, moduleDecoratorLists, moduleAuthDefaults,
                assemblyRequireAuthenticated, moduleAuditDefaults, assemblyAuditDefault, modules,
                variantContractSet, noRetryPolicySet, validationWalk, ct);
            if (info is not null)
                builder.Add(info);
        }

        return builder.ToImmutable();
    }

    // The declaration used for diagnostic locations. Prefer the declaration that carries the base list (the one
    // stage one matched), falling back to the first for exotic partial splits.
    private static ClassDeclarationSyntax? FindDeclaration(INamedTypeSymbol classSymbol, CancellationToken ct) {
        ClassDeclarationSyntax? first = null;
        foreach (var reference in classSymbol.DeclaringSyntaxReferences) {
            if (reference.GetSyntax(ct) is not ClassDeclarationSyntax decl)
                continue;

            first ??= decl;
            if (decl.BaseList is not null)
                return decl;
        }

        return first;
    }

    // The contracts a [FeatureVariant] class is selected for — exactly what its [Service] registers under (shared
    // resolver), so a handler injecting any of them is wrapped in the async-resolving proxy. A [FeatureVariant]
    // without [Service] is reported by the variant generator (ELVAR007); here it simply contributes no contracts.
    private static EquatableArray<string> GetVariantContractFqns(GeneratorAttributeSyntaxContext ctx) {
        if (ctx.TargetSymbol is not INamedTypeSymbol classSymbol)
            return EquatableArray<string>.Empty;

        var serviceAttr = ServiceContractResolver.FindServiceAttribute(classSymbol);
        if (serviceAttr is null)
            return EquatableArray<string>.Empty;

        return ServiceContractResolver.ResolveContractFqns(
            classSymbol, serviceAttr, SymbolDisplayFormat.FullyQualifiedFormat);
    }

    private static EquatableArray<string> FlattenSortedDistinct(ImmutableArray<EquatableArray<string>> groups) {
        if (groups.IsDefaultOrEmpty)
            return EquatableArray<string>.Empty;

        return groups
            .SelectMany(static group => group.AsImmutableArray)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static HandlerInfo? GetHandlerInfo(
        ClassDeclarationSyntax classDecl,
        INamedTypeSymbol classSymbol,
        Compilation compilation,
        IReadOnlyList<(string Namespace, AttributeData DecoratorList)> moduleDecoratorLists,
        IReadOnlyList<(string Namespace, bool RequireAuthenticated)> moduleAuthDefaults,
        bool assemblyRequireAuthenticated,
        IReadOnlyList<string> moduleAuditDefaults,
        bool assemblyAuditDefault,
        EquatableArray<ModuleScanner.Module> modules,
        HashSet<string>? variantContracts,
        HashSet<string>? noRetryPolicies,
        ValidatableTypeWalker.Context? validationWalk,
        CancellationToken ct) {
        ct.ThrowIfCancellationRequested();

        // Re-validated against the current compilation: a cached candidate can drift (e.g. its base class in
        // another file stopped implementing IHandler<,>) without its own tree changing.
        if (classSymbol.IsAbstract || classSymbol.IsGenericType)
            return null;

        var handlerInterface = FindHandlerInterface(classSymbol);
        if (handlerInterface is null)
            return null;

        var fmt = SymbolDisplayFormat.FullyQualifiedFormat;
        var requestType = handlerInterface.TypeArguments[0];
        var responseType = handlerInterface.TypeArguments[1];
        var requestFqn = requestType.ToDisplayString(fmt);
        var responseFqn = responseType.ToDisplayString(fmt);
        var handlerFqn = classSymbol.ToDisplayString(fmt);
        var handlerName = classSymbol.Name;
        var ns = classSymbol.ContainingNamespace?.ToDisplayString() ?? "";

        var diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();

        var decoratorListAttr = ResolveDecoratorListFromPipelineAttributes(classSymbol, compilation, moduleDecoratorLists);
        var decorators = decoratorListAttr is not null
            ? ParseDecorators(decoratorListAttr, requestType, responseType, compilation, diagnostics, fmt)
            : ImmutableArray<DecoratorInfo>.Empty;

        var isEventConsumer = IsEventConsumerRequest(requestType, compilation);
        var cacheable = ParseCacheable(
            classDecl, classSymbol, requestType, responseType, compilation, fmt, isEventConsumer, diagnostics);
        var cacheInvalidation = ParseCacheInvalidation(classSymbol, compilation);
        var resiliencePolicyName = ParseResilient(classDecl, classSymbol, requestType, compilation, diagnostics);
        diagnostics.AddRange(ValidateCacheMetadata(classSymbol, cacheable, cacheInvalidation));

        var (hasAuthorization, requireAuthenticatedByDefault, resourceBindings) = ParseAuthorization(
            classDecl, classSymbol, requestType, responseType, compilation, moduleAuthDefaults,
            assemblyRequireAuthenticated, isEventConsumer, diagnostics);

        var hasFeatureGates = ParseFeatureGates(classDecl, classSymbol, responseType, compilation, diagnostics);

        var hasValidation = ParseValidation(
            classDecl, classSymbol, requestType, responseType, compilation, validationWalk, diagnostics);

        var idempotent = ParseIdempotent(
            classDecl, classSymbol, requestType, responseType, compilation, fmt, cacheable, diagnostics);

        // The inbox (ADR-0022): an integration-event consumer without explicit [Idempotent] gets a synthesized
        // Consumer-scoped policy by default, keyed on the delivered message id. [AllowDuplicates] opts out
        // (TransactionDecorator.AppliesTo mirrors this, handing the plain transaction back).
        idempotent ??= ParseInbox(
            classDecl, classSymbol, requestType, responseType, compilation, fmt, diagnostics);

        // ELPIPE004 (warning): a retrying [Resilient] policy around a command without [Idempotent] risks double
        // execution. ResilienceDecorator sits OUTSIDE TransactionDecorator, whose finalizing commit deliberately
        // runs on CancellationToken.None — so a per-attempt Polly timeout abandons the attempt without waiting
        // for the delegate, the in-flight commit completes in the background, TimeoutRejectedException is not an
        // OCE so the retry fires, and the command executes and commits twice. [Idempotent] turns the retry into
        // a replay of the first committed outcome. A policy provably declared without retry in this compilation
        // is exempt; an unknown name (referenced assembly, imperative registration) warns conservatively.
        // Integration-event consumers never reach here: the default inbox (or their non-command request when
        // [AllowDuplicates] opts out) keeps them off the command path.
        if (resiliencePolicyName is not null && idempotent is null &&
            RequestImplementsMarker(requestType, compilation, CommandMarkerMetadataName) &&
            noRetryPolicies?.Contains(resiliencePolicyName) != true) {
            diagnostics.Add(DiagnosticInfo.Create(
                ResilientCommandWithoutIdempotentDescriptor,
                classDecl.Identifier.GetLocation(),
                handlerFqn,
                resiliencePolicyName,
                requestFqn));
        }

        var audit = ParseAudit(
            classSymbol, requestType, compilation, moduleAuditDefaults, assemblyAuditDefault,
            isEventConsumer, modules);

        var variantContractDeps = GetVariantContractDeps(classSymbol, variantContracts, fmt);

        return new HandlerInfo(
            handlerFqn,
            handlerName,
            requestFqn,
            responseFqn,
            ns,
            decorators,
            resiliencePolicyName,
            cacheable,
            cacheInvalidation,
            hasAuthorization,
            requireAuthenticatedByDefault,
            resourceBindings,
            hasFeatureGates,
            hasValidation,
            idempotent,
            audit,
            variantContractDeps,
            // Event consumers register keyed by their own FQN so N of them coexist for one event; a command/query
            // stays unkeyed (exactly one handler per request, resolvable typed-direct).
            isEventConsumer ? handlerFqn : null,
            diagnostics.ToImmutable());
    }

    // Decides whether the framework ValidationDecorator attaches to this handler (ADR-0027). Attachment is a
    // compile-time presence decision like authorization/feature gating: the request type's graph carries
    // DataAnnotations validation metadata (the shared ValidatableTypeWalker computation, identical to what the
    // ValidationResolverGenerator registers) and the compilation references Elarion.Validation. A validation
    // failure returns TResponse.Failure(AppError.Validation(...)), so the response must implement
    // IResultFailureFactory<TResponse> — otherwise ELVAL001, mirroring ELAUTH001.
    private static bool ParseValidation(
        ClassDeclarationSyntax classDecl,
        INamedTypeSymbol classSymbol,
        ITypeSymbol requestType,
        ITypeSymbol responseType,
        Compilation compilation,
        ValidatableTypeWalker.Context? validationWalk,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics) {
        // No Elarion.Validation reference => no attach; the resolver generator's ELVAL002 already warns.
        if (validationWalk is null)
            return false;

        if (!ValidatableTypeWalker.IsValidatable(requestType, validationWalk))
            return false;

        if (!ResponseSupportsFailure(responseType, compilation)) {
            diagnostics.Add(DiagnosticInfo.Create(
                ValidationResponseNotFailureCapable,
                classDecl.Identifier.GetLocation(),
                classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                responseType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
            return false;
        }

        return true;
    }

    // A handler whose constructor injects a variant contract is registered behind the async-resolving proxy so the
    // contract's variant is awaited (per user) before the handler is built. Returns the distinct variant contracts
    // the handler depends on, in constructor-parameter order; empty for the common (no-variant) case.
    private const string FromKeyedServicesAttributeFqn =
        "Microsoft.Extensions.DependencyInjection.FromKeyedServicesAttribute";

    private static EquatableArray<string> GetVariantContractDeps(
        INamedTypeSymbol classSymbol,
        HashSet<string>? variantContracts,
        SymbolDisplayFormat fmt) {
        if (variantContracts is null)
            return EquatableArray<string>.Empty;

        var constructor = SelectConstructor(classSymbol);
        if (constructor is null)
            return EquatableArray<string>.Empty;

        ImmutableArray<string>.Builder? builder = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in constructor.Parameters) {
            // A [FromKeyedServices(...)] parameter pins a specific keyed implementation — DI resolves it
            // directly, bypassing variant selection — so it must not force the proxy (or warm a switch the
            // handler never consults; with no default and no allocation that warm would even fail the call).
            if (IsKeyedServiceParameter(parameter))
                continue;

            var parameterFqn = parameter.Type.ToDisplayString(fmt);
            if (variantContracts.Contains(parameterFqn) && seen.Add(parameterFqn)) {
                builder ??= ImmutableArray.CreateBuilder<string>();
                builder.Add(parameterFqn);
            }
        }

        return builder is null ? EquatableArray<string>.Empty : builder.ToImmutable();
    }

    private static bool IsKeyedServiceParameter(IParameterSymbol parameter) {
        foreach (var attribute in parameter.GetAttributes()) {
            if (attribute.AttributeClass?.ToDisplayString() == FromKeyedServicesAttributeFqn)
                return true;
        }

        return false;
    }

    // The constructor DI resolves: the public instance constructor with the most parameters (greedy selection).
    private static IMethodSymbol? SelectConstructor(INamedTypeSymbol classSymbol) {
        IMethodSymbol? best = null;
        foreach (var constructor in classSymbol.InstanceConstructors) {
            if (constructor.DeclaredAccessibility != Accessibility.Public)
                continue;

            if (best is null || constructor.Parameters.Length > best.Parameters.Length)
                best = constructor;
        }

        return best;
    }

    // True when the request implements the named event marker (IDomainEvent/IIntegrationEvent) — i.e. the handler
    // is an event-consumer handler. Used to keep pipeline pieces that assume a command/query off consumers.
    private static bool RequestImplementsMarker(ITypeSymbol requestType, Compilation compilation, string markerMetadataName) {
        var markerSymbol = compilation.GetTypeByMetadataName(markerMetadataName);
        if (markerSymbol is null)
            return false;

        foreach (var iface in requestType.AllInterfaces) {
            if (SymbolEqualityComparer.Default.Equals(iface, markerSymbol))
                return true;
        }

        return SymbolEqualityComparer.Default.Equals(requestType, markerSymbol);
    }

    private static bool IsEventConsumerRequest(ITypeSymbol requestType, Compilation compilation) =>
        RequestImplementsMarker(requestType, compilation, DomainEventMetadataName) ||
        RequestImplementsMarker(requestType, compilation, IntegrationEventMetadataName);

    private static INamedTypeSymbol? FindHandlerInterface(INamedTypeSymbol classSymbol) {
        foreach (var iface in classSymbol.AllInterfaces) {
            if (iface.OriginalDefinition.ToDisplayString() == "Elarion.Abstractions.IHandler<TRequest, TResponse>") {
                return iface;
            }
        }

        return null;
    }

    // [Resilient] is Inherited = false, so only the directly-declared attribute is read. A domain-event consumer
    // must never be resilient: its handler runs inline within the publisher's live transaction, so a Polly retry
    // would re-apply tracked mutations — ELPIPE003, and the policy is dropped so no ResilienceDecorator attaches.
    // (Integration-event consumers run on a fresh post-commit scope, so they may be resilient.)
    private static string? ParseResilient(
        ClassDeclarationSyntax classDecl,
        INamedTypeSymbol classSymbol,
        ITypeSymbol requestType,
        Compilation compilation,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics) {
        var attribute = classSymbol.GetAttributes()
            .FirstOrDefault(candidate => candidate.AttributeClass?.ToDisplayString() == ResilientAttributeMetadataName);
        if (attribute is null ||
            attribute.ConstructorArguments.Length == 0 ||
            attribute.ConstructorArguments[0].Value is not string policyName ||
            string.IsNullOrWhiteSpace(policyName)) {
            return null;
        }

        if (RequestImplementsMarker(requestType, compilation, DomainEventMetadataName)) {
            diagnostics.Add(DiagnosticInfo.Create(
                ResilientOnDomainEventDescriptor,
                classDecl.Identifier.GetLocation(),
                classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                requestType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
            return null;
        }

        return policyName;
    }

    // Reduces a [ResiliencePolicy] declaration to (name, retry-ness) for ELPIPE004. Retry-ness mirrors the
    // ResiliencePolicyRegistrationGenerator's parse: any retry option present enables retry generation with a
    // default MaxRetryAttempts of 3, so a declaration retries unless it configures no retry option at all
    // (timeout-only) or sets MaxRetryAttempts = 0 explicitly. Nameless/blank declarations are dropped — they
    // are ELRES001 territory and can never match a [Resilient] reference.
    private static PolicyRetryCandidate? GetPolicyRetryCandidate(GeneratorAttributeSyntaxContext ctx) {
        if (ctx.Attributes.Length == 0)
            return null;

        var attribute = ctx.Attributes[0];
        if (attribute.ConstructorArguments.Length == 0 ||
            attribute.ConstructorArguments[0].Value is not string name ||
            string.IsNullOrWhiteSpace(name)) {
            return null;
        }

        var hasRetryOption = false;
        var maxRetryAttempts = 3;
        foreach (var namedArgument in attribute.NamedArguments) {
            switch (namedArgument.Key) {
                case "MaxRetryAttempts":
                    hasRetryOption = true;
                    if (namedArgument.Value.Value is int max)
                        maxRetryAttempts = max;
                    break;
                case "Delay":
                case "Backoff":
                case "MaxDelay":
                case "UseJitter":
                    hasRetryOption = true;
                    break;
            }
        }

        return new PolicyRetryCandidate(name, hasRetryOption && maxRetryAttempts != 0);
    }

    // The provably-no-retry policy names: declared in this compilation and never with retry configured. A name
    // duplicated with any retrying declaration is excluded (duplicates are ELRES002 errors, but this stage must
    // not pick a winner). Sorted for a deterministic, value-equatable pipeline output.
    private static EquatableArray<string> FlattenNoRetryPolicyNames(
        ImmutableArray<PolicyRetryCandidate?> candidates) {
        if (candidates.IsDefaultOrEmpty)
            return EquatableArray<string>.Empty;

        HashSet<string>? retrying = null;
        HashSet<string>? noRetry = null;
        foreach (var candidate in candidates) {
            if (candidate is null)
                continue;

            if (candidate.HasRetry)
                (retrying ??= new HashSet<string>(StringComparer.Ordinal)).Add(candidate.Name);
            else
                (noRetry ??= new HashSet<string>(StringComparer.Ordinal)).Add(candidate.Name);
        }

        if (noRetry is null)
            return EquatableArray<string>.Empty;

        if (retrying is not null)
            noRetry.ExceptWith(retrying);

        return noRetry
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    // Yields every attribute declared on the handler and — because the authorization/feature-gate attributes all
    // declare Inherited = true — on each of its base types, from most-derived to least. This matches the runtime
    // HandlerMetadata path, which reads attributes with GetCustomAttributes(inherit: true). Ordering the
    // most-derived first means an [AllowAnonymous] override on a derived handler is seen before any base
    // requirement (though presence anywhere still opens the handler). Attributes whose own definition is
    // Inherited = false (e.g. [Cacheable]/[Idempotent]/[Resilient]) must NOT use this walker — they are read via
    // classSymbol.GetAttributes() so they stay directly-declared-only, consistent with their declaration.
    private static IEnumerable<AttributeData> EnumerateInheritedAttributes(INamedTypeSymbol classSymbol) {
        for (INamedTypeSymbol? current = classSymbol; current is not null; current = current.BaseType) {
            foreach (var attribute in current.GetAttributes())
                yield return attribute;
        }
    }

    // Decides whether the authorization decorator attaches to this handler, and whether it enforces an
    // authenticated principal by default. The decorator is auto-appended (not listed in a [DecoratorList]), so
    // attachment is a compile-time presence decision the generator makes by inspecting the handler symbol — an
    // AppliesTo predicate is a runtime gate that always emits the decorator. A default policy
    // ([ElarionAuthorizationDefaults] at module/assembly scope) attaches to every non-anonymous handler.
    private static (bool HasAuthorization, bool RequireAuthenticatedByDefault, EquatableArray<ResourceBindingInfo> ResourceBindings) ParseAuthorization(
        ClassDeclarationSyntax classDecl,
        INamedTypeSymbol classSymbol,
        ITypeSymbol requestType,
        ITypeSymbol responseType,
        Compilation compilation,
        IReadOnlyList<(string Namespace, bool RequireAuthenticated)> moduleAuthDefaults,
        bool assemblyRequireAuthenticated,
        bool isEventConsumer,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics) {
        EquatableArray<ResourceBindingInfo> noBindings = ImmutableArray<ResourceBindingInfo>.Empty;

        var isAllowAnonymous = false;
        var hasExplicit = false;
        var resourceAttributes = new List<AttributeData>();
        // The authorization attributes all declare Inherited = true, so a [Require*]/[AllowAnonymous] on a BASE
        // handler class must be honored — mirroring the runtime HandlerMetadata path, which reads them via
        // GetCustomAttributes(inherit: true). Walk the base-type chain; the most-derived [AllowAnonymous]
        // still wins because presence anywhere in the chain opens the handler (CLR inherit:true semantics for a
        // single, AllowMultiple=false attribute), and the AllowMultiple=true [Require*] accumulate across it.
        foreach (var attribute in EnumerateInheritedAttributes(classSymbol)) {
            switch (attribute.AttributeClass?.ToDisplayString()) {
                case AllowAnonymousAttributeMetadataName:
                    isAllowAnonymous = true;
                    break;
                case RequireClaimAttributeMetadataName:
                case RequirePermissionAttributeMetadataName:
                case RequireRoleAttributeMetadataName:
                case RequirePolicyAttributeMetadataName:
                    hasExplicit = true;
                    break;
                case RequireResourceAttributeMetadataName:
                    hasExplicit = true;
                    resourceAttributes.Add(attribute);
                    break;
            }
        }

        // AllowAnonymous wins: the handler is public, so no decorator is attached.
        if (isAllowAnonymous)
            return (false, false, noBindings);

        // An event-consumer handler (request : IDomainEvent/IIntegrationEvent) is dispatched on a delivery scope
        // with no authenticated user, so the IMPLICIT deny-by-default from [ElarionAuthorizationDefaults] must not
        // attach — it would fail every consumer. An EXPLICIT [Require*] on a consumer still attaches (a deliberate
        // app choice), so only the default-driven attachment is suppressed here.
        var defaultRequireAuthenticated = !isEventConsumer &&
            ResolveAuthorizationDefault(classSymbol, moduleAuthDefaults, assemblyRequireAuthenticated);
        if (!hasExplicit && !defaultRequireAuthenticated)
            return (false, false, noBindings);

        // Denial returns TResponse.Failure(...), which needs IResultFailureFactory<TResponse>. Without it the
        // check could not short-circuit and would be silently skipped — report ELAUTH001 instead of failing open.
        if (!ResponseSupportsFailure(responseType, compilation)) {
            diagnostics.Add(DiagnosticInfo.Create(
                AuthorizationResponseNotFailureCapable,
                classDecl.Identifier.GetLocation(),
                classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                responseType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
            return (false, false, noBindings);
        }

        var bindings = BuildResourceBindings(classDecl, classSymbol, requestType, resourceAttributes, diagnostics);
        return (true, defaultRequireAuthenticated, bindings);
    }

    // ADR-0012 Tier 1: a [RequireResource] references the resource id by a compile-checked path on the request.
    // The path is validated against the request type and emitted as a typed accessor; an unresolvable path is
    // ELAUTH002 (never a runtime surprise).
    internal static EquatableArray<ResourceBindingInfo> BuildResourceBindings(
        ClassDeclarationSyntax classDecl,
        INamedTypeSymbol classSymbol,
        ITypeSymbol requestType,
        List<AttributeData> resourceAttributes,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics) {
        if (resourceAttributes.Count == 0)
            return ImmutableArray<ResourceBindingInfo>.Empty;

        var fmt = SymbolDisplayFormat.FullyQualifiedFormat;
        var builder = ImmutableArray.CreateBuilder<ResourceBindingInfo>();
        foreach (var attribute in resourceAttributes) {
            if (attribute.ConstructorArguments.Length == 0 ||
                attribute.ConstructorArguments[0].Value is not INamedTypeSymbol resourceType)
                continue;

            var operation = "read";
            var idPath = "Id";
            string? resourceTypeName = null;
            foreach (var named in attribute.NamedArguments) {
                if (named.Key == "Operation" && named.Value.Value is string op && op.Length > 0)
                    operation = op;
                else if (named.Key == "Id" && named.Value.Value is string id && id.Length > 0)
                    idPath = id;
                else if (named.Key == "ResourceTypeName" && named.Value.Value is string typeName && typeName.Length > 0)
                    resourceTypeName = typeName;
            }

            if (!ResourcePathResolves(requestType, idPath)) {
                diagnostics.Add(DiagnosticInfo.Create(
                    ResourceIdPathNotFound,
                    classDecl.Identifier.GetLocation(),
                    classSymbol.ToDisplayString(fmt),
                    idPath,
                    requestType.ToDisplayString(fmt)));
                continue;
            }

            builder.Add(new ResourceBindingInfo(resourceType.ToDisplayString(fmt), operation, idPath, resourceTypeName));
        }

        return builder.ToImmutable();
    }

    internal static bool ResourcePathResolves(ITypeSymbol requestType, string path) {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var current = requestType;
        foreach (var rawSegment in path.Split('.')) {
            var segment = rawSegment.Trim();
            if (segment.Length == 0)
                return false;

            var property = FindPublicInstanceProperty(current, segment);
            if (property is null)
                return false;

            current = property.Type;
        }

        return true;
    }

    private static IPropertySymbol? FindPublicInstanceProperty(ITypeSymbol type, string name) {
        for (ITypeSymbol? current = type; current is not null; current = current.BaseType) {
            foreach (var member in current.GetMembers(name)) {
                if (member is IPropertySymbol { IsStatic: false, GetMethod: { DeclaredAccessibility: Accessibility.Public } } property &&
                    property.DeclaredAccessibility == Accessibility.Public) {
                    return property;
                }
            }
        }

        return null;
    }

    private static bool ResolveAuthorizationDefault(
        INamedTypeSymbol classSymbol,
        IReadOnlyList<(string Namespace, bool RequireAuthenticated)> moduleAuthDefaults,
        bool assemblyRequireAuthenticated) {
        var handlerNamespace = classSymbol.ContainingNamespace?.ToDisplayString() ?? "";
        var hasModule = false;
        var bestValue = false;
        var bestNamespaceLength = -1;

        // Most-specific-wins: the handler's nearest module overrides the assembly default.
        foreach (var (moduleNamespace, requireAuthenticated) in moduleAuthDefaults) {
            if (!IsNamespaceInScope(handlerNamespace, moduleNamespace) ||
                moduleNamespace.Length <= bestNamespaceLength) {
                continue;
            }

            hasModule = true;
            bestValue = requireAuthenticated;
            bestNamespaceLength = moduleNamespace.Length;
        }

        return hasModule ? bestValue : assemblyRequireAuthenticated;
    }

    // Decides whether the audit decorators attach (ADR-0045). Attachment is a compile-time presence decision
    // like authorization: an explicit [Auditable] (unless Enabled = false), or [ElarionAuditDefaults] at
    // module/assembly scope — under which every ICommand handler is audited (queries and event consumers stay
    // explicit-only; a consumer runs on a delivery scope with no caller, so default-driven attachment would
    // only produce actor-less noise). No IResultFailureFactory guard is needed: the decorators never
    // short-circuit, they only observe. Attachment is additionally soft at runtime (no IAuditTrail => no-op),
    // so no diagnostic fires for a missing sink.
    private static AuditInfo? ParseAudit(
        INamedTypeSymbol classSymbol,
        ITypeSymbol requestType,
        Compilation compilation,
        IReadOnlyList<string> moduleAuditDefaults,
        bool assemblyAuditDefault,
        bool isEventConsumer,
        EquatableArray<ModuleScanner.Module> modules) {
        AttributeData? auditable = null;
        // [Auditable] declares Inherited = false, but mirror the other gates' base-chain walk deliberately: a
        // shared audited base handler is a reasonable shape, and the runtime HandlerMetadata read is inherit-aware.
        foreach (var attribute in EnumerateInheritedAttributes(classSymbol)) {
            if (attribute.AttributeClass?.ToDisplayString() == AuditableAttributeMetadataName) {
                auditable = attribute;
                break;
            }
        }

        if (auditable is not null) {
            foreach (var named in auditable.NamedArguments) {
                if (named.Key == "Enabled" && named.Value.Value is false)
                    return null;
            }
        } else {
            if (isEventConsumer ||
                !RequestImplementsMarker(requestType, compilation, CommandMarkerMetadataName) ||
                !ResolveAuditDefault(classSymbol, moduleAuditDefaults, assemblyAuditDefault)) {
                return null;
            }
        }

        var handlerNamespace = classSymbol.ContainingNamespace?.ToDisplayString() ?? "";
        var module = ModuleScanner.FindBest(handlerNamespace, modules.AsImmutableArray);
        return new AuditInfo(ResolveAuditAction(classSymbol, module?.Name), module?.Name);
    }

    private static bool ResolveAuditDefault(
        INamedTypeSymbol classSymbol,
        IReadOnlyList<string> moduleAuditDefaults,
        bool assemblyAuditDefault) {
        if (assemblyAuditDefault)
            return true;

        var handlerNamespace = classSymbol.ContainingNamespace?.ToDisplayString() ?? "";
        foreach (var moduleNamespace in moduleAuditDefaults) {
            if (IsNamespaceInScope(handlerNamespace, moduleNamespace))
                return true;
        }

        return false;
    }

    private static bool ResolveAssemblyAuditDefault(Compilation compilation) {
        var defaultsAttr = compilation.GetTypeByMetadataName(AuditDefaultsAttributeMetadataName);
        if (defaultsAttr is null)
            return false;

        return compilation.Assembly.GetAttributes()
            .Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, defaultsAttr));
    }

    // The audit record's action name, resolved exactly like the RPC map resolves a wire name (an explicit
    // [Handler("…")] verbatim, else "{camelCasedModule}.{inferredOperation}") so audit records and the exported
    // schema agree on names. A handler with no [Handler] attribute still gets the name it WOULD have.
    private static string ResolveAuditAction(INamedTypeSymbol classSymbol, string? moduleName) {
        foreach (var attribute in classSymbol.GetAttributes()) {
            if (attribute.AttributeClass?.ToDisplayString() != RpcMethodEmission.HandlerAttributeMetadataName)
                continue;

            if (attribute.ConstructorArguments.Length > 0 &&
                attribute.ConstructorArguments[0].Value is string explicitName &&
                explicitName.Length > 0) {
                return explicitName;
            }

            break;
        }

        var operation = RpcMethodEmission.InferOperationName(classSymbol.Name);
        return moduleName is null ? operation : $"{RpcMethodEmission.CamelCaseModule(moduleName)}.{operation}";
    }

    private static bool ResolveAssemblyAuthorizationDefault(Compilation compilation) {
        var defaultsAttr = compilation.GetTypeByMetadataName(AuthorizationDefaultsAttributeMetadataName);
        if (defaultsAttr is null)
            return false;

        var attribute = compilation.Assembly.GetAttributes()
            .FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, defaultsAttr));
        return attribute is not null && ReadRequireAuthenticated(attribute);
    }

    private static bool ReadRequireAuthenticated(AttributeData attribute) {
        foreach (var namedArgument in attribute.NamedArguments) {
            if (namedArgument.Key == "RequireAuthenticated" && namedArgument.Value.Value is bool value)
                return value;
        }

        // The attribute's RequireAuthenticated property defaults to true when not explicitly set.
        return true;
    }

    private static bool ResponseSupportsFailure(ITypeSymbol responseType, Compilation compilation) {
        var failureFactory = compilation.GetTypeByMetadataName(ResultFailureFactoryMetadataName);
        if (failureFactory is null)
            return false;

        var constructed = failureFactory.Construct(responseType);
        return SatisfiesType(responseType, constructed);
    }

    // Decides whether the feature-gate decorator attaches to this handler. Like authorization, attachment is a
    // compile-time presence decision (any [FeatureGate] on the handler), and a closed gate returns
    // TResponse.Failure(AppError.NotFound(...)) — so the response must implement IResultFailureFactory<TResponse>
    // (ELFEAT001) or the gate would be silently skipped. A [FeatureGate] with no/blank feature name is reported
    // (ELFEAT002) but is otherwise inert.
    private static bool ParseFeatureGates(
        ClassDeclarationSyntax classDecl,
        INamedTypeSymbol classSymbol,
        ITypeSymbol responseType,
        Compilation compilation,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics) {
        // [FeatureGate] declares Inherited = true, so gates on a base handler class apply too — walk the base
        // chain to match the runtime HandlerMetadata path (GetCustomAttributes(inherit: true)).
        var gateAttributes = EnumerateInheritedAttributes(classSymbol)
            .Where(candidate => candidate.AttributeClass?.ToDisplayString() == FeatureGateAttributeMetadataName)
            .ToList();
        if (gateAttributes.Count == 0)
            return false;

        var fmt = SymbolDisplayFormat.FullyQualifiedFormat;

        if (!ResponseSupportsFailure(responseType, compilation)) {
            diagnostics.Add(DiagnosticInfo.Create(
                FeatureGateResponseNotFailureCapable,
                classDecl.Identifier.GetLocation(),
                classSymbol.ToDisplayString(fmt),
                responseType.ToDisplayString(fmt)));
            return false;
        }

        foreach (var attribute in gateAttributes) {
            if (HasBlankFeatureName(attribute)) {
                diagnostics.Add(DiagnosticInfo.Create(
                    EmptyFeatureGateDescriptor,
                    classDecl.Identifier.GetLocation(),
                    classSymbol.ToDisplayString(fmt)));
            }
        }

        return gateAttributes.Any(HasEffectiveFeatureName);
    }

    // The feature names are the trailing `params string[]` constructor argument, which Roslyn surfaces as a single
    // array-kind TypedConstant regardless of which [FeatureGate] constructor was used.
    internal static bool HasBlankFeatureName(AttributeData attribute) {
        if (attribute.ConstructorArguments.Length == 0)
            return true;

        var featuresArg = attribute.ConstructorArguments[attribute.ConstructorArguments.Length - 1];
        if (featuresArg.Kind != TypedConstantKind.Array || featuresArg.Values.Length == 0)
            return true;

        foreach (var value in featuresArg.Values) {
            if (value.Value is not string feature || string.IsNullOrWhiteSpace(feature))
                return true;
        }

        return false;
    }

    internal static bool HasEffectiveFeatureName(AttributeData attribute) {
        if (attribute.ConstructorArguments.Length == 0)
            return false;

        var featuresArg = attribute.ConstructorArguments[attribute.ConstructorArguments.Length - 1];
        return featuresArg.Kind == TypedConstantKind.Array &&
            featuresArg.Values.Any(static value => value.Value is string feature && !string.IsNullOrWhiteSpace(feature));
    }
}

using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Elarion.Generators;

/// <summary>
/// Generates the typed surface over <c>[Actor]</c> classes (<c>Elarion.Actors</c>): a public facade
/// interface mirroring the actor's public async methods, an internal facade implementation whose
/// per-method work items invoke the actor statically (no reflection, AOT-safe), and a per-module
/// <c>Add{Module}Actors</c> registration wired into the module's <c>ConfigureDefaultServices</c>.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ActorRegistrationGenerator : IIncrementalGenerator {
    private const string TriggerAttributeMetadataName = "Elarion.Abstractions.GenerateActorsAttribute";
    private const string ActorAttributeMetadataName = "Elarion.Actors.ActorAttribute";
    private const string ReentrantAttributeDisplayName = "Elarion.Actors.ReentrantAttribute";
    private const string ConsumeEventAttributeDisplayName = "Elarion.Abstractions.Messaging.ConsumeEventAttribute";
    private const string ActorKeyAttributeDisplayName = "Elarion.Actors.ActorKeyAttribute";
    private const string IntegrationEventMetadataName = "Elarion.Abstractions.Messaging.IIntegrationEvent";
    private const string ActorContextMetadataName = "Elarion.Actors.IActorContext";
    private const string ActorContextGenericMetadataName = "Elarion.Actors.IActorContext`1";
    private const string ActorStateGenericMetadataName = "Elarion.Actors.IActorState`1";
    private const string CancellationTokenMetadataName = "System.Threading.CancellationToken";
    private const string TaskMetadataName = "System.Threading.Tasks.Task";
    private const string TaskGenericMetadataName = "System.Threading.Tasks.Task`1";
    private const string ValueTaskMetadataName = "System.Threading.Tasks.ValueTask";
    private const string ValueTaskGenericMetadataName = "System.Threading.Tasks.ValueTask`1";
    private const string AsyncEnumerableGenericMetadataName = "System.Collections.Generic.IAsyncEnumerable`1";

    // Facade signatures must preserve nullable reference annotations (a method returning
    // Task<Quote?> gets a work item and facade of Quote?, not Quote — the bare format drops the
    // annotation and the emitted `return await actor.Method(...)` then fails CS8603 under
    // warnings-as-errors). Applied to method results and parameters only; ctor/key/state types are
    // used in positions where the annotation is meaningless.
    private static readonly SymbolDisplayFormat NullableAwareFullyQualifiedFormat =
        SymbolDisplayFormat.FullyQualifiedFormat.AddMiscellaneousOptions(
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    private const string ActorSingletonKeyFqn = "global::Elarion.Actors.ActorSingletonKey";
    private const string UnitFqn = "global::Elarion.Abstractions.Results.Unit";

    private const string RelayResponseFqn =
        "global::Elarion.Abstractions.Result<global::Elarion.Abstractions.Results.Unit>";

    private const string CancellationTokenFqn = "global::System.Threading.CancellationToken";
    private const string TaskFqn = "global::System.Threading.Tasks.Task";
    private const string ValueTaskFqn = "global::System.Threading.Tasks.ValueTask";
    private const string AsyncEnumerableFqn = "global::System.Collections.Generic.IAsyncEnumerable";

    private static readonly DiagnosticDescriptor InvalidActorType = new(
        "ELACT001",
        "Invalid [Actor] type",
        "Type '{0}' is annotated with [Actor] but must be a non-static, non-abstract, non-generic, "
        + "non-nested class for the facade generator to wrap it",
        "Elarion.Generators",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor InvalidActorMethod = new(
        "ELACT002",
        "Invalid actor method",
        "Public method '{0}' on [Actor] class '{1}' cannot be exposed through the facade: actor methods "
        + "must be non-generic instance methods returning Task/Task<T>/ValueTask/ValueTask<T>/IAsyncEnumerable<T>, "
        + "without ref/out/in or ref-struct parameters and with at most one CancellationToken parameter",
        "Elarion.Generators",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor ActorNotInModule = new(
        "ELACT003",
        "Actor is not in any module",
        "Actor '{0}' is annotated with [Actor] but its namespace is not under any [AppModule]; "
        + "it will not be registered. Move the actor under a module's namespace so it is wired by that module.",
        "Elarion.Generators",
        DiagnosticSeverity.Warning,
        true);

    private static readonly DiagnosticDescriptor AmbiguousActorKey = new(
        "ELACT004",
        "Ambiguous actor key",
        "Actor '{0}' declares conflicting keys: use a single IActorContext<TKey> constructor parameter "
        + "(or one [Actor(KeyType = ...)] matching it) to make the actor keyed",
        "Elarion.Generators",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor InvalidActorConstructor = new(
        "ELACT005",
        "Invalid actor constructor",
        "Actor '{0}' must have exactly one public constructor so the generator can emit its activator",
        "Elarion.Generators",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor ActorEventKeyUnresolved = new(
        "ELACT008",
        "Actor event-consumer key cannot be resolved",
        "The [ConsumeEvent] method '{0}' on keyed actor '{1}' needs an actor key the generator cannot "
        + "determine: event '{2}' has {3} propert(y/ies) assignable to the key type. Add "
        + "[ActorKey(nameof({2}.KeyProperty))] naming the event property whose type is the actor's key type.",
        "Elarion.Generators",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor ActorConsumeMethodNotPublic = new(
        "ELACT009",
        "Actor [ConsumeEvent] method must be public",
        "The [ConsumeEvent] method '{0}' on actor '{1}' is not public. The generated relay reaches the actor "
        + "through its public facade (the same call a hand-written relay makes), so the method must be public.",
        "Elarion.Generators",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor ActorConsumeNotIntegrationEvent = new(
        "ELACT010",
        "Actor [ConsumeEvent] method must take one integration event",
        "The [ConsumeEvent] method '{0}' on actor '{1}' must take exactly one IIntegrationEvent parameter "
        + "(optionally with a CancellationToken). A domain event, or zero/multiple event parameters, is not a "
        + "valid actor consumer: a domain-event consumer shares the emitting command's transaction, which an "
        + "actor cannot. Consume an IIntegrationEvent, or call the actor from the command's handler after commit.",
        "Elarion.Generators",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor InvalidActorStreamMethod = new(
        "ELACT012",
        "Invalid actor stream method",
        "The stream method '{0}' on actor '{1}' (returning IAsyncEnumerable<T>) must not take a "
        + "CancellationToken and cannot be a [ConsumeEvent] consumer. The turn token's lifetime ends with "
        + "the attach turn — using it inside the returned stream would observe a recycled token; "
        + "cancellation flows through the enumerator instead (the facade adds the token parameter).",
        "Elarion.Generators",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor VirtualShardedActorMustBeKeyed = new(
        "ELACT013",
        "Virtual-sharded actor must be keyed",
        "Actor '{0}' uses Placement = VirtualShards but has no actor key; virtual-shard placement "
        + "requires an IActorContext<TKey> constructor parameter or Actor(KeyType = ...)",
        "Elarion.Generators",
        DiagnosticSeverity.Error,
        true);

    private enum ReturnShape {
        TaskVoid,
        TaskOfResult,
        ValueTaskVoid,
        ValueTaskOfResult,
        AsyncEnumerable
    }

    private enum CtorParameterKind {
        Context,
        State,
        Service
    }

    // For State the TypeFqn is the persisted state type (IActorState<T>'s argument), not the parameter type.
    private sealed record CtorParameterInfo(CtorParameterKind Kind, string TypeFqn);

    private sealed record MethodParameterInfo(string Name, string TypeFqn, bool IsCancellationToken);

    private sealed record ActorMethodInfo(
        string Name,
        ReturnShape Return,
        string ResultTypeFqn,
        EquatableArray<MethodParameterInfo> Parameters,
        string WorkItemClassName);

    // A [ConsumeEvent] method on an actor (ADR-0046): the generator emits a handler-form relay that
    // deduplicates the integration event through the inbox and calls this facade method by the resolved key.
    private sealed record ActorConsumerInfo(
        string MethodName,
        string EventTypeFqn,
        int Order,
        string? KeyExpression, // "request.OrderId" for a keyed actor; null for a singleton
        EquatableArray<string> CallArguments, // the facade call args in declared order ("request"/"cancellationToken")
        string RelayClassName);

    private sealed record ActorInfo(
        string ActorTypeFqn,
        string ActorNamespace,
        string ActorName,
        string FacadeInterfaceName,
        string FacadeImplName,
        string? KeyTypeFqn,
        bool Reentrant,
        int MailboxCapacity,
        bool MailboxFailFast,
        double IdleTimeoutSeconds,
        double CallTimeoutSeconds,
        string Placement,
        EquatableArray<CtorParameterInfo> CtorParameters,
        EquatableArray<ActorMethodInfo> Methods,
        EquatableArray<ActorConsumerInfo> Consumers,
        string HintName);

    /// <summary>A discovered actor: either an emission model or the diagnostics that rejected it.</summary>
    private sealed record ActorResult(ActorInfo? Actor, EquatableArray<DiagnosticInfo> Diagnostics);

    private static class TrackingNames {
        public const string Actors = "Actors";
        public const string Combined = "ActorsCombined";
    }

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context) {
        var results = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                ActorAttributeMetadataName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, ct) => CreateActorResult(ctx, ct))
            .Where(static result => result is not null)
            .Select(static (result, _) => result!)
            .Collect()
            .WithTrackingName(TrackingNames.Actors);

        var modules = ModuleProviders.CollectModules(context);
        var trigger = ModuleProviders.HasTrigger(context, TriggerAttributeMetadataName);

        var combined = results.Combine(modules).Combine(trigger).WithTrackingName(TrackingNames.Combined);

        context.RegisterSourceOutput(combined, static (spc, source) => {
            var ((results, modules), hasTrigger) = source;
            if (!hasTrigger) return;

            foreach (var result in results)
            foreach (var diagnostic in result.Diagnostics)
                spc.ReportDiagnostic(diagnostic.ToDiagnostic());

            var actors = new List<ActorInfo>();
            foreach (var result in results)
                if (result.Actor is not null)
                    actors.Add(result.Actor);

            actors.Sort(static (left, right) =>
                string.Compare(left.HintName, right.HintName, StringComparison.Ordinal));

            if (actors.Count == 0) return;

            foreach (var actor in actors) {
                spc.AddSource($"{actor.HintName}.Actor.g.cs", SourceText.From(GenerateFacade(actor), Encoding.UTF8));
                foreach (var consumer in actor.Consumers) {
                    if (actor.ActorNamespace.Length ==
                        0) continue; // a relay needs a namespace (an actor under a module always has one)

                    spc.AddSource(
                        $"{actor.HintName}__{consumer.MethodName}__EventRelay.g.cs",
                        SourceText.From(GenerateRelayClass(actor, consumer), Encoding.UTF8));
                    spc.AddSource(
                        $"{actor.HintName}__{consumer.MethodName}__EventRelayRegistration.g.cs",
                        SourceText.From(
                            HandlerRegistrationGenerator.GenerateRegistration(BuildRelayHandlerInfo(actor, consumer)),
                            Encoding.UTF8));
                }
            }

            EmitPerModule(spc, modules, actors);
        });
    }

    private static ActorResult? CreateActorResult(GeneratorAttributeSyntaxContext ctx,
        CancellationToken cancellationToken) {
        if (ctx.TargetSymbol is not INamedTypeSymbol type) return null;

        var location = LocationInfo.From(type);
        var typeDisplay = type.ToDisplayString();

        if (type.IsStatic || type.IsAbstract || type.IsGenericType || type.ContainingType is not null)
            return new ActorResult(null, new[] {
                DiagnosticInfo.Create(InvalidActorType, location, typeDisplay)
            }.ToEquatableArray());

        var compilation = ctx.SemanticModel.Compilation;
        var taskSymbol = compilation.GetTypeByMetadataName(TaskMetadataName);
        var taskGenericSymbol = compilation.GetTypeByMetadataName(TaskGenericMetadataName);
        var valueTaskSymbol = compilation.GetTypeByMetadataName(ValueTaskMetadataName);
        var valueTaskGenericSymbol = compilation.GetTypeByMetadataName(ValueTaskGenericMetadataName);
        var asyncEnumerableSymbol = compilation.GetTypeByMetadataName(AsyncEnumerableGenericMetadataName);
        var cancellationTokenSymbol = compilation.GetTypeByMetadataName(CancellationTokenMetadataName);
        var contextSymbol = compilation.GetTypeByMetadataName(ActorContextMetadataName);
        var contextGenericSymbol = compilation.GetTypeByMetadataName(ActorContextGenericMetadataName);
        var stateGenericSymbol = compilation.GetTypeByMetadataName(ActorStateGenericMetadataName);
        var integrationEventSymbol = compilation.GetTypeByMetadataName(IntegrationEventMetadataName);

        var diagnostics = new List<DiagnosticInfo>();

        // Attribute knobs.
        string? explicitName = null;
        ITypeSymbol? attributeKeyType = null;
        var mailboxCapacity = 0;
        var mailboxFailFast = false;
        double idleTimeoutSeconds = 0;
        double callTimeoutSeconds = 0;
        var placement = "Local";
        foreach (var named in ctx.Attributes[0].NamedArguments)
            switch (named.Key) {
                case "Name":
                    explicitName = named.Value.Value as string;
                    break;
                case "KeyType":
                    attributeKeyType = named.Value.Value as ITypeSymbol;
                    break;
                case "MailboxCapacity":
                    mailboxCapacity = named.Value.Value is int capacity ? capacity : 0;
                    break;
                case "MailboxFullMode":
                    mailboxFailFast = named.Value.Value is int mode && mode == 1;
                    break;
                case "IdleTimeoutSeconds":
                    idleTimeoutSeconds = named.Value.Value is double idle ? idle : 0;
                    break;
                case "CallTimeoutSeconds":
                    callTimeoutSeconds = named.Value.Value is double call ? call : 0;
                    break;
                case "Placement":
                    placement = named.Value.Value is int placementValue
                        ? placementValue switch {
                            1 => "SingleHome",
                            2 => "VirtualShards",
                            _ => "Local"
                        }
                        : "Local";
                    break;
            }

        var reentrant = false;
        foreach (var attribute in type.GetAttributes())
            if (attribute.AttributeClass?.ToDisplayString() == ReentrantAttributeDisplayName)
                reentrant = true;

        // Constructor: exactly one public constructor carries the activation dependencies.
        var constructors = type.InstanceConstructors
            .Where(static ctor => ctor.DeclaredAccessibility == Accessibility.Public)
            .ToList();
        if (constructors.Count != 1)
            return new ActorResult(null, new[] {
                DiagnosticInfo.Create(InvalidActorConstructor, location, typeDisplay)
            }.ToEquatableArray());

        ITypeSymbol? contextKeyType = null;
        var ambiguousKey = false;
        var ctorParameters = new List<CtorParameterInfo>();
        foreach (var parameter in constructors[0].Parameters)
            if (parameter.Type is INamedTypeSymbol named && contextGenericSymbol is not null &&
                SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, contextGenericSymbol)) {
                var parameterKeyType = named.TypeArguments[0];
                if (contextKeyType is not null &&
                    !SymbolEqualityComparer.Default.Equals(contextKeyType, parameterKeyType))
                    ambiguousKey = true;

                contextKeyType = parameterKeyType;
                ctorParameters.Add(new CtorParameterInfo(CtorParameterKind.Context, string.Empty));
            }
            else if (contextSymbol is not null &&
                     SymbolEqualityComparer.Default.Equals(parameter.Type, contextSymbol)) {
                ctorParameters.Add(new CtorParameterInfo(CtorParameterKind.Context, string.Empty));
            }
            else if (parameter.Type is INamedTypeSymbol namedState && stateGenericSymbol is not null &&
                     SymbolEqualityComparer.Default.Equals(namedState.OriginalDefinition, stateGenericSymbol)) {
                // Snapshot-backed state (ADR-0047): the activator creates it via ActorStateFactory,
                // bound to this activation's identity, instead of resolving it from DI.
                ctorParameters.Add(new CtorParameterInfo(
                    CtorParameterKind.State,
                    namedState.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
            }
            else {
                ctorParameters.Add(new CtorParameterInfo(
                    CtorParameterKind.Service,
                    parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
            }

        if (ambiguousKey ||
            (attributeKeyType is not null && contextKeyType is not null &&
             !SymbolEqualityComparer.Default.Equals(attributeKeyType, contextKeyType)))
            return new ActorResult(null, new[] {
                DiagnosticInfo.Create(AmbiguousActorKey, location, typeDisplay)
            }.ToEquatableArray());

        var keyType = attributeKeyType ?? contextKeyType;
        var actorName = explicitName ?? DeriveActorName(type.Name);

        if (placement == "VirtualShards" && keyType is null)
            diagnostics.Add(DiagnosticInfo.Create(VirtualShardedActorMustBeKeyed, location, typeDisplay));

        // Public instance methods become facade methods; lifecycle hooks stay off the facade. A method carrying
        // [ConsumeEvent] additionally gets a generated integration-event relay (ADR-0046).
        var methods = new List<ActorMethodInfo>();
        var consumers = new List<ActorConsumerInfo>();
        var workItemNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in type.GetMembers()) {
            cancellationToken.ThrowIfCancellationRequested();
            if (member is not IMethodSymbol method ||
                method.MethodKind != MethodKind.Ordinary ||
                method.IsStatic ||
                method.IsOverride ||
                method.IsImplicitlyDeclared)
                continue;

            if (method.Name is "OnActivateAsync" or "OnDeactivateAsync") continue;

            var consumeAttribute = GetAttribute(method, ConsumeEventAttributeDisplayName);

            // Non-public methods are off the facade. The relay reaches the actor through its public facade, so a
            // non-public [ConsumeEvent] method is an error; other non-public methods are simply not facade methods.
            if (method.DeclaredAccessibility != Accessibility.Public) {
                if (consumeAttribute is not null)
                    diagnostics.Add(DiagnosticInfo.Create(
                        ActorConsumeMethodNotPublic, LocationInfo.From(method), method.Name, typeDisplay));

                continue;
            }

            ReturnShape shape;
            var resultTypeFqn = string.Empty;
            if (SymbolEqualityComparer.Default.Equals(method.ReturnType, taskSymbol)) {
                shape = ReturnShape.TaskVoid;
            }
            else if (SymbolEqualityComparer.Default.Equals(method.ReturnType, valueTaskSymbol)) {
                shape = ReturnShape.ValueTaskVoid;
            }
            else if (method.ReturnType is INamedTypeSymbol { IsGenericType: true } generic &&
                     SymbolEqualityComparer.Default.Equals(generic.OriginalDefinition, taskGenericSymbol)) {
                shape = ReturnShape.TaskOfResult;
                resultTypeFqn = generic.TypeArguments[0].ToDisplayString(NullableAwareFullyQualifiedFormat);
            }
            else if (method.ReturnType is INamedTypeSymbol { IsGenericType: true } genericValueTask &&
                     SymbolEqualityComparer.Default.Equals(genericValueTask.OriginalDefinition,
                         valueTaskGenericSymbol)) {
                shape = ReturnShape.ValueTaskOfResult;
                resultTypeFqn = genericValueTask.TypeArguments[0]
                    .ToDisplayString(NullableAwareFullyQualifiedFormat);
            }
            else if (asyncEnumerableSymbol is not null &&
                     method.ReturnType is INamedTypeSymbol { IsGenericType: true } genericStream &&
                     SymbolEqualityComparer.Default.Equals(genericStream.OriginalDefinition, asyncEnumerableSymbol)) {
                shape = ReturnShape.AsyncEnumerable;
                resultTypeFqn = genericStream.TypeArguments[0]
                    .ToDisplayString(NullableAwareFullyQualifiedFormat);
            }
            else {
                diagnostics.Add(DiagnosticInfo.Create(
                    InvalidActorMethod, LocationInfo.From(method), method.Name, typeDisplay));
                continue;
            }

            var parameters = new List<MethodParameterInfo>();
            var invalid = method.IsGenericMethod;
            var cancellationTokenCount = 0;
            foreach (var parameter in method.Parameters) {
                if (parameter.RefKind != RefKind.None || parameter.Type.IsRefLikeType) {
                    invalid = true;
                    break;
                }

                var isCancellationToken = cancellationTokenSymbol is not null &&
                                          SymbolEqualityComparer.Default.Equals(parameter.Type,
                                              cancellationTokenSymbol);
                if (isCancellationToken) cancellationTokenCount++;

                parameters.Add(new MethodParameterInfo(
                    parameter.Name,
                    parameter.Type.ToDisplayString(NullableAwareFullyQualifiedFormat),
                    isCancellationToken));
            }

            if (invalid || cancellationTokenCount > 1) {
                diagnostics.Add(DiagnosticInfo.Create(
                    InvalidActorMethod, LocationInfo.From(method), method.Name, typeDisplay));
                continue;
            }

            // A stream method's turn only attaches the subscription; the turn token (a pooled CTS) must
            // never leak into the returned stream, and a relay cannot await an IAsyncEnumerable.
            if (shape == ReturnShape.AsyncEnumerable && (cancellationTokenCount > 0 || consumeAttribute is not null)) {
                diagnostics.Add(DiagnosticInfo.Create(
                    InvalidActorStreamMethod, LocationInfo.From(method), method.Name, typeDisplay));
                continue;
            }

            var workItemName = method.Name + "WorkItem";
            var suffix = 1;
            while (!workItemNames.Add(workItemName)) workItemName = method.Name + "WorkItem" + suffix++;

            methods.Add(new ActorMethodInfo(
                method.Name,
                shape,
                resultTypeFqn,
                parameters.ToEquatableArray(),
                workItemName));

            if (consumeAttribute is not null) {
                var consumer = TryCreateConsumer(
                    method, consumeAttribute, keyType, actorName,
                    integrationEventSymbol, cancellationTokenSymbol,
                    typeDisplay, diagnostics);
                if (consumer is not null) consumers.Add(consumer);
            }
        }

        var actorNamespace = type.ContainingNamespace is { IsGlobalNamespace: false } containing
            ? containing.ToDisplayString()
            : string.Empty;
        var actor = new ActorInfo(
            type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            actorNamespace,
            actorName,
            "I" + actorName,
            actorName + "ActorFacade",
            keyType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            reentrant,
            mailboxCapacity,
            mailboxFailFast,
            idleTimeoutSeconds,
            callTimeoutSeconds,
            placement,
            ctorParameters.ToEquatableArray(),
            methods.ToEquatableArray(),
            consumers.ToEquatableArray(),
            GetHintName(type));

        return new ActorResult(actor, diagnostics.ToEquatableArray());
    }

    // Builds the relay model for a [ConsumeEvent] actor method: the single integration-event parameter, the
    // resolved actor key, and the facade call arguments in declared order. Reports ELACT008/ELACT010 on failure.
    private static ActorConsumerInfo? TryCreateConsumer(
        IMethodSymbol method,
        AttributeData consumeAttribute,
        ITypeSymbol? keyType,
        string actorName,
        INamedTypeSymbol? integrationEventSymbol,
        INamedTypeSymbol? cancellationTokenSymbol,
        string typeDisplay,
        List<DiagnosticInfo> diagnostics) {
        var nonTokenParameters = method.Parameters
            .Where(parameter => !IsCancellationToken(parameter.Type, cancellationTokenSymbol))
            .ToList();

        // Exactly one integration-event parameter; a domain event / zero / multiple is not a valid actor consumer.
        var eventType = nonTokenParameters.Count == 1 ? nonTokenParameters[0].Type : null;
        if (eventType is null ||
            integrationEventSymbol is null ||
            !Implements(eventType, integrationEventSymbol)) {
            diagnostics.Add(DiagnosticInfo.Create(
                ActorConsumeNotIntegrationEvent, LocationInfo.From(method), method.Name, typeDisplay));
            return null;
        }

        string? keyExpression = null;
        if (keyType is not null) {
            keyExpression = ResolveKeyExpression(method, eventType, keyType, actorName, typeDisplay, diagnostics);
            if (keyExpression is null) return null; // ELACT008 already reported
        }

        var callArguments = method.Parameters
            .Select(parameter => IsCancellationToken(parameter.Type, cancellationTokenSymbol)
                ? "cancellationToken"
                : "request")
            .ToEquatableArray();

        return new ActorConsumerInfo(
            method.Name,
            eventType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            GetIntNamedArgument(consumeAttribute, "Order", 0),
            keyExpression,
            callArguments,
            $"{actorName}_{method.Name}_EventRelay");
    }

    // Resolves the "request.{Property}" key accessor: an explicit [ActorKey] wins, else the event's single
    // property whose type is the actor key type. Reports ELACT008 (and returns null) when neither resolves.
    private static string? ResolveKeyExpression(
        IMethodSymbol method,
        ITypeSymbol eventType,
        ITypeSymbol keyType,
        string actorName,
        string typeDisplay,
        List<DiagnosticInfo> diagnostics) {
        var eventDisplay = eventType.ToDisplayString();
        var candidates = GetKeyCandidateProperties(eventType, keyType);

        var actorKey = GetAttribute(method, ActorKeyAttributeDisplayName);
        if (actorKey is not null) {
            var propertyName = actorKey.ConstructorArguments.Length > 0
                ? actorKey.ConstructorArguments[0].Value as string
                : null;
            var match = propertyName is null
                ? null
                : candidates.FirstOrDefault(property =>
                    string.Equals(property.Name, propertyName, StringComparison.Ordinal));
            if (match is null) {
                diagnostics.Add(DiagnosticInfo.Create(
                    ActorEventKeyUnresolved, LocationInfo.From(method), method.Name, actorName, eventDisplay,
                    candidates.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                return null;
            }

            return $"request.{match.Name}";
        }

        if (candidates.Count == 1) return $"request.{candidates[0].Name}";

        diagnostics.Add(DiagnosticInfo.Create(
            ActorEventKeyUnresolved, LocationInfo.From(method), method.Name, actorName, eventDisplay,
            candidates.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return null;
    }

    // Public instance properties (of the event and its base types) whose type is exactly the actor key type.
    private static List<IPropertySymbol> GetKeyCandidateProperties(ITypeSymbol eventType, ITypeSymbol keyType) {
        var result = new List<IPropertySymbol>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var current = eventType;
             current is not null && current.SpecialType != SpecialType.System_Object;
             current = current.BaseType)
            foreach (var member in current.GetMembers())
                if (member is IPropertySymbol {
                        DeclaredAccessibility: Accessibility.Public,
                        IsStatic: false,
                        GetMethod: not null,
                        IsIndexer: false
                    } property
                    && seen.Add(property.Name)
                    && SymbolEqualityComparer.Default.Equals(property.Type, keyType))
                    result.Add(property);

        return result;
    }

    private static bool IsCancellationToken(ITypeSymbol type, INamedTypeSymbol? cancellationTokenSymbol) {
        return cancellationTokenSymbol is not null &&
               SymbolEqualityComparer.Default.Equals(type, cancellationTokenSymbol);
    }

    private static bool Implements(ITypeSymbol type, INamedTypeSymbol interfaceType) {
        return type.AllInterfaces.Any(implemented => SymbolEqualityComparer.Default.Equals(implemented, interfaceType));
    }

    private static AttributeData? GetAttribute(ISymbol symbol, string attributeDisplayName) {
        return symbol.GetAttributes()
            .FirstOrDefault(attribute => attribute.AttributeClass?.ToDisplayString() == attributeDisplayName);
    }

    private static int GetIntNamedArgument(AttributeData attribute, string name, int defaultValue) {
        foreach (var argument in attribute.NamedArguments)
            if (argument.Key == name && argument.Value.Value is int value)
                return value;

        return defaultValue;
    }

    private static string DeriveActorName(string typeName) {
        return typeName.Length > 5 && typeName.EndsWith("Actor", StringComparison.Ordinal)
            ? typeName.Substring(0, typeName.Length - 5)
            : typeName;
    }

    private static string GetHintName(INamedTypeSymbol type) {
        var display = type.ToDisplayString();
        var sb = new StringBuilder(display.Length);
        foreach (var ch in display) sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');

        return sb.ToString();
    }

    private static void EmitPerModule(
        SourceProductionContext spc,
        IReadOnlyList<ModuleScanner.Module> modules,
        IReadOnlyList<ActorInfo> actors) {
        if (modules.Count == 0) return;

        var byModule = new Dictionary<ModuleScanner.Module, List<ActorInfo>>();
        foreach (var actor in actors) {
            var module = ModuleScanner.FindBest(actor.ActorNamespace, modules);
            if (module is null) {
                spc.ReportDiagnostic(Diagnostic.Create(
                    ActorNotInModule,
                    Location.None,
                    actor.ActorTypeFqn.Replace("global::", string.Empty)));
                continue;
            }

            if (!byModule.TryGetValue(module, out var list)) {
                list = [];
                byModule[module] = list;
            }

            list.Add(actor);
        }

        foreach (var kvp in byModule.OrderBy(static x => x.Key.Name, StringComparer.Ordinal)) {
            var module = kvp.Key;
            var className = $"{module.Name}ActorExtensions";
            var methodName = $"Add{module.Name}Actors";
            var ns = module.Namespace.Length > 0 ? module.Namespace : null;

            var source = GenerateRegistration(ns, className, methodName, kvp.Value);
            spc.AddSource($"{module.Name}ActorExtensions.g.cs", SourceText.From(source, Encoding.UTF8));

            var nsPrefix = module.Namespace.Length > 0 ? $"global::{module.Namespace}." : "global::";
            ModuleDefaultsEmitter.EmitFiller(
                spc,
                module.Namespace,
                module.TypeName,
                ModuleDefaultsEmitter.AddActorsMethod,
                "Actors",
                $"{nsPrefix}{className}.{methodName}(services);");
        }
    }

    private static string GenerateFacade(ActorInfo actor) {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// Source: Elarion.Generators.ActorRegistrationGenerator");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        if (actor.ActorNamespace.Length > 0) {
            sb.AppendLine($"namespace {actor.ActorNamespace};");
            sb.AppendLine();
        }

        var facadeMarker = actor.KeyTypeFqn is null
            ? "global::Elarion.Actors.IActorFacade"
            : $"global::Elarion.Actors.IActorFacade<{actor.KeyTypeFqn}>";

        sb.AppendLine("/// <summary>");
        sb.AppendLine(
            $"/// Typed facade over the <see cref=\"{DocCref(actor.ActorTypeFqn)}\"/> actor: each call is enqueued");
        sb.AppendLine("/// into the actor's mailbox and executed under its single-threaded guarantee. Resolve via");
        sb.AppendLine("/// <see cref=\"global::Elarion.Actors.IActorSystem\"/>.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine($"public interface {actor.FacadeInterfaceName} : {facadeMarker}");
        sb.AppendLine("{");
        var first = true;
        foreach (var method in actor.Methods) {
            if (!first) sb.AppendLine();

            first = false;
            sb.AppendLine(
                $"    /// <summary>Invokes <c>{Plain(actor.ActorTypeFqn)}.{method.Name}</c> through the actor mailbox.</summary>");
            sb.AppendLine($"    {FacadeReturnType(method)} {method.Name}({FacadeParameterList(method)});");
        }

        sb.AppendLine("}");
        sb.AppendLine();

        sb.AppendLine($"internal sealed class {actor.FacadeImplName} : {actor.FacadeInterfaceName}");
        sb.AppendLine("{");
        sb.AppendLine($"    private readonly global::Elarion.Actors.ActorHandle<{actor.ActorTypeFqn}> _handle;");
        sb.AppendLine();
        sb.AppendLine(
            $"    public {actor.FacadeImplName}(global::Elarion.Actors.ActorHandle<{actor.ActorTypeFqn}> handle)");
        sb.AppendLine("    {");
        sb.AppendLine("        _handle = handle;");
        sb.AppendLine("    }");
        foreach (var method in actor.Methods) {
            sb.AppendLine();
            AppendFacadeMethod(sb, actor, method);
        }

        foreach (var method in actor.Methods) {
            sb.AppendLine();
            AppendWorkItem(sb, actor, method);
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static void AppendFacadeMethod(StringBuilder sb, ActorInfo actor, ActorMethodInfo method) {
        var tokenName = FacadeTokenName(method);
        var arguments = string.Join(", ", method.Parameters
            .Where(static p => !p.IsCancellationToken)
            .Select(static p => p.Name));

        if (method.Return is ReturnShape.AsyncEnumerable) {
            // The attach turn is deferred until enumeration (once per enumeration); the facade token
            // cancels the queued attach and, linked with the enumerator's token, the stream itself.
            sb.AppendLine($"    public {FacadeReturnType(method)} {method.Name}({FacadeParameterList(method)}) =>");
            sb.AppendLine($"        global::Elarion.Actors.Runtime.ActorStreams.Defer<{method.ResultTypeFqn}>(");
            sb.AppendLine(
                $"            elarionAttachToken => _handle.InvokeAsync({method.WorkItemClassName}.Rent({arguments}), elarionAttachToken),");
            sb.AppendLine($"            {tokenName});");
            return;
        }

        var invoke = $"_handle.InvokeAsync({method.WorkItemClassName}.Rent({arguments}), {tokenName})";
        // Pass-through instead of an async/await wrapper (ADR-0042 roadmap): ValueTask shapes
        // return the handle's ValueTask directly; Task shapes call AsTask(), which on the handle's
        // sync-enqueue fast path returns the underlying completion task allocation-free.
        var body = method.Return is ReturnShape.TaskVoid or ReturnShape.TaskOfResult
            ? $"{invoke}.AsTask()"
            : invoke;
        sb.AppendLine($"    public {FacadeReturnType(method)} {method.Name}({FacadeParameterList(method)}) =>");
        sb.AppendLine($"        {body};");
    }

    private static void AppendWorkItem(StringBuilder sb, ActorInfo actor, ActorMethodInfo method) {
        var resultFqn = method.Return switch {
            ReturnShape.TaskVoid or ReturnShape.ValueTaskVoid => UnitFqn,
            ReturnShape.AsyncEnumerable => $"{AsyncEnumerableFqn}<{method.ResultTypeFqn}>",
            _ => method.ResultTypeFqn
        };
        var dataParameters = method.Parameters.Where(static p => !p.IsCancellationToken).ToList();

        var poolFqn = $"global::Elarion.Actors.Runtime.ActorWorkItemPool<{method.WorkItemClassName}>";
        sb.AppendLine(
            $"    private sealed class {method.WorkItemClassName} : global::Elarion.Actors.ActorWorkItem<{actor.ActorTypeFqn}, {resultFqn}>");
        sb.AppendLine("    {");
        // Fields are mutable and the item is pooled: Rent reuses a recycled instance and overwrites
        // the arguments, so a completed call allocates no work item. The caller captures the
        // completion task before enqueue, so recycling never disturbs an in-flight await. The
        // default! initializer satisfies nullable analysis for the parameterless pooled ctor; Rent
        // always assigns before the item is used.
        foreach (var parameter in dataParameters)
            sb.AppendLine($"        private {parameter.TypeFqn} _{parameter.Name} = default!;");

        if (dataParameters.Count > 0) sb.AppendLine();

        var rentParameters = string.Join(", ", dataParameters.Select(static p => $"{p.TypeFqn} {p.Name}"));
        sb.AppendLine($"        public static {method.WorkItemClassName} Rent({rentParameters})");
        sb.AppendLine("        {");
        sb.AppendLine($"            var item = {poolFqn}.Rent(static () => new {method.WorkItemClassName}());");
        foreach (var parameter in dataParameters)
            sb.AppendLine($"            item._{parameter.Name} = {parameter.Name};");

        sb.AppendLine("            return item;");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        protected override void Recycle()");
        sb.AppendLine("        {");
        // Clear the arguments before pooling so a reference-typed argument is not retained by the
        // idle item (value-typed args reset harmlessly).
        foreach (var parameter in dataParameters) sb.AppendLine($"            _{parameter.Name} = default!;");

        sb.AppendLine($"            {poolFqn}.Return(this);");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine($"        public override string MethodName => \"{method.Name}\";");
        sb.AppendLine();
        var invokeArguments = string.Join(", ", method.Parameters
            .Select(static p => p.IsCancellationToken ? "cancellationToken" : $"_{p.Name}"));
        if (method.Return is ReturnShape.AsyncEnumerable) {
            // The attach turn is synchronous: the method subscribes and returns; the enumeration runs
            // off the mailbox. The turn token is deliberately not passed (ELACT012 forbids the
            // parameter). RetainActivation ties the activation's lifetime to the enumeration (refCount
            // lifetime, ADR-0052): idle passivation never ends a live stream mid-flight.
            sb.AppendLine(
                $"        protected override global::System.Threading.Tasks.ValueTask<{resultFqn}> InvokeAsync({actor.ActorTypeFqn} actor, {CancellationTokenFqn} cancellationToken) =>");
            sb.AppendLine(
                $"            new(global::Elarion.Actors.Runtime.ActorStreams.RetainWhileEnumerating(actor.{method.Name}({invokeArguments}), RetainActivation()));");
            sb.AppendLine("    }");
            return;
        }

        sb.AppendLine(
            $"        protected override async global::System.Threading.Tasks.ValueTask<{resultFqn}> InvokeAsync({actor.ActorTypeFqn} actor, {CancellationTokenFqn} cancellationToken)");
        sb.AppendLine("        {");
        if (method.Return is ReturnShape.TaskVoid or ReturnShape.ValueTaskVoid) {
            sb.AppendLine($"            await actor.{method.Name}({invokeArguments}).ConfigureAwait(false);");
            sb.AppendLine($"            return {UnitFqn}.Value;");
        }
        else {
            sb.AppendLine($"            return await actor.{method.Name}({invokeArguments}).ConfigureAwait(false);");
        }

        sb.AppendLine("        }");
        sb.AppendLine("    }");
    }

    private static string GenerateRegistration(
        string? ns,
        string className,
        string methodName,
        IReadOnlyList<ActorInfo> actors) {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// Source: Elarion.Generators.ActorRegistrationGenerator");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
        sb.AppendLine();
        if (ns is not null) {
            sb.AppendLine($"namespace {ns};");
            sb.AppendLine();
        }

        sb.AppendLine("/// <summary>Registers this module's [Actor] classes with the Elarion actor system.</summary>");
        sb.AppendLine($"public static class {className}");
        sb.AppendLine("{");
        sb.AppendLine($"    /// <summary>Adds the actor system and this module's actor registrations.</summary>");
        sb.AppendLine(
            $"    public static global::Microsoft.Extensions.DependencyInjection.IServiceCollection {methodName}(this global::Microsoft.Extensions.DependencyInjection.IServiceCollection services)");
        sb.AppendLine("    {");
        sb.AppendLine(
            "        global::Elarion.Actors.ActorServiceCollectionExtensions.AddElarionActorSystem(services);");
        foreach (var actor in actors) AppendActorRegistration(sb, actor);

        // Actor event relays (ADR-0046) register alongside their actor's module, so they share its feature gate:
        // a disabled module's relays disappear like its actors.
        foreach (var actor in actors)
        foreach (var consumer in actor.Consumers) {
            if (actor.ActorNamespace.Length == 0) continue;

            AppendConsumerRegistration(sb, actor, consumer);
        }

        sb.AppendLine("        return services;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    // The relay: a handler-form IHandler<TEvent, Result<Unit>> that resolves the actor facade by the extracted
    // key and calls the [ConsumeEvent] method through the public facade — the same call a hand-written relay
    // makes (ADR-0046). Its inbox is attached by the reused handler-registration emit (BuildRelayHandlerInfo).
    private static string GenerateRelayClass(ActorInfo actor, ActorConsumerInfo consumer) {
        var facadeFqn = $"global::{actor.ActorNamespace}.{actor.FacadeInterfaceName}";
        var callArguments = string.Join(", ", consumer.CallArguments);

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// Source: Elarion.Generators.ActorRegistrationGenerator");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine($"namespace {actor.ActorNamespace};");
        sb.AppendLine();
        sb.AppendLine(
            $"// Relays the {consumer.EventTypeFqn} integration event into the {actor.ActorName} actor (ADR-0046).");
        sb.AppendLine($"internal sealed class {consumer.RelayClassName}");
        sb.AppendLine($"    : global::Elarion.Abstractions.IHandler<{consumer.EventTypeFqn}, {RelayResponseFqn}>");
        sb.AppendLine("{");
        sb.AppendLine("    private readonly global::Elarion.Actors.IActorSystem _actors;");
        sb.AppendLine();
        sb.AppendLine(
            $"    public {consumer.RelayClassName}(global::Elarion.Actors.IActorSystem actors) => _actors = actors;");
        sb.AppendLine();
        sb.AppendLine($"    public async global::System.Threading.Tasks.ValueTask<{RelayResponseFqn}> HandleAsync(");
        sb.AppendLine($"        {consumer.EventTypeFqn} request,");
        sb.AppendLine("        global::System.Threading.CancellationToken cancellationToken)");
        sb.AppendLine("    {");
        sb.AppendLine(consumer.KeyExpression is null
            ? $"        var facade = _actors.Get<{facadeFqn}>();"
            : $"        var facade = _actors.GetByKey<{facadeFqn}, {actor.KeyTypeFqn}>({consumer.KeyExpression});");
        sb.AppendLine($"        await facade.{consumer.MethodName}({callArguments}).ConfigureAwait(false);");
        sb.AppendLine($"        return {RelayResponseFqn}.Success(global::Elarion.Abstractions.Results.Unit.Value);");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    // Synthesizes the HandlerInfo the handler-registration emit consumes, so the relay's decorator chain — the
    // Consumer-scoped inbox in particular — is the same code path as a hand-written consumer's (ADR-0046).
    private static HandlerRegistrationGenerator.HandlerInfo BuildRelayHandlerInfo(ActorInfo actor,
        ActorConsumerInfo consumer) {
        var relayFqn = $"global::{actor.ActorNamespace}.{consumer.RelayClassName}";
        var owner = HandlerRegistrationGenerator.TruncateOwner($"{actor.ActorNamespace}.{consumer.RelayClassName}");
        return new HandlerRegistrationGenerator.HandlerInfo(
            relayFqn,
            consumer.RelayClassName,
            consumer.EventTypeFqn,
            RelayResponseFqn,
            actor.ActorNamespace,
            EquatableArray<HandlerRegistrationGenerator.DecoratorInfo>.Empty,
            null,
            null,
            null,
            false,
            false,
            EquatableArray<HandlerRegistrationGenerator.ResourceBindingInfo>.Empty,
            false,
            false,
            HandlerRegistrationGenerator.CreateInboxInfo(owner, UnitFqn),
            null,
            EquatableArray<string>.Empty,
            // Keyed by the relay's own FQN so it coexists with any other consumer of the same event (ADR-0046).
            relayFqn,
            EquatableArray<DiagnosticInfo>.Empty);
    }

    // Wires a relay into the module's Add{Module}Actors: register its decorated pipeline, then the integration
    // subscription pointing at the IHandler<TEvent, Result<Unit>> interface (matches the event-consumer emit).
    private static void AppendConsumerRegistration(StringBuilder sb, ActorInfo actor, ActorConsumerInfo consumer) {
        var interfaceFqn = $"global::Elarion.Abstractions.IHandler<{consumer.EventTypeFqn}, {RelayResponseFqn}>";
        var registrationFqn = $"global::{actor.ActorNamespace}.{consumer.RelayClassName}Registration";
        var relayFqn = $"global::{actor.ActorNamespace}.{consumer.RelayClassName}";
        sb.AppendLine();
        sb.AppendLine($"        {registrationFqn}.Add{consumer.RelayClassName}(services);");
        sb.AppendLine(
            "        services.AddSingleton(new global::Elarion.Abstractions.Messaging.EventSubscriptionDescriptor");
        sb.AppendLine("        {");
        sb.AppendLine($"            ConsumerId = \"{relayFqn}\",");
        sb.AppendLine($"            EventType = typeof({consumer.EventTypeFqn}),");
        sb.AppendLine("            Plane = global::Elarion.Abstractions.Messaging.EventPlane.Integration,");
        sb.AppendLine($"            ServiceType = typeof({interfaceFqn}),");
        sb.AppendLine($"            Order = {consumer.Order},");
        if (actor.Placement == "SingleHome") {
            sb.AppendLine("            ResolveDeliveryRole = static (serviceProvider, _) =>");
            sb.AppendLine(
                "                serviceProvider.GetService<global::Elarion.Actors.IActorHomeLease>()?.Role,");
        }
        else if (actor.Placement == "VirtualShards") {
            var eventKey = consumer.KeyExpression!.Replace(
                "request.", $"(({consumer.EventTypeFqn})@event).");
            sb.AppendLine("            ResolveDeliveryRole = static (serviceProvider, @event) =>");
            sb.AppendLine("            {");
            sb.AppendLine(
                "                var resolver = serviceProvider.GetService<global::Elarion.Actors.IActorPlacementResolver>();");
            sb.AppendLine(
                $"                return resolver is null ? null : resolver.Resolve(\"{actor.ActorName}\", {eventKey}.ToString() ?? string.Empty).Role;");
            sb.AppendLine("            },");
        }

        sb.AppendLine("            InvokeAsync = static async (serviceProvider, @event, context, ct) =>");
        sb.AppendLine("            {");
        sb.AppendLine(
            $"                var handler = serviceProvider.GetRequiredKeyedService<{interfaceFqn}>(\"{relayFqn}\");");
        sb.AppendLine(
            $"                var result = await handler.HandleAsync(({consumer.EventTypeFqn})@event, ct).ConfigureAwait(false);");
        sb.AppendLine("                if (!result.IsSuccess)");
        sb.AppendLine("                {");
        sb.AppendLine(
            "                    throw new global::Elarion.Abstractions.Messaging.EventConsumerFailedException(result.Error);");
        sb.AppendLine("                }");
        sb.AppendLine("            }");
        sb.AppendLine("        });");
    }

    private static void AppendActorRegistration(StringBuilder sb, ActorInfo actor) {
        var keyFqn = actor.KeyTypeFqn ?? ActorSingletonKeyFqn;
        var facadeFqn = actor.ActorNamespace.Length > 0
            ? $"global::{actor.ActorNamespace}.{actor.FacadeInterfaceName}"
            : $"global::{actor.FacadeInterfaceName}";
        var facadeImplFqn = actor.ActorNamespace.Length > 0
            ? $"global::{actor.ActorNamespace}.{actor.FacadeImplName}"
            : $"global::{actor.FacadeImplName}";

        var activatorArguments = string.Join(", ", actor.CtorParameters.Select(parameter => parameter.Kind switch {
            CtorParameterKind.Context => "context",
            CtorParameterKind.State =>
                $"global::Elarion.Actors.ActorStateFactory.Create<{parameter.TypeFqn}, {keyFqn}>(serviceProvider, context)",
            _ => $"serviceProvider.GetRequiredService<{parameter.TypeFqn}>()"
        }));

        sb.AppendLine();
        sb.AppendLine(
            $"        global::Elarion.Actors.ActorServiceCollectionExtensions.AddElarionActor(services, new global::Elarion.Actors.ActorRegistration<{actor.ActorTypeFqn}, {keyFqn}, {facadeFqn}>");
        sb.AppendLine("        {");
        sb.AppendLine($"            Name = \"{actor.ActorName}\",");
        sb.AppendLine("            Options = new global::Elarion.Actors.ActorOptions");
        sb.AppendLine("            {");
        sb.AppendLine(
            $"                MailboxCapacity = {(actor.MailboxCapacity > 0 ? actor.MailboxCapacity.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null")},");
        sb.AppendLine(
            $"                MailboxFullMode = global::Elarion.Actors.ActorMailboxFullMode.{(actor.MailboxFailFast ? "Fail" : "Wait")},");
        sb.AppendLine(
            $"                IdleTimeout = {TimeoutExpression(actor.IdleTimeoutSeconds, "DefaultIdleTimeout")},");
        sb.AppendLine(
            $"                CallTimeout = {TimeoutExpression(actor.CallTimeoutSeconds, "DefaultCallTimeout")},");
        sb.AppendLine($"                Reentrant = {(actor.Reentrant ? "true" : "false")},");
        sb.AppendLine($"                Placement = global::Elarion.Actors.ActorPlacementMode.{actor.Placement}");
        sb.AppendLine("            },");
        sb.AppendLine(
            $"            Activator = static (serviceProvider, context) => new {actor.ActorTypeFqn}({activatorArguments}),");
        sb.AppendLine($"            Facade = static handle => new {facadeImplFqn}(handle)");
        sb.AppendLine("        });");
    }

    private static string TimeoutExpression(double seconds, string defaultProperty) {
        if (seconds < 0) return "null";

        if (seconds == 0) return $"global::Elarion.Actors.ActorOptions.{defaultProperty}";

        return
            $"global::System.TimeSpan.FromSeconds({seconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture)})";
    }

    private static string FacadeReturnType(ActorMethodInfo method) {
        return method.Return switch {
            ReturnShape.TaskVoid => TaskFqn,
            ReturnShape.TaskOfResult => $"{TaskFqn}<{method.ResultTypeFqn}>",
            ReturnShape.ValueTaskVoid => ValueTaskFqn,
            ReturnShape.AsyncEnumerable => $"{AsyncEnumerableFqn}<{method.ResultTypeFqn}>",
            _ => $"{ValueTaskFqn}<{method.ResultTypeFqn}>"
        };
    }

    private static string FacadeTokenName(ActorMethodInfo method) {
        foreach (var parameter in method.Parameters)
            if (parameter.IsCancellationToken)
                return parameter.Name;

        // The facade always exposes a trailing token (it controls queue wait + call timeout) even
        // when the actor method has none; dodge collisions with data parameter names.
        return method.Parameters.Any(static p => p.Name == "cancellationToken")
            ? "elarionCancellationToken"
            : "cancellationToken";
    }

    private static string FacadeParameterList(ActorMethodInfo method) {
        var parts = new List<string>();
        var hasToken = false;
        for (var i = 0; i < method.Parameters.Count; i++) {
            var parameter = method.Parameters[i];
            if (parameter.IsCancellationToken) {
                hasToken = true;
                // `= default` is only legal on a trailing parameter; a mid-list token stays required.
                var suffix = i == method.Parameters.Count - 1 ? " = default" : string.Empty;
                parts.Add($"{CancellationTokenFqn} {parameter.Name}{suffix}");
            }
            else {
                parts.Add($"{parameter.TypeFqn} {parameter.Name}");
            }
        }

        if (!hasToken) parts.Add($"{CancellationTokenFqn} {FacadeTokenName(method)} = default");

        return string.Join(", ", parts);
    }

    private static string Plain(string fqn) {
        return fqn.Replace("global::", string.Empty);
    }

    private static string DocCref(string fqn) {
        return Plain(fqn).Replace('<', '{').Replace('>', '}');
    }
}

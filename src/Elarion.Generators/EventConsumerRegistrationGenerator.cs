using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Elarion.Generators;

/// <summary>
/// Generates reflection-free <c>EventSubscriptionDescriptor</c> registrations for
/// <c>[ConsumeEvent]</c> methods declared on <c>[Service]</c> classes.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class EventConsumerRegistrationGenerator : IIncrementalGenerator {
    private const string TriggerAttributeMetadataName =
        "Elarion.Abstractions.GenerateEventConsumersAttribute";

    private const string ConsumeEventAttributeMetadataName =
        "Elarion.Abstractions.Messaging.ConsumeEventAttribute";

    private const string ServiceAttributeMetadataName =
        "Elarion.Abstractions.ServiceAttribute";

    private const string DomainEventMetadataName =
        "Elarion.Abstractions.Messaging.IDomainEvent";

    private const string IntegrationEventMetadataName =
        "Elarion.Abstractions.Messaging.IIntegrationEvent";

    private const string EventContextMetadataName =
        "Elarion.Abstractions.Messaging.IEventContext";

    private const string EventContextGenericMetadataName =
        "Elarion.Abstractions.Messaging.IEventContext`1";

    private const string ResultMetadataName =
        "Elarion.Abstractions.Result`1";

    private const string ResultNonGenericMetadataName =
        "Elarion.Abstractions.Result";

    private const string HandlerMetadataName =
        "Elarion.Abstractions.IHandler`2";

    private const string UnitMetadataName =
        "Elarion.Abstractions.Results.Unit";

    private const string CancellationTokenMetadataName =
        "System.Threading.CancellationToken";

    private const string TaskMetadataName =
        "System.Threading.Tasks.Task";

    private const string TaskGenericMetadataName =
        "System.Threading.Tasks.Task`1";

    private const string ValueTaskMetadataName =
        "System.Threading.Tasks.ValueTask";

    private const string ValueTaskGenericMetadataName =
        "System.Threading.Tasks.ValueTask`1";

    private static readonly DiagnosticDescriptor ConsumerNotOnService = new(
        id: "ELEVT001",
        title: "Event consumer must be declared on a [Service] class",
        messageFormat: "Event consumer method '{0}' must be declared on a class annotated with [Service]",
        category: "Elarion.Generators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidConsumerSignature = new(
        id: "ELEVT002",
        title: "Invalid event consumer signature",
        messageFormat:
        "Event consumer method '{0}' must be an accessible, non-generic, non-static instance method that accepts exactly one IDomainEvent or IIntegrationEvent parameter (optionally with IEventContext and/or CancellationToken) and returns void/Task/ValueTask or Result/Task<Result>/ValueTask<Result> — event consumers are fan-out subscribers, so a Result<T> with a value is request/reply, for which you use IHandlerSender/IHandler instead of the event bus",
        category: "Elarion.Generators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidHandlerConsumer = new(
        id: "ELEVT005",
        title: "Invalid handler-form event consumer",
        messageFormat:
        "Class '{0}' is annotated with [ConsumeEvent] but must implement exactly one "
        + "IHandler<TEvent> (or IHandler<TEvent, Result<Unit>>) whose request type implements "
        + "IDomainEvent or IIntegrationEvent — event consumers are fan-out subscribers, so the response must be "
        + "Result<Unit>; for a typed reply use IHandlerSender/IHandler instead of the event bus",
        category: "Elarion.Generators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor ConsumerNotInModule = new(
        id: "ELEVT003",
        title: "Event consumer is not in any module",
        messageFormat:
        "Event consumer '{0}' is annotated with [ConsumeEvent] but its namespace is not under any [AppModule]; "
        + "it will not be registered. Move the consumer under a module's namespace so it is wired by that module.",
        category: "Elarion.Generators",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private enum ParameterKind {
        Message,
        Context,
        ContextGeneric,
        CancellationToken
    }

    private enum ReturnShape {
        SubscriberVoid,
        SubscriberTask,
        SubscriberValueTask,
        SubscriberResult,
        SubscriberResultTask,
        SubscriberResultValueTask
    }

    private sealed record EventConsumerInfo(
        string ServiceTypeFqn,
        string ServiceNamespace,
        string MethodName,
        string EventTypeFqn,
        string Plane,
        int Order,
        ReturnShape Return,
        EquatableArray<ParameterKind> Parameters,
        string HintName,
        bool IsHandler,
        // The DI service key a handler-form consumer is registered under (its FQN, matching the handler
        // generator's keyed registration) so multiple consumers of one event resolve distinctly; null for
        // method-form consumers, which resolve their concrete [Service] type and never collide.
        string? ConsumerKey);

    /// <summary>A discovered consumer: either a registration model or the diagnostics that rejected it.</summary>
    private sealed record ConsumerResult(EventConsumerInfo? Consumer, EquatableArray<DiagnosticInfo> Diagnostics);

    private static class TrackingNames {
        public const string Consumers = "EventConsumers";
        public const string Combined = "EventConsumersCombined";
    }

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context) {
        // [ConsumeEvent] applies to methods (on [Service] classes) and to types (the IHandler form); a single
        // attribute-index pass discovers both, and the transform branches on the target symbol kind.
        var results = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                ConsumeEventAttributeMetadataName,
                static (node, _) => node is MethodDeclarationSyntax or ClassDeclarationSyntax,
                static (ctx, ct) => CreateConsumerResult(ctx, ct))
            .Where(static result => result is not null)
            .Select(static (result, _) => result!)
            .Collect()
            .WithTrackingName(TrackingNames.Consumers);

        var modules = ModuleProviders.CollectModules(context);
        var trigger = ModuleProviders.HasTrigger(context, TriggerAttributeMetadataName);

        var combined = results.Combine(modules).Combine(trigger).WithTrackingName(TrackingNames.Combined);

        context.RegisterSourceOutput(combined, static (spc, source) => {
            var ((results, modules), hasTrigger) = source;
            if (!hasTrigger) {
                return;
            }

            foreach (var result in results) {
                foreach (var diagnostic in result.Diagnostics) {
                    spc.ReportDiagnostic(diagnostic.ToDiagnostic());
                }
            }

            var consumers = new List<EventConsumerInfo>();
            foreach (var result in results) {
                if (result.Consumer is not null) {
                    consumers.Add(result.Consumer);
                }
            }

            consumers.Sort(static (left, right) =>
                string.Compare(left.HintName, right.HintName, StringComparison.Ordinal));

            if (consumers.Count == 0) {
                return;
            }

            // Consumers are registered per module via ConfigureDefaultServices; there is no assembly-wide method.
            EmitPerModule(spc, modules, consumers);
        });
    }

    private static ConsumerResult? CreateConsumerResult(GeneratorAttributeSyntaxContext ctx, CancellationToken ct) {
        if (ctx.Attributes.Length == 0) {
            return null;
        }

        var symbols = ResolveSymbols(ctx.SemanticModel.Compilation);
        if (symbols is null) {
            return null;
        }

        var consumeAttribute = ctx.Attributes[0];
        var diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();

        // [ConsumeEvent] on an [Actor] method is owned by the actor generator (ADR-0046: it emits the
        // integration-event relay). Yield here so this generator neither double-registers it nor reports a
        // misleading "not a [Service]".
        if (IsOnActorClass(ctx.TargetSymbol)) {
            return null;
        }

        if (ctx.TargetSymbol is IMethodSymbol method) {
            var location = (ctx.TargetNode as MethodDeclarationSyntax)?.Identifier.GetLocation();
            var containingType = method.ContainingType;
            if (containingType is null || !HasAttribute(containingType, symbols.ServiceAttribute, out _)) {
                diagnostics.Add(DiagnosticInfo.Create(ConsumerNotOnService, location, method.Name));
                return new ConsumerResult(null, diagnostics.ToImmutable());
            }

            var consumer = TryCreateConsumer(method, containingType, consumeAttribute, symbols, location, diagnostics);
            return new ConsumerResult(consumer, diagnostics.ToImmutable());
        }

        if (ctx.TargetSymbol is INamedTypeSymbol type) {
            var location = (ctx.TargetNode as ClassDeclarationSyntax)?.Identifier.GetLocation();
            var consumer = TryCreateHandlerConsumer(type, consumeAttribute, symbols, location, diagnostics);
            return new ConsumerResult(consumer, diagnostics.ToImmutable());
        }

        return null;
    }

    private static bool IsOnActorClass(ISymbol targetSymbol) {
        var type = targetSymbol as INamedTypeSymbol ?? targetSymbol.ContainingType;
        if (type is null) {
            return false;
        }

        foreach (var attribute in type.GetAttributes()) {
            if (attribute.AttributeClass?.ToDisplayString() == "Elarion.Actors.ActorAttribute") {
                return true;
            }
        }

        return false;
    }

    private static void EmitPerModule(
        SourceProductionContext spc,
        IReadOnlyList<ModuleScanner.Module> modules,
        IReadOnlyList<EventConsumerInfo> consumers)
    {
        if (modules.Count == 0)
        {
            return;
        }

        var byModule = new Dictionary<ModuleScanner.Module, List<EventConsumerInfo>>();
        foreach (var consumer in consumers)
        {
            var module = ModuleScanner.FindBest(consumer.ServiceNamespace, modules);
            if (module is null)
            {
                // Modules exist (guarded above) but this consumer matches none: the per-module path
                // is the only one a module-bootstrapper host calls, so warn that it would be dropped.
                spc.ReportDiagnostic(Diagnostic.Create(
                    ConsumerNotInModule,
                    Location.None,
                    $"{consumer.ServiceTypeFqn.Replace("global::", string.Empty)}.{consumer.MethodName}"));
                continue;
            }

            if (!byModule.TryGetValue(module, out var list))
            {
                list = [];
                byModule[module] = list;
            }

            list.Add(consumer);
        }

        foreach (var kvp in byModule.OrderBy(x => x.Key.Name, StringComparer.Ordinal))
        {
            var module = kvp.Key;
            var className = $"{module.Name}EventConsumerExtensions";
            var methodName = $"Add{module.Name}EventConsumers";
            var ns = module.Namespace.Length > 0 ? module.Namespace : null;

            var source = GenerateRegistration(ns, className, methodName, kvp.Value);
            spc.AddSource($"{module.Name}EventConsumerExtensions.g.cs", SourceText.From(source, Encoding.UTF8));

            var nsPrefix = module.Namespace.Length > 0 ? $"global::{module.Namespace}." : "global::";
            ModuleDefaultsEmitter.EmitFiller(
                spc,
                module.Namespace,
                module.TypeName,
                ModuleDefaultsEmitter.AddEventConsumersMethod,
                "EventConsumers",
                $"{nsPrefix}{className}.{methodName}(services);");
        }
    }

    private sealed record KnownSymbols(
        INamedTypeSymbol ConsumeEventAttribute,
        INamedTypeSymbol ServiceAttribute,
        INamedTypeSymbol DomainEvent,
        INamedTypeSymbol IntegrationEvent,
        INamedTypeSymbol EventContext,
        INamedTypeSymbol EventContextGeneric,
        INamedTypeSymbol Result,
        INamedTypeSymbol ResultNonGeneric,
        INamedTypeSymbol Handler,
        INamedTypeSymbol Unit,
        INamedTypeSymbol CancellationToken,
        INamedTypeSymbol Task,
        INamedTypeSymbol TaskGeneric,
        INamedTypeSymbol ValueTask,
        INamedTypeSymbol ValueTaskGeneric);

    private static KnownSymbols? ResolveSymbols(Compilation compilation) {
        var consumeEvent = compilation.GetTypeByMetadataName(ConsumeEventAttributeMetadataName);
        var service = compilation.GetTypeByMetadataName(ServiceAttributeMetadataName);
        var domainEvent = compilation.GetTypeByMetadataName(DomainEventMetadataName);
        var integrationEvent = compilation.GetTypeByMetadataName(IntegrationEventMetadataName);
        var eventContext = compilation.GetTypeByMetadataName(EventContextMetadataName);
        var eventContextGeneric = compilation.GetTypeByMetadataName(EventContextGenericMetadataName);
        var result = compilation.GetTypeByMetadataName(ResultMetadataName);
        var resultNonGeneric = compilation.GetTypeByMetadataName(ResultNonGenericMetadataName);
        var handler = compilation.GetTypeByMetadataName(HandlerMetadataName);
        var unit = compilation.GetTypeByMetadataName(UnitMetadataName);
        var cancellationToken = compilation.GetTypeByMetadataName(CancellationTokenMetadataName);
        var task = compilation.GetTypeByMetadataName(TaskMetadataName);
        var taskGeneric = compilation.GetTypeByMetadataName(TaskGenericMetadataName);
        var valueTask = compilation.GetTypeByMetadataName(ValueTaskMetadataName);
        var valueTaskGeneric = compilation.GetTypeByMetadataName(ValueTaskGenericMetadataName);

        if (consumeEvent is null ||
            service is null ||
            domainEvent is null ||
            integrationEvent is null ||
            eventContext is null ||
            eventContextGeneric is null ||
            result is null ||
            resultNonGeneric is null ||
            handler is null ||
            unit is null ||
            cancellationToken is null ||
            task is null ||
            taskGeneric is null ||
            valueTask is null ||
            valueTaskGeneric is null) {
            return null;
        }

        return new KnownSymbols(
            consumeEvent,
            service,
            domainEvent,
            integrationEvent,
            eventContext,
            eventContextGeneric,
            result,
            resultNonGeneric,
            handler,
            unit,
            cancellationToken,
            task,
            taskGeneric,
            valueTask,
            valueTaskGeneric);
    }

    private static EventConsumerInfo? TryCreateConsumer(
        IMethodSymbol method,
        INamedTypeSymbol containingType,
        AttributeData consumeAttribute,
        KnownSymbols symbols,
        Location? location,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics) {
        if (method.IsStatic ||
            method.IsGenericMethod ||
            !IsAccessible(method.DeclaredAccessibility) ||
            IsGenericOrNestedInGenericType(containingType)) {
            diagnostics.Add(DiagnosticInfo.Create(InvalidConsumerSignature, location, method.Name));
            return null;
        }

        if (!TryResolveParameters(method, symbols, out var parameters, out var eventType, out var plane)) {
            diagnostics.Add(DiagnosticInfo.Create(InvalidConsumerSignature, location, method.Name));
            return null;
        }

        if (!TryResolveReturn(method.ReturnType, symbols, out var returnShape)) {
            diagnostics.Add(DiagnosticInfo.Create(InvalidConsumerSignature, location, method.Name));
            return null;
        }

        var order = GetIntNamedArgument(consumeAttribute, "Order", 0);
        var eventFqn = eventType!.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var serviceFqn = containingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var serviceNamespace = containingType.ContainingNamespace is { IsGlobalNamespace: false } containing
            ? containing.ToDisplayString()
            : string.Empty;
        var hintName = $"{GetHintName(containingType)}__{method.Name}__{GetHintName(eventType!)}";

        return new EventConsumerInfo(
            serviceFqn,
            serviceNamespace,
            method.Name,
            eventFqn,
            plane,
            order,
            returnShape,
            parameters,
            hintName,
            IsHandler: false,
            ConsumerKey: null);
    }

    private static EventConsumerInfo? TryCreateHandlerConsumer(
        INamedTypeSymbol type,
        AttributeData consumeAttribute,
        KnownSymbols symbols,
        Location? location,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics) {
        if (type.IsAbstract || IsGenericOrNestedInGenericType(type)) {
            diagnostics.Add(DiagnosticInfo.Create(InvalidHandlerConsumer, location, type.Name));
            return null;
        }

        // Find the single IHandler<TEvent, Result<T>> whose request type is an event. Keying off
        // the two-arg interface covers both the explicit form and the IHandler<TEvent> sugar (which
        // inherits IHandler<TEvent, Result<Unit>>).
        INamedTypeSymbol? match = null;
        ITypeSymbol? eventType = null;
        ITypeSymbol? responseValueType = null;
        string plane = string.Empty;
        var ambiguous = false;

        foreach (var iface in type.AllInterfaces) {
            if (!SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, symbols.Handler)) {
                continue;
            }

            var request = iface.TypeArguments[0];
            var isDomain = Implements(request, symbols.DomainEvent);
            var isIntegration = Implements(request, symbols.IntegrationEvent);
            if (isDomain == isIntegration) {
                // Not an event request, or a type that is both planes: skip (ambiguous if both).
                continue;
            }

            var response = iface.TypeArguments[1];
            if (response is not INamedTypeSymbol named ||
                !SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, symbols.Result)) {
                continue;
            }

            if (match is not null) {
                ambiguous = true;
                break;
            }

            match = iface;
            eventType = request;
            responseValueType = named.TypeArguments[0];
            plane = isDomain ? "Domain" : "Integration";
        }

        if (match is null || ambiguous) {
            diagnostics.Add(DiagnosticInfo.Create(InvalidHandlerConsumer, location, type.Name));
            return null;
        }

        // Event consumers are fan-out subscribers (the event bus is pub/sub-only, ADR-0010), so the response
        // must be Result<Unit> (the "no content" analog of void). A non-Unit Result<T> is request/reply —
        // reject it and point the author at IHandlerSender/IHandler.
        if (!SymbolEqualityComparer.Default.Equals(responseValueType, symbols.Unit)) {
            diagnostics.Add(DiagnosticInfo.Create(InvalidHandlerConsumer, location, type.Name));
            return null;
        }

        var order = GetIntNamedArgument(consumeAttribute, "Order", 0);
        var handlerFqn = match.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var eventFqn = eventType!.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var typeNamespace = type.ContainingNamespace is { IsGlobalNamespace: false } containing
            ? containing.ToDisplayString()
            : string.Empty;
        var hintName = $"{GetHintName(type)}__HandleAsync__{GetHintName(eventType!)}";

        return new EventConsumerInfo(
            // The descriptor resolves the IHandler<,> interface so DI yields the decorated chain, keyed by the
            // consumer's own FQN (ConsumerKey below) so multiple consumers of one event resolve distinctly.
            handlerFqn,
            typeNamespace,
            "HandleAsync",
            eventFqn,
            plane,
            order,
            ReturnShape.SubscriberValueTask,
            ImmutableArray<ParameterKind>.Empty,
            hintName,
            IsHandler: true,
            ConsumerKey: type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
    }

    private static bool TryResolveParameters(
        IMethodSymbol method,
        KnownSymbols symbols,
        out ImmutableArray<ParameterKind> parameters,
        out ITypeSymbol? eventType,
        out string plane) {
        parameters = ImmutableArray<ParameterKind>.Empty;
        eventType = null;
        plane = string.Empty;

        var builder = ImmutableArray.CreateBuilder<ParameterKind>(method.Parameters.Length);
        foreach (var parameter in method.Parameters) {
            var type = parameter.Type;

            var isDomain = Implements(type, symbols.DomainEvent);
            var isIntegration = Implements(type, symbols.IntegrationEvent);
            if (isDomain || isIntegration) {
                if (eventType is not null || (isDomain && isIntegration)) {
                    // More than one message parameter, or a type that is both planes: ambiguous.
                    return false;
                }

                eventType = type;
                plane = isDomain ? "Domain" : "Integration";
                builder.Add(ParameterKind.Message);
                continue;
            }

            if (SymbolEqualityComparer.Default.Equals(type, symbols.CancellationToken)) {
                builder.Add(ParameterKind.CancellationToken);
                continue;
            }

            if (SymbolEqualityComparer.Default.Equals(type, symbols.EventContext)) {
                builder.Add(ParameterKind.Context);
                continue;
            }

            if (type is INamedTypeSymbol named &&
                named.IsGenericType &&
                SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, symbols.EventContextGeneric)) {
                builder.Add(ParameterKind.ContextGeneric);
                continue;
            }

            return false;
        }

        if (eventType is null) {
            return false;
        }

        // A typed context parameter must match the message type so the generated cast is valid.
        foreach (var parameter in method.Parameters) {
            if (parameter.Type is INamedTypeSymbol named &&
                named.IsGenericType &&
                SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, symbols.EventContextGeneric) &&
                !SymbolEqualityComparer.Default.Equals(named.TypeArguments[0], eventType)) {
                return false;
            }
        }

        parameters = builder.ToImmutable();
        return true;
    }

    // Event consumers are fan-out subscribers. A "no value" success/failure is the non-generic Result (the
    // IHandler<TEvent> shape) — a failed Result surfaces as an EventConsumerFailedException. void/Task/ValueTask
    // are the fire-and-throw shorthand. A Result<T> with a value is request/reply and is rejected (ELEVT002).
    private static bool TryResolveReturn(
        ITypeSymbol returnType,
        KnownSymbols symbols,
        out ReturnShape shape) {
        shape = ReturnShape.SubscriberVoid;

        if (returnType.SpecialType == SpecialType.System_Void) {
            shape = ReturnShape.SubscriberVoid;
            return true;
        }

        if (SymbolEqualityComparer.Default.Equals(returnType, symbols.Task)) {
            shape = ReturnShape.SubscriberTask;
            return true;
        }

        if (SymbolEqualityComparer.Default.Equals(returnType, symbols.ValueTask)) {
            shape = ReturnShape.SubscriberValueTask;
            return true;
        }

        if (SymbolEqualityComparer.Default.Equals(returnType, symbols.ResultNonGeneric)) {
            shape = ReturnShape.SubscriberResult;
            return true;
        }

        if (returnType is INamedTypeSymbol { IsGenericType: true } named) {
            var definition = named.OriginalDefinition;
            var isTask = SymbolEqualityComparer.Default.Equals(definition, symbols.TaskGeneric);
            var isValueTask = SymbolEqualityComparer.Default.Equals(definition, symbols.ValueTaskGeneric);
            if ((isTask || isValueTask) &&
                SymbolEqualityComparer.Default.Equals(named.TypeArguments[0], symbols.ResultNonGeneric)) {
                shape = isTask ? ReturnShape.SubscriberResultTask : ReturnShape.SubscriberResultValueTask;
                return true;
            }
        }

        return false;
    }

    private static string GenerateRegistration(
        string? ns,
        string className,
        string methodName,
        IReadOnlyList<EventConsumerInfo> consumers) {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// Source: Elarion.Generators.EventConsumerRegistrationGenerator");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
        sb.AppendLine("using Microsoft.Extensions.DependencyInjection.Extensions;");
        sb.AppendLine();
        if (ns is not null) {
            sb.AppendLine($"namespace {ns};");
            sb.AppendLine();
        }

        sb.AppendLine($"public static class {className}");
        sb.AppendLine("{");
        sb.AppendLine($"    public static global::Microsoft.Extensions.DependencyInjection.IServiceCollection {methodName}(this global::Microsoft.Extensions.DependencyInjection.IServiceCollection services)");
        sb.AppendLine("    {");

        // Handler-form consumers resolve the IHandler<,> interface, which the handler generator
        // already registers (with its decorator chain); only service-method consumers self-register.
        foreach (var serviceFqn in consumers
                     .Where(consumer => !consumer.IsHandler)
                     .Select(consumer => consumer.ServiceTypeFqn)
                     .Distinct(StringComparer.Ordinal)) {
            sb.AppendLine($"        services.TryAddScoped<{serviceFqn}>();");
        }

        foreach (var consumer in consumers) {
            AppendDescriptorRegistration(sb, consumer);
        }

        sb.AppendLine("        return services;");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static void AppendDescriptorRegistration(StringBuilder sb, EventConsumerInfo consumer) {
        sb.AppendLine();
        sb.AppendLine("        services.AddSingleton(new global::Elarion.Abstractions.Messaging.EventSubscriptionDescriptor");
        sb.AppendLine("        {");
        var owner = consumer.IsHandler ? consumer.ConsumerKey! : consumer.ServiceTypeFqn;
        var parameterShape = consumer.IsHandler
            ? string.Empty
            : ":" + string.Join(",", consumer.Parameters.Select(static parameter => parameter.ToString()));
        var consumerId = $"{owner.Replace("global::", string.Empty)}.{consumer.MethodName}({consumer.EventTypeFqn.Replace("global::", string.Empty)}{parameterShape})";
        sb.AppendLine($"            ConsumerId = {FormatStringLiteral(consumerId)},");
        sb.AppendLine($"            EventType = typeof({consumer.EventTypeFqn}),");
        sb.AppendLine($"            Plane = global::Elarion.Abstractions.Messaging.EventPlane.{consumer.Plane},");
        sb.AppendLine($"            ServiceType = typeof({consumer.ServiceTypeFqn}),");

        sb.AppendLine($"            Order = {consumer.Order},");
        if (consumer.IsHandler) {
            AppendHandlerSubscriberInvoke(sb, consumer);
        }
        else {
            AppendSubscriberInvoke(sb, consumer);
        }

        sb.AppendLine("        });");
    }

    private static void AppendSubscriberInvoke(StringBuilder sb, EventConsumerInfo consumer) {
        var arguments = BuildArguments(consumer, "@event");
        var invocation = $"service.{consumer.MethodName}({arguments})";

        switch (consumer.Return) {
            case ReturnShape.SubscriberVoid:
            case ReturnShape.SubscriberTask:
            case ReturnShape.SubscriberValueTask:
                sb.AppendLine("            InvokeAsync = static (serviceProvider, @event, context, ct) =>");
                sb.AppendLine("            {");
                sb.AppendLine($"                var service = serviceProvider.GetRequiredService<{consumer.ServiceTypeFqn}>();");
                switch (consumer.Return) {
                    case ReturnShape.SubscriberVoid:
                        sb.AppendLine($"                {invocation};");
                        sb.AppendLine("                return global::System.Threading.Tasks.ValueTask.CompletedTask;");
                        break;
                    case ReturnShape.SubscriberTask:
                        sb.AppendLine($"                return new global::System.Threading.Tasks.ValueTask({invocation});");
                        break;
                    default:
                        sb.AppendLine($"                return {invocation};");
                        break;
                }

                sb.AppendLine("            }");
                break;
            case ReturnShape.SubscriberResult:
                // A synchronous non-generic Result: invoke, surface a failure as EventConsumerFailedException, complete.
                sb.AppendLine("            InvokeAsync = static (serviceProvider, @event, context, ct) =>");
                sb.AppendLine("            {");
                sb.AppendLine($"                var service = serviceProvider.GetRequiredService<{consumer.ServiceTypeFqn}>();");
                sb.AppendLine($"                var result = {invocation};");
                sb.AppendLine("                if (!result.IsSuccess)");
                sb.AppendLine("                {");
                sb.AppendLine("                    throw new global::Elarion.Abstractions.Messaging.EventConsumerFailedException(result.Error);");
                sb.AppendLine("                }");
                sb.AppendLine("                return global::System.Threading.Tasks.ValueTask.CompletedTask;");
                sb.AppendLine("            }");
                break;
            default:
                // Task<Result> / ValueTask<Result>: await, surface a failure as EventConsumerFailedException.
                sb.AppendLine("            InvokeAsync = static async (serviceProvider, @event, context, ct) =>");
                sb.AppendLine("            {");
                sb.AppendLine($"                var service = serviceProvider.GetRequiredService<{consumer.ServiceTypeFqn}>();");
                sb.AppendLine($"                var result = await {invocation}.ConfigureAwait(false);");
                sb.AppendLine("                if (!result.IsSuccess)");
                sb.AppendLine("                {");
                sb.AppendLine("                    throw new global::Elarion.Abstractions.Messaging.EventConsumerFailedException(result.Error);");
                sb.AppendLine("                }");
                sb.AppendLine("            }");
                break;
        }
    }

    private static void AppendHandlerSubscriberInvoke(StringBuilder sb, EventConsumerInfo consumer) {
        // ServiceTypeFqn is the IHandler<TEvent, Result<T>> interface, resolved keyed by the consumer's FQN so
        // DI yields THIS consumer's decorated chain even when several consumers share the event. A handler
        // subscriber has no return channel, so a failed Result is surfaced as an EventConsumerFailedException.
        sb.AppendLine("            InvokeAsync = static async (serviceProvider, @event, context, ct) =>");
        sb.AppendLine("            {");
        sb.AppendLine($"                var handler = serviceProvider.GetRequiredKeyedService<{consumer.ServiceTypeFqn}>({FormatStringLiteral(consumer.ConsumerKey!)});");
        sb.AppendLine($"                var result = await handler.HandleAsync(({consumer.EventTypeFqn})@event, ct).ConfigureAwait(false);");
        sb.AppendLine("                if (!result.IsSuccess)");
        sb.AppendLine("                {");
        sb.AppendLine("                    throw new global::Elarion.Abstractions.Messaging.EventConsumerFailedException(result.Error);");
        sb.AppendLine("                }");
        sb.AppendLine("            }");
    }

    private static string BuildArguments(EventConsumerInfo consumer, string messageVariable) =>
        string.Join(", ", consumer.Parameters.Select(parameter => parameter switch {
            ParameterKind.Message => $"({consumer.EventTypeFqn}){messageVariable}",
            ParameterKind.Context => "context",
            ParameterKind.ContextGeneric =>
                $"(global::Elarion.Abstractions.Messaging.IEventContext<{consumer.EventTypeFqn}>)context",
            _ => "ct"
        }));

    private static bool HasAttribute(ISymbol symbol, INamedTypeSymbol attributeType, out AttributeData? attribute) {
        attribute = symbol.GetAttributes()
            .FirstOrDefault(candidate => SymbolEqualityComparer.Default.Equals(candidate.AttributeClass, attributeType));
        return attribute is not null;
    }

    private static bool Implements(ITypeSymbol type, INamedTypeSymbol interfaceType) =>
        type.AllInterfaces.Any(implemented =>
            SymbolEqualityComparer.Default.Equals(implemented, interfaceType));

    private static int GetIntNamedArgument(AttributeData attribute, string name, int defaultValue) {
        foreach (var argument in attribute.NamedArguments) {
            if (argument.Key == name && argument.Value.Value is int value) {
                return value;
            }
        }

        return defaultValue;
    }

    private static string FormatStringLiteral(string value) =>
        Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(value, quote: true);

    private static bool IsAccessible(Accessibility accessibility) =>
        accessibility is Accessibility.Public or Accessibility.Internal;

    private static bool IsGenericOrNestedInGenericType(INamedTypeSymbol type) {
        for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType) {
            if (current.TypeParameters.Length > 0) {
                return true;
            }
        }

        return false;
    }

    private static string GetHintName(ITypeSymbol type) =>
        type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            .Replace("global::", string.Empty)
            .Replace(".", "_")
            .Replace("<", "_")
            .Replace(">", "_")
            .Replace(",", "_")
            .Replace(" ", string.Empty);
}

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
        "Event consumer method '{0}' must be an accessible, non-generic, non-static instance method that accepts exactly one IDomainEvent or IIntegrationEvent parameter (optionally with IEventContext and/or CancellationToken), and returns void/Task/ValueTask (subscriber) or Result<T>/Task<Result<T>>/ValueTask<Result<T>> for a domain request (responder)",
        category: "Elarion.Generators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor DuplicateResponder = new(
        id: "ELEVT004",
        title: "Duplicate request responder",
        messageFormat: "Request type '{0}' has more than one responder; exactly one responder is allowed",
        category: "Elarion.Generators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor ConsumerNotInModule = new(
        id: "ELEVT003",
        title: "Event consumer is not in any module",
        messageFormat:
        "Event consumer '{0}' is annotated with [ConsumeEvent] but its namespace is not under any [AppModule]; "
        + "it will not be registered. Move the consumer under a module's namespace so it is wired by that module",
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
        ResponderResultSync,
        ResponderResultTask,
        ResponderResultValueTask
    }

    private sealed record EventConsumerInfo(
        string ServiceTypeFqn,
        string ServiceNamespace,
        string MethodName,
        string EventTypeFqn,
        string Plane,
        string? ResponseTypeFqn,
        int Order,
        ReturnShape Return,
        ImmutableArray<ParameterKind> Parameters,
        string HintName);

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context) {
        context.RegisterSourceOutput(context.CompilationProvider, static (spc, compilation) => {
            if (!FrameworkFeatureTriggers.HasAssemblyTrigger(compilation, TriggerAttributeMetadataName)) {
                return;
            }

            var symbols = ResolveSymbols(compilation);
            if (symbols is null) {
                return;
            }

            var consumers = CollectConsumers(compilation, symbols, spc);
            ReportDuplicateResponders(consumers, spc);
            if (consumers.Count == 0) {
                return;
            }

            // Consumers are registered per module via ConfigureDefaultServices; there is no assembly-wide method.
            EmitPerModule(spc, ModuleScanner.Collect(compilation, spc.CancellationToken), consumers);
        });
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
            cancellationToken,
            task,
            taskGeneric,
            valueTask,
            valueTaskGeneric);
    }

    private static List<EventConsumerInfo> CollectConsumers(
        Compilation compilation,
        KnownSymbols symbols,
        SourceProductionContext spc) {
        var consumers = new List<EventConsumerInfo>();
        var seenMethods = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

        foreach (var syntaxTree in compilation.SyntaxTrees) {
            var semanticModel = compilation.GetSemanticModel(syntaxTree);
            var root = syntaxTree.GetRoot(spc.CancellationToken);

            foreach (var methodDeclaration in root.DescendantNodes().OfType<MethodDeclarationSyntax>()) {
                if (semanticModel.GetDeclaredSymbol(methodDeclaration, spc.CancellationToken) is not IMethodSymbol method ||
                    !seenMethods.Add(method)) {
                    continue;
                }

                if (!HasAttribute(method, symbols.ConsumeEventAttribute, out var consumeAttribute)) {
                    continue;
                }

                var location = methodDeclaration.Identifier.GetLocation();
                var containingType = method.ContainingType;
                if (containingType is null || !HasAttribute(containingType, symbols.ServiceAttribute, out _)) {
                    spc.ReportDiagnostic(Diagnostic.Create(ConsumerNotOnService, location, method.Name));
                    continue;
                }

                var consumer = TryCreateConsumer(method, containingType, consumeAttribute!, symbols, location, spc);
                if (consumer is not null) {
                    consumers.Add(consumer);
                }
            }
        }

        consumers.Sort(static (left, right) =>
            string.Compare(left.HintName, right.HintName, StringComparison.Ordinal));
        return consumers;
    }

    private static EventConsumerInfo? TryCreateConsumer(
        IMethodSymbol method,
        INamedTypeSymbol containingType,
        AttributeData consumeAttribute,
        KnownSymbols symbols,
        Location location,
        SourceProductionContext spc) {
        if (method.IsStatic ||
            method.IsGenericMethod ||
            !IsAccessible(method.DeclaredAccessibility) ||
            IsGenericOrNestedInGenericType(containingType)) {
            spc.ReportDiagnostic(Diagnostic.Create(InvalidConsumerSignature, location, method.Name));
            return null;
        }

        if (!TryResolveParameters(method, symbols, out var parameters, out var eventType, out var plane)) {
            spc.ReportDiagnostic(Diagnostic.Create(InvalidConsumerSignature, location, method.Name));
            return null;
        }

        if (!TryResolveReturn(method.ReturnType, symbols, out var returnShape, out var responseType)) {
            spc.ReportDiagnostic(Diagnostic.Create(InvalidConsumerSignature, location, method.Name));
            return null;
        }

        var isResponder = responseType is not null;
        if (isResponder && plane != "Domain") {
            // Only domain requests can have responders; integration events are fan-out only.
            spc.ReportDiagnostic(Diagnostic.Create(InvalidConsumerSignature, location, method.Name));
            return null;
        }

        var order = GetIntNamedArgument(consumeAttribute, "Order", 0);
        var eventFqn = eventType!.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var serviceFqn = containingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var serviceNamespace = containingType.ContainingNamespace is { IsGlobalNamespace: false } containing
            ? containing.ToDisplayString()
            : string.Empty;
        var responseFqn = responseType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var hintName = $"{GetHintName(containingType)}__{method.Name}__{GetHintName(eventType!)}";

        return new EventConsumerInfo(
            serviceFqn,
            serviceNamespace,
            method.Name,
            eventFqn,
            plane,
            responseFqn,
            order,
            returnShape,
            parameters,
            hintName);
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

    private static bool TryResolveReturn(
        ITypeSymbol returnType,
        KnownSymbols symbols,
        out ReturnShape shape,
        out ITypeSymbol? responseType) {
        shape = ReturnShape.SubscriberVoid;
        responseType = null;

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

        if (returnType is not INamedTypeSymbol { IsGenericType: true } named) {
            return false;
        }

        var definition = named.OriginalDefinition;
        if (SymbolEqualityComparer.Default.Equals(definition, symbols.Result)) {
            shape = ReturnShape.ResponderResultSync;
            responseType = named.TypeArguments[0];
            return true;
        }

        var isTask = SymbolEqualityComparer.Default.Equals(definition, symbols.TaskGeneric);
        var isValueTask = SymbolEqualityComparer.Default.Equals(definition, symbols.ValueTaskGeneric);
        if (!isTask && !isValueTask) {
            return false;
        }

        if (named.TypeArguments[0] is not INamedTypeSymbol { IsGenericType: true } inner ||
            !SymbolEqualityComparer.Default.Equals(inner.OriginalDefinition, symbols.Result)) {
            return false;
        }

        shape = isTask ? ReturnShape.ResponderResultTask : ReturnShape.ResponderResultValueTask;
        responseType = inner.TypeArguments[0];
        return true;
    }

    private static void ReportDuplicateResponders(
        IReadOnlyList<EventConsumerInfo> consumers,
        SourceProductionContext spc) {
        foreach (var group in consumers
                     .Where(consumer => consumer.ResponseTypeFqn is not null)
                     .GroupBy(consumer => consumer.EventTypeFqn, StringComparer.Ordinal)) {
            if (group.Count() < 2) {
                continue;
            }

            spc.ReportDiagnostic(Diagnostic.Create(DuplicateResponder, Location.None, group.Key));
        }
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

        foreach (var serviceFqn in consumers.Select(consumer => consumer.ServiceTypeFqn).Distinct(StringComparer.Ordinal)) {
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
        sb.AppendLine($"            EventType = typeof({consumer.EventTypeFqn}),");
        sb.AppendLine($"            Plane = global::Elarion.Abstractions.Messaging.EventPlane.{consumer.Plane},");
        sb.AppendLine($"            ServiceType = typeof({consumer.ServiceTypeFqn}),");

        if (consumer.ResponseTypeFqn is null) {
            sb.AppendLine($"            Order = {consumer.Order},");
            AppendSubscriberInvoke(sb, consumer);
        }
        else {
            sb.AppendLine($"            ResponseType = typeof({consumer.ResponseTypeFqn}),");
            AppendResponderInvoke(sb, consumer);
        }

        sb.AppendLine("        });");
    }

    private static void AppendSubscriberInvoke(StringBuilder sb, EventConsumerInfo consumer) {
        var arguments = BuildArguments(consumer, "@event");
        var invocation = $"service.{consumer.MethodName}({arguments})";

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
    }

    private static void AppendResponderInvoke(StringBuilder sb, EventConsumerInfo consumer) {
        var arguments = BuildArguments(consumer, "request");
        var invocation = $"service.{consumer.MethodName}({arguments})";

        if (consumer.Return == ReturnShape.ResponderResultSync) {
            sb.AppendLine("            InvokeRequestAsync = static (serviceProvider, request, context, ct) =>");
            sb.AppendLine("            {");
            sb.AppendLine($"                var service = serviceProvider.GetRequiredService<{consumer.ServiceTypeFqn}>();");
            sb.AppendLine($"                return new global::System.Threading.Tasks.ValueTask<object>((object){invocation});");
            sb.AppendLine("            }");
            return;
        }

        sb.AppendLine("            InvokeRequestAsync = static async (serviceProvider, request, context, ct) =>");
        sb.AppendLine("            {");
        sb.AppendLine($"                var service = serviceProvider.GetRequiredService<{consumer.ServiceTypeFqn}>();");
        sb.AppendLine($"                return (object)await {invocation};");
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

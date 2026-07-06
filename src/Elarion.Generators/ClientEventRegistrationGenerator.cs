using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Elarion.Generators;

/// <summary>
/// Generates per-module client-event topic registration (ADR-0043). Discovers every non-abstract type
/// implementing <c>IClientEvent</c>, groups it under its owning <c>[AppModule]</c> by longest-prefix
/// namespace match, infers the topic name <c>{module}.{name}</c> (camel-cased, a trailing <c>Event</c>
/// suffix stripped; <c>[ClientEvent("name")]</c> overrides the full name), reads the contract's
/// <c>[RequirePermission]</c>/<c>[RequireRole]</c> attributes as subscribe-time requirements, and emits
/// <c>Add{Module}ClientEvents(this IServiceCollection)</c> — wired into the module's
/// <c>ConfigureDefaultServices</c> like the other categories, so a topic exists just by declaring the
/// contract, and disappears with its module's feature gate.
/// <para>Trigger: <c>[assembly: UseElarion]</c> or <c>[assembly: GenerateClientEventTopics]</c>.</para>
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ClientEventRegistrationGenerator : IIncrementalGenerator
{
    private const string ClientEventInterfaceMetadataName = "Elarion.Abstractions.ClientEvents.IClientEvent";
    private const string ClientEventAttributeMetadataName = "Elarion.Abstractions.ClientEvents.ClientEventAttribute";
    private const string RequirePermissionAttributeMetadataName = "Elarion.Abstractions.Authorization.RequirePermissionAttribute";
    private const string RequireRoleAttributeMetadataName = "Elarion.Abstractions.Authorization.RequireRoleAttribute";
    private const string ClientEventsBuilderMetadataName = "Elarion.ClientEvents.ClientEventsBuilder";
    private const string TriggerAttributeMetadataName = "Elarion.Abstractions.GenerateClientEventTopicsAttribute";

    private static readonly DiagnosticDescriptor EventNotInModule = new(
        "ELCEV001",
        "Client event is not in any module",
        "Client event '{0}' is not under any [AppModule] namespace, so no topic is registered for it; move it under a module or register the topic manually via AddElarionClientEvents",
        "Elarion.Abstractions.ClientEvents",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor DuplicateTopicName = new(
        "ELCEV002",
        "Duplicate client-event topic name",
        "Client event '{0}' resolves to topic '{1}', which is also declared by another contract; topic names must be unique — rename the type or disambiguate with [ClientEvent(\"…\")]",
        "Elarion.Abstractions.ClientEvents",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor ClientEventsPackageMissing = new(
        "ELCEV003",
        "Client events are not registered without Elarion.ClientEvents",
        "This compilation declares IClientEvent contracts but does not reference Elarion.ClientEvents, so no topics are registered and nothing reaches the wire",
        "Elarion.Abstractions.ClientEvents",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private sealed record PermissionRequirement(string Resource, string Verb);

    private sealed record ClientEventInfo(
        string Fqn,
        string TypeName,
        string Namespace,
        string? NameOverride,
        EquatableArray<PermissionRequirement> Permissions,
        EquatableArray<string> Roles,
        bool BuilderAvailable,
        LocationInfo Location);

    private static class TrackingNames
    {
        public const string Events = "ClientEventContracts";
        public const string Combined = "ClientEventContractsCombined";
    }

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Structural trigger: the marker is an interface, not an attribute, so discovery filters type
        // declarations with a base list and resolves the symbol in the transform.
        var events = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is TypeDeclarationSyntax { BaseList: not null },
                static (ctx, _) => CreateEvent(ctx))
            .Where(static clientEvent => clientEvent is not null)
            .Select(static (clientEvent, _) => clientEvent!)
            .Collect()
            .WithTrackingName(TrackingNames.Events);

        var modules = ModuleProviders.CollectModules(context);
        var trigger = ModuleProviders.HasTrigger(context, TriggerAttributeMetadataName);

        var combined = events.Combine(modules).Combine(trigger).WithTrackingName(TrackingNames.Combined);

        context.RegisterSourceOutput(combined, static (spc, source) =>
        {
            var ((eventList, modules), hasTrigger) = source;
            if (!hasTrigger)
            {
                return;
            }

            Emit(spc, eventList, modules);
        });
    }

    private static ClientEventInfo? CreateEvent(GeneratorSyntaxContext ctx)
    {
        var declaration = (TypeDeclarationSyntax)ctx.Node;
        if (ctx.SemanticModel.GetDeclaredSymbol(declaration) is not INamedTypeSymbol type ||
            type.IsAbstract ||
            type.TypeKind != TypeKind.Class)
        {
            return null;
        }

        var compilation = ctx.SemanticModel.Compilation;
        var markerInterface = compilation.GetTypeByMetadataName(ClientEventInterfaceMetadataName);
        if (markerInterface is null ||
            !type.AllInterfaces.Any(iface => SymbolEqualityComparer.Default.Equals(iface, markerInterface)))
        {
            return null;
        }

        string? nameOverride = null;
        var permissions = ImmutableArray.CreateBuilder<PermissionRequirement>();
        var roles = ImmutableArray.CreateBuilder<string>();
        var overrideAttribute = compilation.GetTypeByMetadataName(ClientEventAttributeMetadataName);
        var permissionAttribute = compilation.GetTypeByMetadataName(RequirePermissionAttributeMetadataName);
        var roleAttribute = compilation.GetTypeByMetadataName(RequireRoleAttributeMetadataName);

        foreach (var attribute in type.GetAttributes())
        {
            var attributeClass = attribute.AttributeClass;
            if (attributeClass is null)
            {
                continue;
            }

            if (SymbolEqualityComparer.Default.Equals(attributeClass, overrideAttribute) &&
                attribute.ConstructorArguments.Length >= 1 &&
                attribute.ConstructorArguments[0].Value is string overrideName &&
                !string.IsNullOrWhiteSpace(overrideName))
            {
                nameOverride = overrideName;
            }
            else if (SymbolEqualityComparer.Default.Equals(attributeClass, permissionAttribute) &&
                attribute.ConstructorArguments.Length >= 2 &&
                attribute.ConstructorArguments[0].Value is string resource &&
                attribute.ConstructorArguments[1].Value is string verb)
            {
                permissions.Add(new PermissionRequirement(resource, verb));
            }
            else if (SymbolEqualityComparer.Default.Equals(attributeClass, roleAttribute) &&
                attribute.ConstructorArguments.Length >= 1 &&
                attribute.ConstructorArguments[0].Value is string role)
            {
                roles.Add(role);
            }
        }

        var ns = type.ContainingNamespace is { IsGlobalNamespace: false } containing
            ? containing.ToDisplayString()
            : string.Empty;
        return new ClientEventInfo(
            type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            type.Name,
            ns,
            nameOverride,
            permissions.ToImmutable(),
            roles.ToImmutable(),
            compilation.GetTypeByMetadataName(ClientEventsBuilderMetadataName) is not null,
            LocationInfo.From(type));
    }

    private static void Emit(
        SourceProductionContext spc,
        ImmutableArray<ClientEventInfo> events,
        EquatableArray<ModuleScanner.Module> modules)
    {
        // Partial declarations produce one candidate per declaration with a base list; keep one per type.
        var distinctEvents = events
            .GroupBy(static clientEvent => clientEvent.Fqn, StringComparer.Ordinal)
            .Select(static group => group.First())
            .OrderBy(static clientEvent => clientEvent.Fqn, StringComparer.Ordinal)
            .ToList();

        if (distinctEvents.Count == 0)
        {
            return;
        }

        if (!distinctEvents[0].BuilderAvailable)
        {
            // Emitting AddElarionClientEvents calls without the package referenced would not compile;
            // fail closed with a warning instead of silently generating nothing.
            spc.ReportDiagnostic(
                DiagnosticInfo.Create(ClientEventsPackageMissing, distinctEvents[0].Location).ToDiagnostic());
            return;
        }

        var moduleTopics = modules.ToDictionary(
            module => module,
            _ => new List<(string Fqn, string Topic, ClientEventInfo Event)>());
        var resolved = new List<(ModuleScanner.Module Module, string Topic, ClientEventInfo Event)>();

        foreach (var clientEvent in distinctEvents)
        {
            ModuleScanner.Module? bestMatch = null;
            foreach (var module in modules)
                if (ModuleScanner.IsInScope(clientEvent.Namespace, module.Namespace) &&
                    (bestMatch is null || module.Namespace.Length > bestMatch.Namespace.Length))
                    bestMatch = module;

            if (bestMatch is null)
            {
                spc.ReportDiagnostic(
                    DiagnosticInfo.Create(EventNotInModule, clientEvent.Location, clientEvent.Fqn).ToDiagnostic());
                continue;
            }

            resolved.Add((bestMatch, clientEvent.NameOverride ?? InferTopicName(bestMatch.Name, clientEvent.TypeName), clientEvent));
        }

        // Topic names are a global wire vocabulary: collisions are errors and the colliding topics are
        // withheld (registering both would throw at catalog composition anyway).
        foreach (var group in resolved.GroupBy(static entry => entry.Topic, StringComparer.Ordinal))
        {
            if (group.Count() > 1)
            {
                foreach (var (_, topic, clientEvent) in group)
                {
                    spc.ReportDiagnostic(
                        DiagnosticInfo.Create(DuplicateTopicName, clientEvent.Location, clientEvent.Fqn, topic).ToDiagnostic());
                }
                continue;
            }

            var entry = group.First();
            moduleTopics[entry.Module].Add((entry.Event.Fqn, entry.Topic, entry.Event));
        }

        foreach (var kvp in moduleTopics
                     .Where(static x => x.Value.Count > 0)
                     .OrderBy(static x => x.Key.Name, StringComparer.Ordinal))
        {
            var module = kvp.Key;
            var moduleName = module.Name;

            var sb = new StringBuilder();
            sb.AppendLine("using Elarion.ClientEvents;");
            sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
            sb.AppendLine();
            sb.AppendLine($"namespace {module.Namespace};");
            sb.AppendLine();
            sb.AppendLine("/// <summary>");
            sb.AppendLine($"/// Extension methods for registering {moduleName} module client-event topics.");
            sb.AppendLine("/// </summary>");
            sb.AppendLine($"public static class {moduleName}ClientEventExtensions");
            sb.AppendLine("{");
            sb.AppendLine("    /// <summary>");
            sb.AppendLine($"    /// Registers all IClientEvent topics for the {moduleName} module.");
            sb.AppendLine("    /// </summary>");
            sb.AppendLine($"    public static IServiceCollection Add{moduleName}ClientEvents(");
            sb.AppendLine("        this IServiceCollection services)");
            sb.AppendLine("    {");
            sb.AppendLine("        services.AddElarionClientEvents(events =>");
            sb.AppendLine("        {");

            foreach (var (fqn, topic, clientEvent) in kvp.Value.OrderBy(static x => x.Topic, StringComparer.Ordinal))
            {
                var topicLiteral = SymbolDisplay.FormatLiteral(topic, quote: true);
                if (clientEvent.Permissions.Count == 0 && clientEvent.Roles.Count == 0)
                {
                    sb.AppendLine($"            events.AddTopic<{fqn}>({topicLiteral});");
                    continue;
                }

                sb.AppendLine($"            events.AddTopic<{fqn}>({topicLiteral}, static topic => topic");
                var requirementLines = new List<string>();
                foreach (var permission in clientEvent.Permissions)
                {
                    requirementLines.Add(
                        $"                .RequirePermission({SymbolDisplay.FormatLiteral(permission.Resource, quote: true)}, {SymbolDisplay.FormatLiteral(permission.Verb, quote: true)})");
                }
                foreach (var role in clientEvent.Roles)
                {
                    requirementLines.Add(
                        $"                .RequireRole({SymbolDisplay.FormatLiteral(role, quote: true)})");
                }
                for (var index = 0; index < requirementLines.Count; index += 1)
                {
                    sb.AppendLine(index == requirementLines.Count - 1 ? requirementLines[index] + ");" : requirementLines[index]);
                }
            }

            sb.AppendLine("        });");
            sb.AppendLine("        return services;");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            spc.AddSource(
                $"{moduleName}ClientEventExtensions.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));

            var nsPrefix = module.Namespace.Length > 0 ? $"global::{module.Namespace}." : "global::";
            ModuleDefaultsEmitter.EmitFiller(
                spc,
                module.Namespace,
                module.TypeName,
                ModuleDefaultsEmitter.AddClientEventsMethod,
                "ClientEvents",
                $"{nsPrefix}{moduleName}ClientEventExtensions.Add{moduleName}ClientEvents(services);");
        }
    }

    /// <summary>
    /// Infers <c>{module}.{name}</c>: both parts camel-cased, a trailing <c>Event</c> suffix stripped from
    /// the type name (<c>InvoiceChangedEvent</c> and <c>InvoiceChanged</c> both → <c>invoiceChanged</c>).
    /// </summary>
    private static string InferTopicName(string moduleName, string typeName)
    {
        var name = typeName;
        if (name.Length > "Event".Length && name.EndsWith("Event", StringComparison.Ordinal))
        {
            name = name.Substring(0, name.Length - "Event".Length);
        }

        return CamelCase(moduleName) + "." + CamelCase(name);
    }

    private static string CamelCase(string value) =>
        value.Length > 0 && char.IsUpper(value[0])
            ? char.ToLowerInvariant(value[0]) + value.Substring(1)
            : value;
}

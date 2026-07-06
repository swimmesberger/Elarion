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
    private const string ActorContextMetadataName = "Elarion.Actors.IActorContext";
    private const string ActorContextGenericMetadataName = "Elarion.Actors.IActorContext`1";
    private const string CancellationTokenMetadataName = "System.Threading.CancellationToken";
    private const string TaskMetadataName = "System.Threading.Tasks.Task";
    private const string TaskGenericMetadataName = "System.Threading.Tasks.Task`1";
    private const string ValueTaskMetadataName = "System.Threading.Tasks.ValueTask";
    private const string ValueTaskGenericMetadataName = "System.Threading.Tasks.ValueTask`1";

    private const string ActorSingletonKeyFqn = "global::Elarion.Actors.ActorSingletonKey";
    private const string UnitFqn = "global::Elarion.Abstractions.Results.Unit";
    private const string CancellationTokenFqn = "global::System.Threading.CancellationToken";
    private const string TaskFqn = "global::System.Threading.Tasks.Task";
    private const string ValueTaskFqn = "global::System.Threading.Tasks.ValueTask";

    private static readonly DiagnosticDescriptor InvalidActorType = new(
        id: "ELACT001",
        title: "Invalid [Actor] type",
        messageFormat:
        "Type '{0}' is annotated with [Actor] but must be a non-static, non-abstract, non-generic, "
        + "non-nested class for the facade generator to wrap it",
        category: "Elarion.Generators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidActorMethod = new(
        id: "ELACT002",
        title: "Invalid actor method",
        messageFormat:
        "Public method '{0}' on [Actor] class '{1}' cannot be exposed through the facade: actor methods "
        + "must be non-generic instance methods returning Task/Task<T>/ValueTask/ValueTask<T>, without "
        + "ref/out/in or ref-struct parameters and with at most one CancellationToken parameter",
        category: "Elarion.Generators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor ActorNotInModule = new(
        id: "ELACT003",
        title: "Actor is not in any module",
        messageFormat:
        "Actor '{0}' is annotated with [Actor] but its namespace is not under any [AppModule]; "
        + "it will not be registered. Move the actor under a module's namespace so it is wired by that module.",
        category: "Elarion.Generators",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor AmbiguousActorKey = new(
        id: "ELACT004",
        title: "Ambiguous actor key",
        messageFormat:
        "Actor '{0}' declares conflicting keys: use a single IActorContext<TKey> constructor parameter "
        + "(or one [Actor(KeyType = ...)] matching it) to make the actor keyed",
        category: "Elarion.Generators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor InvalidActorConstructor = new(
        id: "ELACT005",
        title: "Invalid actor constructor",
        messageFormat:
        "Actor '{0}' must have exactly one public constructor so the generator can emit its activator",
        category: "Elarion.Generators",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private enum ReturnShape {
        TaskVoid,
        TaskOfResult,
        ValueTaskVoid,
        ValueTaskOfResult
    }

    private enum CtorParameterKind {
        Context,
        Service
    }

    private sealed record CtorParameterInfo(CtorParameterKind Kind, string TypeFqn);

    private sealed record MethodParameterInfo(string Name, string TypeFqn, bool IsCancellationToken);

    private sealed record ActorMethodInfo(
        string Name,
        ReturnShape Return,
        string ResultTypeFqn,
        EquatableArray<MethodParameterInfo> Parameters,
        string WorkItemClassName);

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
        EquatableArray<CtorParameterInfo> CtorParameters,
        EquatableArray<ActorMethodInfo> Methods,
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
            if (!hasTrigger) {
                return;
            }

            foreach (var result in results) {
                foreach (var diagnostic in result.Diagnostics) {
                    spc.ReportDiagnostic(diagnostic.ToDiagnostic());
                }
            }

            var actors = new List<ActorInfo>();
            foreach (var result in results) {
                if (result.Actor is not null) {
                    actors.Add(result.Actor);
                }
            }

            actors.Sort(static (left, right) =>
                string.Compare(left.HintName, right.HintName, StringComparison.Ordinal));

            if (actors.Count == 0) {
                return;
            }

            foreach (var actor in actors) {
                spc.AddSource($"{actor.HintName}.Actor.g.cs", SourceText.From(GenerateFacade(actor), Encoding.UTF8));
            }

            EmitPerModule(spc, modules, actors);
        });
    }

    private static ActorResult? CreateActorResult(GeneratorAttributeSyntaxContext ctx, CancellationToken cancellationToken) {
        if (ctx.TargetSymbol is not INamedTypeSymbol type) {
            return null;
        }

        var location = LocationInfo.From(type);
        var typeDisplay = type.ToDisplayString();

        if (type.IsStatic || type.IsAbstract || type.IsGenericType || type.ContainingType is not null) {
            return new ActorResult(null, new[] {
                DiagnosticInfo.Create(InvalidActorType, location, typeDisplay)
            }.ToEquatableArray());
        }

        var compilation = ctx.SemanticModel.Compilation;
        var taskSymbol = compilation.GetTypeByMetadataName(TaskMetadataName);
        var taskGenericSymbol = compilation.GetTypeByMetadataName(TaskGenericMetadataName);
        var valueTaskSymbol = compilation.GetTypeByMetadataName(ValueTaskMetadataName);
        var valueTaskGenericSymbol = compilation.GetTypeByMetadataName(ValueTaskGenericMetadataName);
        var cancellationTokenSymbol = compilation.GetTypeByMetadataName(CancellationTokenMetadataName);
        var contextSymbol = compilation.GetTypeByMetadataName(ActorContextMetadataName);
        var contextGenericSymbol = compilation.GetTypeByMetadataName(ActorContextGenericMetadataName);

        var diagnostics = new List<DiagnosticInfo>();

        // Attribute knobs.
        string? explicitName = null;
        ITypeSymbol? attributeKeyType = null;
        var mailboxCapacity = 0;
        var mailboxFailFast = false;
        double idleTimeoutSeconds = 0;
        double callTimeoutSeconds = 0;
        foreach (var named in ctx.Attributes[0].NamedArguments) {
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
            }
        }

        var reentrant = false;
        foreach (var attribute in type.GetAttributes()) {
            if (attribute.AttributeClass?.ToDisplayString() == ReentrantAttributeDisplayName) {
                reentrant = true;
                break;
            }
        }

        // Constructor: exactly one public constructor carries the activation dependencies.
        var constructors = type.InstanceConstructors
            .Where(static ctor => ctor.DeclaredAccessibility == Accessibility.Public)
            .ToList();
        if (constructors.Count != 1) {
            return new ActorResult(null, new[] {
                DiagnosticInfo.Create(InvalidActorConstructor, location, typeDisplay)
            }.ToEquatableArray());
        }

        ITypeSymbol? contextKeyType = null;
        var ambiguousKey = false;
        var ctorParameters = new List<CtorParameterInfo>();
        foreach (var parameter in constructors[0].Parameters) {
            if (parameter.Type is INamedTypeSymbol named && contextGenericSymbol is not null &&
                SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, contextGenericSymbol)) {
                var parameterKeyType = named.TypeArguments[0];
                if (contextKeyType is not null &&
                    !SymbolEqualityComparer.Default.Equals(contextKeyType, parameterKeyType)) {
                    ambiguousKey = true;
                }

                contextKeyType = parameterKeyType;
                ctorParameters.Add(new CtorParameterInfo(CtorParameterKind.Context, string.Empty));
            }
            else if (contextSymbol is not null &&
                     SymbolEqualityComparer.Default.Equals(parameter.Type, contextSymbol)) {
                ctorParameters.Add(new CtorParameterInfo(CtorParameterKind.Context, string.Empty));
            }
            else {
                ctorParameters.Add(new CtorParameterInfo(
                    CtorParameterKind.Service,
                    parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
            }
        }

        if (ambiguousKey ||
            (attributeKeyType is not null && contextKeyType is not null &&
             !SymbolEqualityComparer.Default.Equals(attributeKeyType, contextKeyType))) {
            return new ActorResult(null, new[] {
                DiagnosticInfo.Create(AmbiguousActorKey, location, typeDisplay)
            }.ToEquatableArray());
        }

        var keyType = attributeKeyType ?? contextKeyType;

        // Public instance methods become facade methods; lifecycle hooks stay off the facade.
        var methods = new List<ActorMethodInfo>();
        var workItemNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in type.GetMembers()) {
            cancellationToken.ThrowIfCancellationRequested();
            if (member is not IMethodSymbol method ||
                method.MethodKind != MethodKind.Ordinary ||
                method.DeclaredAccessibility != Accessibility.Public ||
                method.IsStatic ||
                method.IsOverride ||
                method.IsImplicitlyDeclared) {
                continue;
            }

            if (method.Name is "OnActivateAsync" or "OnDeactivateAsync") {
                continue;
            }

            ReturnShape shape;
            string resultTypeFqn = string.Empty;
            if (SymbolEqualityComparer.Default.Equals(method.ReturnType, taskSymbol)) {
                shape = ReturnShape.TaskVoid;
            }
            else if (SymbolEqualityComparer.Default.Equals(method.ReturnType, valueTaskSymbol)) {
                shape = ReturnShape.ValueTaskVoid;
            }
            else if (method.ReturnType is INamedTypeSymbol { IsGenericType: true } generic &&
                     SymbolEqualityComparer.Default.Equals(generic.OriginalDefinition, taskGenericSymbol)) {
                shape = ReturnShape.TaskOfResult;
                resultTypeFqn = generic.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            }
            else if (method.ReturnType is INamedTypeSymbol { IsGenericType: true } genericValueTask &&
                     SymbolEqualityComparer.Default.Equals(genericValueTask.OriginalDefinition, valueTaskGenericSymbol)) {
                shape = ReturnShape.ValueTaskOfResult;
                resultTypeFqn = genericValueTask.TypeArguments[0]
                    .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
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
                                          SymbolEqualityComparer.Default.Equals(parameter.Type, cancellationTokenSymbol);
                if (isCancellationToken) {
                    cancellationTokenCount++;
                }

                parameters.Add(new MethodParameterInfo(
                    parameter.Name,
                    parameter.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    isCancellationToken));
            }

            if (invalid || cancellationTokenCount > 1) {
                diagnostics.Add(DiagnosticInfo.Create(
                    InvalidActorMethod, LocationInfo.From(method), method.Name, typeDisplay));
                continue;
            }

            var workItemName = method.Name + "WorkItem";
            var suffix = 1;
            while (!workItemNames.Add(workItemName)) {
                workItemName = method.Name + "WorkItem" + suffix++;
            }

            methods.Add(new ActorMethodInfo(
                method.Name,
                shape,
                resultTypeFqn,
                parameters.ToEquatableArray(),
                workItemName));
        }

        var actorNamespace = type.ContainingNamespace is { IsGlobalNamespace: false } containing
            ? containing.ToDisplayString()
            : string.Empty;
        var actorName = explicitName ?? DeriveActorName(type.Name);
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
            ctorParameters.ToEquatableArray(),
            methods.ToEquatableArray(),
            GetHintName(type));

        return new ActorResult(actor, diagnostics.ToEquatableArray());
    }

    private static string DeriveActorName(string typeName) =>
        typeName.Length > 5 && typeName.EndsWith("Actor", StringComparison.Ordinal)
            ? typeName.Substring(0, typeName.Length - 5)
            : typeName;

    private static string GetHintName(INamedTypeSymbol type) {
        var display = type.ToDisplayString();
        var sb = new StringBuilder(display.Length);
        foreach (var ch in display) {
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        }

        return sb.ToString();
    }

    private static void EmitPerModule(
        SourceProductionContext spc,
        IReadOnlyList<ModuleScanner.Module> modules,
        IReadOnlyList<ActorInfo> actors) {
        if (modules.Count == 0) {
            return;
        }

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
        sb.AppendLine($"/// Typed facade over the <see cref=\"{DocCref(actor.ActorTypeFqn)}\"/> actor: each call is enqueued");
        sb.AppendLine("/// into the actor's mailbox and executed under its single-threaded guarantee. Resolve via");
        sb.AppendLine("/// <see cref=\"global::Elarion.Actors.IActorSystem\"/>.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine($"public interface {actor.FacadeInterfaceName} : {facadeMarker}");
        sb.AppendLine("{");
        var first = true;
        foreach (var method in actor.Methods) {
            if (!first) {
                sb.AppendLine();
            }

            first = false;
            sb.AppendLine($"    /// <summary>Invokes <c>{Plain(actor.ActorTypeFqn)}.{method.Name}</c> through the actor mailbox.</summary>");
            sb.AppendLine($"    {FacadeReturnType(method)} {method.Name}({FacadeParameterList(method)});");
        }

        sb.AppendLine("}");
        sb.AppendLine();

        sb.AppendLine($"internal sealed class {actor.FacadeImplName} : {actor.FacadeInterfaceName}");
        sb.AppendLine("{");
        sb.AppendLine($"    private readonly global::Elarion.Actors.ActorHandle<{actor.ActorTypeFqn}> _handle;");
        sb.AppendLine();
        sb.AppendLine($"    public {actor.FacadeImplName}(global::Elarion.Actors.ActorHandle<{actor.ActorTypeFqn}> handle)");
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
        var invoke = $"_handle.InvokeAsync(new {method.WorkItemClassName}({arguments}), {tokenName})";
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
        var resultFqn = method.Return is ReturnShape.TaskVoid or ReturnShape.ValueTaskVoid
            ? UnitFqn
            : method.ResultTypeFqn;
        var dataParameters = method.Parameters.Where(static p => !p.IsCancellationToken).ToList();

        sb.AppendLine($"    private sealed class {method.WorkItemClassName} : global::Elarion.Actors.ActorWorkItem<{actor.ActorTypeFqn}, {resultFqn}>");
        sb.AppendLine("    {");
        foreach (var parameter in dataParameters) {
            sb.AppendLine($"        private readonly {parameter.TypeFqn} _{parameter.Name};");
        }

        if (dataParameters.Count > 0) {
            sb.AppendLine();
        }

        var ctorParameters = string.Join(", ", dataParameters.Select(static p => $"{p.TypeFqn} {p.Name}"));
        sb.AppendLine($"        public {method.WorkItemClassName}({ctorParameters})");
        sb.AppendLine("        {");
        foreach (var parameter in dataParameters) {
            sb.AppendLine($"            _{parameter.Name} = {parameter.Name};");
        }

        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine($"        public override string MethodName => \"{method.Name}\";");
        sb.AppendLine();
        var invokeArguments = string.Join(", ", method.Parameters
            .Select(static p => p.IsCancellationToken ? "cancellationToken" : $"_{p.Name}"));
        sb.AppendLine($"        protected override async global::System.Threading.Tasks.ValueTask<{resultFqn}> InvokeAsync({actor.ActorTypeFqn} actor, {CancellationTokenFqn} cancellationToken)");
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
        sb.AppendLine($"    public static global::Microsoft.Extensions.DependencyInjection.IServiceCollection {methodName}(this global::Microsoft.Extensions.DependencyInjection.IServiceCollection services)");
        sb.AppendLine("    {");
        sb.AppendLine("        global::Elarion.Actors.ActorServiceCollectionExtensions.AddElarionActorSystem(services);");
        foreach (var actor in actors) {
            AppendActorRegistration(sb, actor);
        }

        sb.AppendLine("        return services;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static void AppendActorRegistration(StringBuilder sb, ActorInfo actor) {
        var keyFqn = actor.KeyTypeFqn ?? ActorSingletonKeyFqn;
        var facadeFqn = actor.ActorNamespace.Length > 0
            ? $"global::{actor.ActorNamespace}.{actor.FacadeInterfaceName}"
            : $"global::{actor.FacadeInterfaceName}";
        var facadeImplFqn = actor.ActorNamespace.Length > 0
            ? $"global::{actor.ActorNamespace}.{actor.FacadeImplName}"
            : $"global::{actor.FacadeImplName}";

        var activatorArguments = string.Join(", ", actor.CtorParameters.Select(static parameter =>
            parameter.Kind == CtorParameterKind.Context
                ? "context"
                : $"serviceProvider.GetRequiredService<{parameter.TypeFqn}>()"));

        sb.AppendLine();
        sb.AppendLine($"        global::Elarion.Actors.ActorServiceCollectionExtensions.AddElarionActor(services, new global::Elarion.Actors.ActorRegistration<{actor.ActorTypeFqn}, {keyFqn}, {facadeFqn}>");
        sb.AppendLine("        {");
        sb.AppendLine($"            Name = \"{actor.ActorName}\",");
        sb.AppendLine("            Options = new global::Elarion.Actors.ActorOptions");
        sb.AppendLine("            {");
        sb.AppendLine($"                MailboxCapacity = {(actor.MailboxCapacity > 0 ? actor.MailboxCapacity.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null")},");
        sb.AppendLine($"                MailboxFullMode = global::Elarion.Actors.ActorMailboxFullMode.{(actor.MailboxFailFast ? "Fail" : "Wait")},");
        sb.AppendLine($"                IdleTimeout = {TimeoutExpression(actor.IdleTimeoutSeconds, "DefaultIdleTimeout")},");
        sb.AppendLine($"                CallTimeout = {TimeoutExpression(actor.CallTimeoutSeconds, "DefaultCallTimeout")},");
        sb.AppendLine($"                Reentrant = {(actor.Reentrant ? "true" : "false")}");
        sb.AppendLine("            },");
        sb.AppendLine($"            Activator = static (serviceProvider, context) => new {actor.ActorTypeFqn}({activatorArguments}),");
        sb.AppendLine($"            Facade = static handle => new {facadeImplFqn}(handle)");
        sb.AppendLine("        });");
    }

    private static string TimeoutExpression(double seconds, string defaultProperty) {
        if (seconds < 0) {
            return "null";
        }

        if (seconds == 0) {
            return $"global::Elarion.Actors.ActorOptions.{defaultProperty}";
        }

        return $"global::System.TimeSpan.FromSeconds({seconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture)})";
    }

    private static string FacadeReturnType(ActorMethodInfo method) => method.Return switch {
        ReturnShape.TaskVoid => TaskFqn,
        ReturnShape.TaskOfResult => $"{TaskFqn}<{method.ResultTypeFqn}>",
        ReturnShape.ValueTaskVoid => ValueTaskFqn,
        _ => $"{ValueTaskFqn}<{method.ResultTypeFqn}>"
    };

    private static string FacadeTokenName(ActorMethodInfo method) {
        foreach (var parameter in method.Parameters) {
            if (parameter.IsCancellationToken) {
                return parameter.Name;
            }
        }

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

        if (!hasToken) {
            parts.Add($"{CancellationTokenFqn} {FacadeTokenName(method)} = default");
        }

        return string.Join(", ", parts);
    }

    private static string Plain(string fqn) => fqn.Replace("global::", string.Empty);

    private static string DocCref(string fqn) => Plain(fqn).Replace('<', '{').Replace('>', '}');
}

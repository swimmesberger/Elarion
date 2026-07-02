using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Elarion.Generators;

/// <summary>
/// Emits a per-module <c>Microsoft.Extensions.Validation</c> <c>IValidatableInfoResolver</c> for the module's
/// handler request types (ADR-0027). Request types are discovered structurally from the handlers' declared
/// <c>IHandler&lt;TRequest, TResponse&gt;</c> interface — no extra attribute — and their type graphs are walked
/// for <c>System.ComponentModel.DataAnnotations</c> validation attributes (or <c>IValidatableObject</c>) via the
/// shared <see cref="ValidatableTypeWalker"/>, the same computation <see cref="HandlerRegistrationGenerator"/>
/// uses to auto-attach the framework <c>ValidationDecorator</c>.
/// <para>
/// The emitted <c>ValidatableTypeInfo</c>/<c>ValidatablePropertyInfo</c> subclasses return <b>cached,
/// constant-constructed attribute arrays</b> — every attribute is reconstructed from its compile-time
/// <c>AttributeData</c> as typed literals, so no runtime attribute reflection remains (unlike Microsoft's
/// bundled generator). Registration flows through the module's gated <c>ConfigureDefaultServices</c> hook
/// (<c>AddValidators</c>), so a disabled module contributes no validation metadata.
/// </para>
/// <para>
/// Generation is conditional on the compilation referencing <c>Elarion.Validation</c>. When validatable request
/// types exist without that reference, the attributes would be documented in the exported schemas but silently
/// unenforced — reported as <c>ELVAL002</c> so it is a visible choice.
/// </para>
/// <para>
/// Trigger: annotate the Application assembly with <c>[assembly: UseElarion]</c> or
/// <c>[assembly: GenerateModuleHandlers]</c> (validation registration is a handler concern and follows the
/// handler trigger).
/// </para>
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ValidationResolverGenerator : IIncrementalGenerator
{
    private const string TriggerAttributeMetadataName =
        "Elarion.Abstractions.GenerateModuleHandlersAttribute";

    /// <summary>The reference probe: present only when the compilation references <c>Elarion.Validation</c>.</summary>
    private const string ElarionValidationExtensionsMetadataName =
        "Elarion.Validation.ElarionValidationServiceCollectionExtensions";

    private const string ValidationAttributeFqn =
        "global::System.ComponentModel.DataAnnotations.ValidationAttribute";

    private const string ValidatablePropertyInfoFqn =
        "global::Microsoft.Extensions.Validation.ValidatablePropertyInfo";

    private const string ValidatableInfoFqn =
        "global::Microsoft.Extensions.Validation.IValidatableInfo";

    private static readonly DiagnosticDescriptor ValidationNotEnforcedDescriptor = new(
        "ELVAL002",
        "Validation attributes are not enforced without Elarion.Validation",
        "Handler '{0}' has request type '{1}' carrying validation attributes, but the compilation does not "
        + "reference Elarion.Validation, so the constraints appear in exported schemas but are not enforced at "
        + "run time; reference Elarion.Validation and call AddElarionValidation()",
        "Elarion.Abstractions.Validation",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private sealed record ModuleResolverModel(
        string ModuleName,
        string ModuleNamespace,
        string ModuleTypeName,
        EquatableArray<ValidatableTypeModel> Types);

    private sealed record ValidationModel(
        EquatableArray<ModuleResolverModel> Modules,
        EquatableArray<DiagnosticInfo> Diagnostics)
    {
        public static readonly ValidationModel Empty = new(
            EquatableArray<ModuleResolverModel>.Empty, EquatableArray<DiagnosticInfo>.Empty);
    }

    private static class TrackingNames
    {
        public const string Candidates = "ValidationCandidates";
        public const string Resolvers = "ValidationResolvers";
        public const string Combined = "ValidationCombined";
    }

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Stage one (cached per syntax tree): shared handler-candidate discovery — metadata names only.
        var handlerCandidates = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList: not null },
                transform: static (ctx, ct) => HandlerCandidates.Identify(ctx, ct))
            .Where(static candidate => candidate is not null)
            .Collect()
            .Select(static (candidates, _) => HandlerCandidates.FlattenSortedDistinct(candidates))
            .WithTrackingName(TrackingNames.Candidates);

        var modules = ModuleProviders.CollectModules(context);
        var trigger = ModuleProviders.HasTrigger(context, TriggerAttributeMetadataName);

        // Stage two combines the compilation deliberately: a request type's validatable graph spans files
        // (property types and their attributes live anywhere), so the walk must re-derive on compilation
        // changes. The Elarion.Validation reference probe is a symbol-table lookup on that same compilation
        // (no extra provider needed — this stage is already compilation-bound). Output is value-equatable,
        // so emission stays cached when no model changed.
        var model = handlerCandidates
            .Combine(modules)
            .Combine(context.CompilationProvider)
            .Select(static (source, ct) => BuildModel(source.Left.Left, source.Left.Right, source.Right, ct))
            .WithTrackingName(TrackingNames.Resolvers);

        var combined = model.Combine(trigger).WithTrackingName(TrackingNames.Combined);

        context.RegisterSourceOutput(combined, static (spc, source) =>
        {
            var (model, hasTrigger) = source;
            if (!hasTrigger)
                return;

            foreach (var diagnostic in model.Diagnostics)
                spc.ReportDiagnostic(diagnostic.ToDiagnostic());

            foreach (var module in model.Modules)
                EmitModuleResolver(spc, module);
        });
    }

    private static ValidationModel BuildModel(
        EquatableArray<string> candidates,
        EquatableArray<ModuleScanner.Module> modules,
        Compilation compilation,
        CancellationToken ct)
    {
        if (candidates.IsEmpty)
            return ValidationModel.Empty;

        var hasElarionValidation =
            compilation.GetTypeByMetadataName(ElarionValidationExtensionsMetadataName) is not null;
        var walkContext = new ValidatableTypeWalker.Context(compilation.Assembly);
        var diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();

        // The validatable request roots per module (handler module by longest-prefix namespace match, like
        // every other per-module registration; a handler under no module contributes nothing).
        var moduleRoots = new Dictionary<ModuleScanner.Module, List<ITypeSymbol>>();
        foreach (var metadataName in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (compilation.Assembly.GetTypeByMetadataName(metadataName) is not { } classSymbol)
                continue;

            if (classSymbol.IsAbstract || classSymbol.IsGenericType)
                continue;

            var handlerInterface = HandlerShape.FindHandlerInterface(classSymbol);
            if (handlerInterface is null)
                continue;

            var requestType = handlerInterface.TypeArguments[0];
            if (!ValidatableTypeWalker.IsValidatable(requestType, walkContext))
                continue;

            if (!hasElarionValidation)
            {
                // The attributes would flow into every exported schema surface yet nothing would enforce
                // them — that must be a visible choice, never a silent one (ADR-0027).
                diagnostics.Add(DiagnosticInfo.Create(
                    ValidationNotEnforcedDescriptor,
                    LocationInfo.From(classSymbol),
                    classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                    requestType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
                continue;
            }

            var handlerNamespace = classSymbol.ContainingNamespace?.ToDisplayString() ?? "";
            var module = ModuleScanner.FindBest(handlerNamespace, modules);
            if (module is null)
                continue;

            if (!moduleRoots.TryGetValue(module, out var roots))
            {
                roots = new List<ITypeSymbol>();
                moduleRoots[module] = roots;
            }

            // Requests are deduped per module; two modules sharing a request type each register it (their
            // resolvers are independent and cross-resolver duplicates are harmless — first-match-wins).
            if (!roots.Contains(requestType, SymbolEqualityComparer.Default))
                roots.Add(requestType);
        }

        if (moduleRoots.Count == 0)
            return new ValidationModel(EquatableArray<ModuleResolverModel>.Empty, diagnostics.ToImmutable());

        var moduleModels = ImmutableArray.CreateBuilder<ModuleResolverModel>();
        foreach (var module in modules.OrderBy(static m => m.Name, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (!moduleRoots.TryGetValue(module, out var roots))
                continue;

            var types = ValidatableTypeWalker.BuildModels(roots, walkContext);
            if (types.IsEmpty)
                continue;

            moduleModels.Add(new ModuleResolverModel(module.Name, module.Namespace, module.TypeName, types));
        }

        return new ValidationModel(moduleModels.ToImmutable(), diagnostics.ToImmutable());
    }

    private static void EmitModuleResolver(SourceProductionContext spc, ModuleResolverModel module)
    {
        var resolverName = $"{module.ModuleName}ValidatableInfoResolver";
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// Source: Elarion.Generators.ValidationResolverGenerator");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("#pragma warning disable ASP0029 // The Microsoft.Extensions.Validation extensibility surface is experimental; this generated resolver is its contained consumer (ADR-0027).");
        sb.AppendLine();
        if (module.ModuleNamespace.Length > 0)
        {
            sb.AppendLine($"namespace {module.ModuleNamespace};");
            sb.AppendLine();
        }

        sb.AppendLine("/// <summary>");
        sb.AppendLine($"/// Source-generated validation metadata for the {module.ModuleName} module's handler request types.");
        sb.AppendLine("/// Attribute arrays are cached, constant-constructed compile-time values — no runtime attribute reflection.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine($"internal sealed class {resolverName} : global::Microsoft.Extensions.Validation.IValidatableInfoResolver");
        sb.AppendLine("{");

        AppendTryGetValidatableTypeInfo(sb, module);
        sb.AppendLine();
        AppendTryGetValidatableParameterInfo(sb);
        sb.AppendLine();
        AppendTypeInfoFields(sb, module);
        sb.AppendLine();
        AppendInfoSubclasses(sb);

        sb.AppendLine("}");

        spc.AddSource($"{resolverName}.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));

        var nsPrefix = module.ModuleNamespace.Length > 0 ? $"global::{module.ModuleNamespace}." : "global::";
        ModuleDefaultsEmitter.EmitFiller(
            spc,
            module.ModuleNamespace,
            module.ModuleTypeName,
            ModuleDefaultsEmitter.AddValidatorsMethod,
            "Validators",
            $"global::Elarion.Validation.ElarionValidationServiceCollectionExtensions.AddElarionValidationResolver(services, new {nsPrefix}{resolverName}());");
    }

    private static void AppendTryGetValidatableTypeInfo(StringBuilder sb, ModuleResolverModel module)
    {
        sb.AppendLine("    /// <inheritdoc />");
        sb.AppendLine("    public bool TryGetValidatableTypeInfo(");
        sb.AppendLine("        global::System.Type type,");
        sb.AppendLine($"        [global::System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out {ValidatableInfoFqn}? validatableInfo)");
        sb.AppendLine("    {");
        for (var i = 0; i < module.Types.Length; i++)
        {
            sb.AppendLine($"        if (type == typeof({module.Types[i].TypeFqn}))");
            sb.AppendLine("        {");
            sb.AppendLine($"            validatableInfo = __type{i};");
            sb.AppendLine("            return true;");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        sb.AppendLine("        validatableInfo = null;");
        sb.AppendLine("        return false;");
        sb.AppendLine("    }");
    }

    private static void AppendTryGetValidatableParameterInfo(StringBuilder sb)
    {
        sb.AppendLine("    /// <inheritdoc />");
        sb.AppendLine("    public bool TryGetValidatableParameterInfo(");
        sb.AppendLine("        global::System.Reflection.ParameterInfo parameterInfo,");
        sb.AppendLine($"        [global::System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out {ValidatableInfoFqn}? validatableInfo)");
        sb.AppendLine("    {");
        sb.AppendLine("        // Handler requests are dispatched as whole objects; parameter binding is a transport concern.");
        sb.AppendLine("        validatableInfo = null;");
        sb.AppendLine("        return false;");
        sb.AppendLine("    }");
    }

    private static void AppendTypeInfoFields(StringBuilder sb, ModuleResolverModel module)
    {
        if (NeedsEmptyAttributeArray(module))
        {
            sb.AppendLine($"    private static readonly {ValidationAttributeFqn}[] __noAttributes =");
            sb.AppendLine($"        global::System.Array.Empty<{ValidationAttributeFqn}>();");
            sb.AppendLine();
        }

        for (var i = 0; i < module.Types.Length; i++)
        {
            var type = module.Types[i];
            if (i > 0)
                sb.AppendLine();

            sb.AppendLine($"    private static readonly {ValidatableInfoFqn} __type{i} = new ElarionValidatableTypeInfo(");
            sb.AppendLine($"        typeof({type.TypeFqn}),");
            if (type.Members.IsEmpty)
            {
                sb.AppendLine($"        global::System.Array.Empty<{ValidatablePropertyInfoFqn}>(),");
            }
            else
            {
                sb.AppendLine($"        new {ValidatablePropertyInfoFqn}[]");
                sb.AppendLine("        {");
                foreach (var member in type.Members)
                {
                    sb.AppendLine("            new ElarionValidatablePropertyInfo(");
                    sb.AppendLine($"                typeof({type.TypeFqn}),");
                    sb.AppendLine($"                typeof({member.PropertyTypeFqn}),");
                    sb.AppendLine($"                {SourceLiterals.String(member.Name)},");
                    sb.AppendLine($"                {SourceLiterals.String(member.DisplayName)},");
                    AppendAttributeArray(sb, member.Attributes, indent: "                ", terminator: "),");
                }

                sb.AppendLine("        },");
            }

            AppendAttributeArray(sb, type.TypeAttributes, indent: "        ", terminator: ");");
        }
    }

    private static void AppendAttributeArray(
        StringBuilder sb,
        EquatableArray<string> attributes,
        string indent,
        string terminator)
    {
        if (attributes.IsEmpty)
        {
            sb.AppendLine($"{indent}__noAttributes{terminator}");
            return;
        }

        sb.AppendLine($"{indent}new {ValidationAttributeFqn}[]");
        sb.AppendLine($"{indent}{{");
        foreach (var attribute in attributes)
            sb.AppendLine($"{indent}    {attribute},");
        sb.AppendLine($"{indent}}}{terminator}");
    }

    private static bool NeedsEmptyAttributeArray(ModuleResolverModel module)
    {
        foreach (var type in module.Types)
        {
            if (type.TypeAttributes.IsEmpty)
                return true;

            foreach (var member in type.Members)
            {
                if (member.Attributes.IsEmpty)
                    return true;
            }
        }

        return false;
    }

    private static void AppendInfoSubclasses(StringBuilder sb)
    {
        // The constructor signatures below bind against Microsoft.Extensions.Validation's protected base
        // constructors; the DynamicallyAccessedMembers annotations mirror the base parameters so the trimmer
        // keeps what the runtime walker touches.
        sb.AppendLine("    private sealed class ElarionValidatableTypeInfo : global::Microsoft.Extensions.Validation.ValidatableTypeInfo");
        sb.AppendLine("    {");
        sb.AppendLine($"        private readonly {ValidationAttributeFqn}[] _typeAttributes;");
        sb.AppendLine();
        sb.AppendLine("        public ElarionValidatableTypeInfo(");
        sb.AppendLine("            [param: global::System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(global::System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.Interfaces)]");
        sb.AppendLine("            global::System.Type type,");
        sb.AppendLine($"            {ValidatablePropertyInfoFqn}[] members,");
        sb.AppendLine($"            {ValidationAttributeFqn}[] typeAttributes) : base(type, members)");
        sb.AppendLine("        {");
        sb.AppendLine("            _typeAttributes = typeAttributes;");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine($"        protected override {ValidationAttributeFqn}[] GetValidationAttributes() => _typeAttributes;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    private sealed class ElarionValidatablePropertyInfo : global::Microsoft.Extensions.Validation.ValidatablePropertyInfo");
        sb.AppendLine("    {");
        sb.AppendLine($"        private readonly {ValidationAttributeFqn}[] _validationAttributes;");
        sb.AppendLine();
        sb.AppendLine("        public ElarionValidatablePropertyInfo(");
        sb.AppendLine("            [param: global::System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(global::System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)]");
        sb.AppendLine("            global::System.Type declaringType,");
        sb.AppendLine("            global::System.Type propertyType,");
        sb.AppendLine("            string name,");
        sb.AppendLine("            string displayName,");
        sb.AppendLine($"            {ValidationAttributeFqn}[] validationAttributes) : base(declaringType, propertyType, name, displayName)");
        sb.AppendLine("        {");
        sb.AppendLine("            _validationAttributes = validationAttributes;");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine($"        protected override {ValidationAttributeFqn}[] GetValidationAttributes() => _validationAttributes;");
        sb.AppendLine("    }");
    }
}

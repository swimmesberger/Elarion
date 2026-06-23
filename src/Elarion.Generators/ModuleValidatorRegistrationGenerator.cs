using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Elarion.Generators;

/// <summary>
/// Generates per-module validator registration extension methods.
/// <para>
/// Discovers modules by scanning for classes with <c>[AppModule("Name")]</c> attribute,
/// then groups validators by namespace containment within that module's namespace.
/// Generates <c>Add{ModuleName}Validators(this IServiceCollection)</c>
/// that registers each validator.
/// </para>
/// <para>
/// Trigger: annotate the Application assembly with <c>[assembly: UseElarion]</c>
/// or <c>[assembly: GenerateModuleValidators]</c>.
/// </para>
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ModuleValidatorRegistrationGenerator : IIncrementalGenerator
{
    private const string TriggerAttributeMetadataName =
        "Elarion.Abstractions.GenerateModuleValidatorsAttribute";

    private const string AbstractValidatorMetadataName =
        "FluentValidation.AbstractValidator`1";

    private sealed record ValidatorInfo(string ValidatorFqn, string ValidatedTypeFqn, string Namespace);

    private static class TrackingNames
    {
        public const string Validators = "Validators";
        public const string Combined = "ValidatorsCombined";
    }

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Validators carry no marker attribute, so they are discovered by a predicate-filtered syntax
        // provider (a candidate must have a base list) plus a semantic base-type check in the transform.
        var validators = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax { BaseList: not null },
                static (ctx, ct) => CreateValidator(ctx, ct))
            .Where(static validator => validator is not null)
            .Select(static (validator, _) => validator!)
            .Collect()
            .WithTrackingName(TrackingNames.Validators);

        var modules = ModuleProviders.CollectModules(context);
        var trigger = ModuleProviders.HasTrigger(context, TriggerAttributeMetadataName);

        var combined = validators.Combine(modules).Combine(trigger).WithTrackingName(TrackingNames.Combined);

        context.RegisterSourceOutput(combined, static (spc, source) =>
        {
            var ((validatorList, modules), hasTrigger) = source;
            if (!hasTrigger)
            {
                return;
            }

            Emit(spc, validatorList, modules);
        });
    }

    private static ValidatorInfo? CreateValidator(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.Node is not ClassDeclarationSyntax classDecl ||
            ctx.SemanticModel.GetDeclaredSymbol(classDecl, ct) is not INamedTypeSymbol typeSymbol ||
            typeSymbol.IsAbstract)
        {
            return null;
        }

        // Compare by symbol identity against the resolved metadata symbol — not a display-string match,
        // which would also accept any unrelated *AbstractValidator<T> type and is culture/format fragile.
        var abstractValidatorSymbol = ctx.SemanticModel.Compilation.GetTypeByMetadataName(AbstractValidatorMetadataName);
        if (abstractValidatorSymbol is null)
        {
            return null;
        }

        for (var baseType = typeSymbol.BaseType; baseType is not null; baseType = baseType.BaseType)
        {
            if (!SymbolEqualityComparer.Default.Equals(baseType.OriginalDefinition, abstractValidatorSymbol))
            {
                continue;
            }

            var validatedType = baseType.TypeArguments.FirstOrDefault();
            if (validatedType is null)
            {
                return null;
            }

            var validatorNs = typeSymbol.ContainingNamespace?.ToDisplayString() ?? "";
            return new ValidatorInfo(typeSymbol.ToDisplayString(), validatedType.ToDisplayString(), validatorNs);
        }

        return null;
    }

    private static void Emit(
        SourceProductionContext spc,
        ImmutableArray<ValidatorInfo> validators,
        EquatableArray<ModuleScanner.Module> modules)
    {
        // Assign each validator to its owning module by longest-prefix namespace match.
        var moduleValidators = modules.ToDictionary(
            module => module,
            _ => new List<(string ValidatorFqn, string ValidatedTypeFqn)>());

        foreach (var validator in validators)
        {
            ModuleScanner.Module? bestMatch = null;
            foreach (var module in modules)
                if (ModuleScanner.IsInScope(validator.Namespace, module.Namespace) &&
                    (bestMatch is null || module.Namespace.Length > bestMatch.Namespace.Length))
                    bestMatch = module;

            if (bestMatch is not null)
                moduleValidators[bestMatch].Add((validator.ValidatorFqn, validator.ValidatedTypeFqn));
        }

        // Generate extension methods for each module that has validators
        foreach (var kvp in moduleValidators
                     .Where(x => x.Value.Count > 0)
                     .OrderBy(x => x.Key.Name, StringComparer.Ordinal))
        {
            var module = kvp.Key;
            var moduleName = module.Name;
            var validatorsForModule = kvp.Value;

            var sb = new StringBuilder();
            sb.AppendLine("using FluentValidation;");
            sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
            sb.AppendLine();
            sb.AppendLine($"namespace {module.Namespace};");
            sb.AppendLine();
            sb.AppendLine($"/// <summary>");
            sb.AppendLine($"/// Extension methods for registering {moduleName} module validators.");
            sb.AppendLine($"/// </summary>");
            sb.AppendLine($"public static class {moduleName}ValidatorExtensions");
            sb.AppendLine("{");
            sb.AppendLine($"    /// <summary>");
            sb.AppendLine($"    /// Registers all validators for the {moduleName} module.");
            sb.AppendLine($"    /// </summary>");
            sb.AppendLine($"    public static IServiceCollection Add{moduleName}Validators(");
            sb.AppendLine($"        this IServiceCollection services)");
            sb.AppendLine("    {");

            foreach (var (validatorFqn, validatedTypeFqn) in validatorsForModule.OrderBy(x => x.ValidatorFqn))
                sb.AppendLine(
                    $"        services.AddScoped<IValidator<global::{validatedTypeFqn}>, global::{validatorFqn}>();");

            sb.AppendLine("        return services;");
            sb.AppendLine("    }");
            sb.AppendLine("}");

            spc.AddSource($"{moduleName}ValidatorExtensions.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));

            var nsPrefix = module.Namespace.Length > 0 ? $"global::{module.Namespace}." : "global::";
            ModuleDefaultsEmitter.EmitFiller(
                spc,
                module.Namespace,
                module.TypeName,
                ModuleDefaultsEmitter.AddValidatorsMethod,
                "Validators",
                $"{nsPrefix}{moduleName}ValidatorExtensions.Add{moduleName}Validators(services);");
        }
    }
}

using System.Text;
using Microsoft.CodeAnalysis;
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

    private const string AppModuleAttributeMetadataName =
        "Elarion.Abstractions.Modules.AppModuleAttribute";

    private sealed record ModuleInfo(string Name, string Namespace);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Generate the registration methods directly from compilation
        context.RegisterSourceOutput(context.CompilationProvider, static (spc, compilation) =>
        {
            // Check if assembly has the trigger attribute
            var hasTrigger = FrameworkFeatureTriggers.HasAssemblyTrigger(compilation, TriggerAttributeMetadataName);
            if (!hasTrigger) return;

            var abstractValidatorSymbol = compilation.GetTypeByMetadataName(AbstractValidatorMetadataName);
            if (abstractValidatorSymbol is null) return;

            var moduleAttrSymbol = compilation.GetTypeByMetadataName(AppModuleAttributeMetadataName);
            if (moduleAttrSymbol is null) return;

            // Step 1: Find all module definitions (classes with [AppModule("Name")])
            var modules = new List<ModuleInfo>();
            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = syntaxTree.GetRoot();

                foreach (var typeDecl in root.DescendantNodes()
                             .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax>())
                {
                    var typeSymbol = semanticModel.GetDeclaredSymbol(typeDecl);
                    if (typeSymbol is null) continue;

                    var moduleAttr = typeSymbol.GetAttributes()
                        .FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, moduleAttrSymbol));

                    if (moduleAttr is null) continue;

                    // Get module name from attribute constructor argument
                    if (moduleAttr.ConstructorArguments.Length > 0 &&
                        moduleAttr.ConstructorArguments[0].Value is string moduleName)
                    {
                        var ns = typeSymbol.ContainingNamespace?.ToDisplayString() ?? "";
                        modules.Add(new ModuleInfo(moduleName, ns));
                    }
                }
            }

            // Step 2: Collect all validator types and assign to modules by namespace containment
            var moduleValidators = modules.ToDictionary(m => m,
                _ => new List<(string ValidatorFqn, string ValidatedTypeFqn)>());

            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = syntaxTree.GetRoot();

                foreach (var classDecl in root.DescendantNodes()
                             .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>())
                {
                    var typeSymbol = semanticModel.GetDeclaredSymbol(classDecl) as INamedTypeSymbol;
                    if (typeSymbol is null || typeSymbol.IsAbstract) continue;

                    // Check if type inherits from AbstractValidator<T>
                    var baseType = typeSymbol.BaseType;
                    while (baseType is not null)
                    {
                        var baseName = baseType.OriginalDefinition.ToDisplayString();
                        // FluentValidation.AbstractValidator<T> can also appear as AbstractValidator<T>
                        if (baseName == "FluentValidation.AbstractValidator<T>" ||
                            baseName.EndsWith("AbstractValidator<T>"))
                        {
                            // Found a validator
                            var validatedType = baseType.TypeArguments.FirstOrDefault();
                            if (validatedType is null) break;

                            var validatorNs = typeSymbol.ContainingNamespace?.ToDisplayString() ?? "";

                            // Find which module this validator belongs to (longest matching namespace prefix)
                            ModuleInfo? bestMatch = null;
                            foreach (var module in modules)
                                if (validatorNs.StartsWith(module.Namespace) &&
                                    (bestMatch is null || module.Namespace.Length > bestMatch.Namespace.Length))
                                    bestMatch = module;

                            if (bestMatch is not null)
                                moduleValidators[bestMatch].Add((typeSymbol.ToDisplayString(),
                                    validatedType.ToDisplayString()));
                            break;
                        }

                        baseType = baseType.BaseType;
                    }
                }
            }

            // Generate extension methods for each module that has validators
            foreach (var kvp in moduleValidators
                         .Where(x => x.Value.Count > 0)
                         .OrderBy(x => x.Key.Name, StringComparer.Ordinal))
            {
                var module = kvp.Key;
                var moduleName = module.Name;
                var validators = kvp.Value;

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

                foreach (var (validatorFqn, validatedTypeFqn) in validators.OrderBy(x => x.ValidatorFqn))
                    sb.AppendLine(
                        $"        services.AddScoped<IValidator<global::{validatedTypeFqn}>, global::{validatorFqn}>();");

                sb.AppendLine("        return services;");
                sb.AppendLine("    }");
                sb.AppendLine("}");

                spc.AddSource($"{moduleName}ValidatorExtensions.g.cs", SourceText.From(sb.ToString(), Encoding.UTF8));
            }
        });
    }
}

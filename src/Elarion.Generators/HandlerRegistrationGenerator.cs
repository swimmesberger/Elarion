using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Elarion.Generators;

/// <summary>
/// Generates per-handler <c>AddHandler</c> extension methods that register handlers
/// in DI wrapped in their configured decorator and cache pipeline.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed partial class HandlerRegistrationGenerator : IIncrementalGenerator {
    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context) {
        // Discovery is split into two stages (ADR-0006). Stage one identifies handler CANDIDATES per node —
        // concrete IHandler<,> implementations — and carries only their metadata names, so it is cached per
        // syntax tree and an edit re-binds only the edited file (never the whole compilation, the old
        // O(all-classes) per-keystroke cost). Stage two re-resolves each candidate from the CURRENT compilation
        // via IAssemblySymbol.GetTypeByMetadataName (symbol-table lookups, no tree binding) and parses the full
        // pipeline there. Stage two deliberately combines the compilation: a handler's effective pipeline
        // depends on cross-file state — the [DecoratorList] on a module/assembly/pipeline-attribute class and
        // [Require*]/[FeatureGate] on base classes — which must stay fresh rather than cached against a stale
        // tree. Its cost is bounded by the number of actual handlers, and its value-equatable output keeps
        // emission cached when no handler model changed.
        // The set of variant contracts ([FeatureVariant<TContract>] targets) in the compilation. A handler whose
        // constructor depends on one is registered behind the async-resolving proxy, so the contract's variant can
        // be awaited per user. Collected via the attribute syntax provider so it stays incremental.
        var variantContracts = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                FeatureVariantAttributeMetadataName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, _) => GetVariantContractFqns(ctx))
            .Collect()
            .Select(static (groups, _) => FlattenSortedDistinct(groups))
            .WithTrackingName("VariantContracts");

        var handlerCandidates = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList: not null },
                transform: static (ctx, ct) => HandlerCandidates.Identify(ctx, ct))
            .Where(static candidate => candidate is not null)
            .WithTrackingName("HandlerCandidateNodes")
            .Collect()
            .Select(static (candidates, _) => HandlerCandidates.FlattenSortedDistinct(candidates))
            .WithTrackingName("HandlerCandidates");

        var modules = ModuleProviders.CollectModules(context);

        var handlerProvider = handlerCandidates
            .Combine(modules)
            .Combine(variantContracts)
            .Combine(context.CompilationProvider)
            .Select(static (source, ct) =>
                ResolveHandlers(source.Left.Left.Left, source.Left.Left.Right, source.Left.Right, source.Right, ct))
            .WithTrackingName("Handlers");

        context.RegisterSourceOutput(handlerProvider, static (spc, handlers) => {
            foreach (var handler in handlers)
                EmitHandlerRegistration(spc, handler);
        });

        var moduleAggregationProvider = handlerProvider
            .Combine(modules)
            .Combine(ModuleProviders.HasTrigger(context, TriggerAttributeMetadataName))
            .WithTrackingName("HandlerModuleAggregation");

        context.RegisterSourceOutput(
            moduleAggregationProvider,
            static (spc, source) => {
                var ((handlers, modules), hasTrigger) = source;
                if (!hasTrigger)
                    return;

                EmitModuleAggregations(spc, handlers, modules);
            });
    }
}

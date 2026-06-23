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
        // A handler's generated pipeline depends on cross-file state: the effective [DecoratorList] can come
        // from the handler, its [AppModule], or the assembly. So resolution must be re-derived whenever the
        // compilation changes (correctness) — hence the Combine(CompilationProvider). Incrementality comes from
        // two places instead: the module [DecoratorList] map is built ONCE per pass (not rescanned per handler,
        // the old O(handlers x all-trees) cost), and the result is a value-equatable array so an edit that does
        // not change any handler model does not re-emit.
        var handlerProvider = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList: not null },
                transform: static (ctx, _) => (ClassDeclarationSyntax)ctx.Node)
            .Collect()
            .Combine(context.CompilationProvider)
            .Select(static (source, ct) => ResolveHandlers(source.Left, source.Right, ct))
            .WithTrackingName("Handlers");

        context.RegisterSourceOutput(handlerProvider, static (spc, handlers) => {
            foreach (var handler in handlers)
                EmitHandlerRegistration(spc, handler);
        });

        var moduleAggregationProvider = handlerProvider
            .Combine(ModuleProviders.CollectModules(context))
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

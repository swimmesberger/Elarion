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
        var handlerDeclarations = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => node is ClassDeclarationSyntax { BaseList: not null },
                transform: static (ctx, _) => (ClassDeclarationSyntax)ctx.Node);

        var handlerProvider = handlerDeclarations
            .Combine(context.CompilationProvider)
            .Select(static (source, ct) => GetHandlerInfo(source.Left, source.Right, ct))
            .Where(static handler => handler is not null)
            .Select(static (handler, _) => handler!);

        context.RegisterSourceOutput(handlerProvider, EmitHandlerRegistration);

        var moduleAggregationProvider = handlerProvider
            .Collect()
            .Combine(context.CompilationProvider);

        context.RegisterSourceOutput(
            moduleAggregationProvider,
            static (spc, source) => EmitModuleAggregations(spc, source.Left, source.Right));
    }
}

using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Elarion.Generators.CodeFixes;

/// <summary>
/// Code fix for ELID001: rewrites a <c>Guid.NewGuid()</c> invocation to <c>Guid.CreateVersion7()</c>,
/// preserving however the receiver was written (<c>Guid.NewGuid()</c>, <c>System.Guid.NewGuid()</c>, an
/// alias, or a bare <c>NewGuid()</c> under <c>using static System.Guid</c>).
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(PreferGuidCreateVersion7CodeFixProvider))]
[Shared]
public sealed class PreferGuidCreateVersion7CodeFixProvider : CodeFixProvider
{
    // Duplicated from PreferGuidCreateVersion7Analyzer: the analyzer assembly is a compiler component and
    // this Workspaces-layer sibling must not force it onto the IDE's load path just for one constant.
    private const string DiagnosticId = "ELID001";

    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds { get; } = ImmutableArray.Create(DiagnosticId);

    /// <inheritdoc />
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    /// <inheritdoc />
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return;
        }

        foreach (var diagnostic in context.Diagnostics)
        {
            var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
            if (node is not InvocationExpressionSyntax invocation)
            {
                continue;
            }

            // The method name is the only token that changes; the receiver spelling is preserved.
            SimpleNameSyntax? methodName = invocation.Expression switch
            {
                MemberAccessExpressionSyntax memberAccess => memberAccess.Name,
                IdentifierNameSyntax identifier => identifier,
                _ => null,
            };
            if (methodName is null)
            {
                continue;
            }

            context.RegisterCodeFix(
                CodeAction.Create(
                    "Use Guid.CreateVersion7()",
                    ct => RewriteAsync(context.Document, methodName, ct),
                    equivalenceKey: "UseGuidCreateVersion7"),
                diagnostic);
        }
    }

    private static async Task<Document> RewriteAsync(
        Document document, SimpleNameSyntax methodName, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return document;
        }

        var replacement = SyntaxFactory.IdentifierName("CreateVersion7").WithTriviaFrom(methodName);
        return document.WithSyntaxRoot(root.ReplaceNode(methodName, replacement));
    }
}

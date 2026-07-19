using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Elarion.Generators;

/// <summary>
/// Reports <c>ConfigureAwait(false)</c> written inside a <c>[Reentrant]</c> <c>[Actor]</c> class
/// (ELACT006).
/// </summary>
/// <remarks>
/// A reentrant actor's single-threaded guarantee comes from every turn resuming on the activation's
/// exclusive scheduler. Context capture is per-method, so a library the actor calls may freely use
/// <c>ConfigureAwait(false)</c> internally — the actor method still resumes on its scheduler. But an
/// await configured with <c>false</c> (or a <c>ConfigureAwaitOptions</c> value without
/// <c>ContinueOnCapturedContext</c>) <em>in the actor's own code</em> moves the rest of that method
/// off the scheduler, silently forfeiting the guarantee — and only when the awaited task actually
/// suspends, which makes the escape latent and timing-dependent. Hence an analyzer, not a doc note
/// (ADR-0042). The rule fires for any code whose containing type (including nested types, lambdas,
/// and local functions) carries both attributes; non-reentrant actors are exempt because their
/// sequential guarantee comes from the mailbox loop, not a scheduler.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ReentrantActorConfigureAwaitAnalyzer : DiagnosticAnalyzer {
    private const string ActorAttributeMetadataName = "Elarion.Actors.ActorAttribute";
    private const string ReentrantAttributeMetadataName = "Elarion.Actors.ReentrantAttribute";
    private const string TasksNamespace = "System.Threading.Tasks";

    // ConfigureAwaitOptions.ContinueOnCapturedContext == 1; any constant without that bit disables capture.
    private const int ContinueOnCapturedContextFlag = 1;

    private static readonly DiagnosticDescriptor ConfigureAwaitFalseInReentrantActor = new(
        "ELACT006",
        "ConfigureAwait(false) inside a [Reentrant] actor",
        "ConfigureAwait(false) inside [Reentrant] actor '{0}' escapes the exclusive scheduler: the rest of "
        + "the method resumes off the actor's single-threaded context, forfeiting the interleaving guarantee "
        + "(and only when the await actually suspends, so the escape is timing-dependent). Remove it from "
        + "actor-owned code — libraries the actor calls may use ConfigureAwait(false) internally without harm.",
        "Elarion.Generators",
        DiagnosticSeverity.Warning,
        true);

    private static readonly ImmutableArray<DiagnosticDescriptor> SupportedDiagnosticsArray =
        ImmutableArray.Create(ConfigureAwaitFalseInReentrantActor);

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => SupportedDiagnosticsArray;

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context) {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(static startContext => {
            var actorAttribute = startContext.Compilation.GetTypeByMetadataName(ActorAttributeMetadataName);
            var reentrantAttribute = startContext.Compilation.GetTypeByMetadataName(ReentrantAttributeMetadataName);
            if (actorAttribute is null || reentrantAttribute is null) return;

            startContext.RegisterOperationAction(
                operationContext => AnalyzeInvocation(operationContext, actorAttribute, reentrantAttribute),
                OperationKind.Invocation);
        });
    }

    private static void AnalyzeInvocation(
        OperationAnalysisContext context,
        INamedTypeSymbol actorAttribute,
        INamedTypeSymbol reentrantAttribute) {
        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod;
        if (method.Name != "ConfigureAwait" ||
            invocation.Arguments.Length != 1 ||
            method.ContainingType?.ContainingNamespace?.ToDisplayString() != TasksNamespace)
            return;

        if (!DisablesContextCapture(invocation.Arguments[0].Value)) return;

        var actorType = FindContainingReentrantActor(context.ContainingSymbol, actorAttribute, reentrantAttribute);
        if (actorType is null) return;

        context.ReportDiagnostic(Diagnostic.Create(
            ConfigureAwaitFalseInReentrantActor,
            invocation.Syntax.GetLocation(),
            actorType.ToDisplayString()));
    }

    private static bool DisablesContextCapture(IOperation argument) {
        if (!argument.ConstantValue.HasValue)
            // Non-constant argument: unknowable, stay quiet.
            return false;

        return argument.ConstantValue.Value switch {
            bool continueOnCapturedContext => !continueOnCapturedContext,
            int options => (options & ContinueOnCapturedContextFlag) == 0, // ConfigureAwaitOptions constant
            _ => false
        };
    }

    private static INamedTypeSymbol? FindContainingReentrantActor(
        ISymbol containingSymbol,
        INamedTypeSymbol actorAttribute,
        INamedTypeSymbol reentrantAttribute) {
        // Walk the nesting chain so lambdas, local functions, and nested types (e.g. hand-written
        // helpers inside the actor class) are covered.
        for (var type = containingSymbol as INamedTypeSymbol ?? containingSymbol.ContainingType;
             type is not null;
             type = type.ContainingType)
            if (HasAttribute(type, actorAttribute) && HasAttribute(type, reentrantAttribute))
                return type;

        return null;
    }

    private static bool HasAttribute(INamedTypeSymbol type, INamedTypeSymbol attribute) {
        foreach (var candidate in type.GetAttributes())
            if (SymbolEqualityComparer.Default.Equals(candidate.AttributeClass, attribute))
                return true;

        return false;
    }
}

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Elarion.Generators;

/// <summary>
/// Reports every call to <c>System.Guid.NewGuid()</c> and points at <c>Guid.CreateVersion7()</c>.
/// </summary>
/// <remarks>
/// <para>
/// Elarion's identity doctrine (ADR-0038) is that the application mints entity identity in code with a
/// time-ordered UUIDv7: a v7's timestamp prefix keeps primary-key b-tree inserts append-mostly, where random
/// v4 ids scatter across the whole index. The convention applies to never-persisted identifiers too, so the
/// codebase reads one way.
/// </para>
/// <para>
/// Flagging <em>every</em> <c>NewGuid()</c> is deliberate, not a heuristic gap: deciding "is this a key?"
/// without the EF model is brittle, and v7 is the better default nearly everywhere. The residue is the site
/// where v4 is genuinely <em>preferred</em> — an id that must be unpredictable, because a v7 leaks its creation
/// instant and carries only 74 random bits. That site keeps v4 and suppresses this rule with a justification,
/// which is the point: the exception becomes visible and argued instead of invisible and accidental.
/// </para>
/// <para>
/// Severity is Warning: it enforces under the common <c>TreatWarningsAsErrors</c> posture while staying an
/// advisory nudge elsewhere. There is deliberately no code fix — the replacement is a one-token edit named in
/// the message, and a code fix would require a <c>Microsoft.CodeAnalysis.Workspaces</c> reference that RS1038
/// forbids in a compiler-loaded analyzer assembly.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class GuidVersionAnalyzer : DiagnosticAnalyzer {
    private const string GuidMetadataName = "System.Guid";
    private const string NewGuidMethodName = "NewGuid";

    private static readonly DiagnosticDescriptor PreferVersion7 = new(
        "ELID001",
        "Prefer Guid.CreateVersion7() over Guid.NewGuid()",
        "Use Guid.CreateVersion7() instead of Guid.NewGuid(); v7 ids are time-ordered and index-friendly. If "
        + "this id must be unpredictable (a token or capability code), keep v4 and suppress ELID001 with a "
        + "justification.",
        "Elarion.Identity",
        DiagnosticSeverity.Warning,
        true,
        "Elarion applications own entity identity and mint it in code (ADR-0038). A UUIDv7's time-ordered "
        + "prefix keeps primary-key b-tree inserts append-mostly, while random v4 ids scatter across the whole "
        + "index; the convention covers never-persisted identifiers too so the codebase reads one way. Every "
        + "Guid.NewGuid() is flagged deliberately — deciding whether a given id becomes a key is not decidable "
        + "here — so the genuine v4 site (an id that must be unpredictable, since a v7 leaks its creation "
        + "instant) is suppressed explicitly with a justification rather than left indistinguishable from an "
        + "accidental one.");

    private static readonly ImmutableArray<DiagnosticDescriptor> SupportedDiagnosticsArray =
        ImmutableArray.Create(PreferVersion7);

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => SupportedDiagnosticsArray;

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context) {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(start => {
            // Resolved once per compilation and matched by symbol, so a user-defined Guid.NewGuid() on some
            // other type — or a local named NewGuid — is never flagged.
            var guid = start.Compilation.GetTypeByMetadataName(GuidMetadataName);
            if (guid is null)
                return;

            start.RegisterOperationAction(operation => Analyze(operation, guid), OperationKind.Invocation);
        });
    }

    private static void Analyze(OperationAnalysisContext context, INamedTypeSymbol guid) {
        if (context.Operation is not IInvocationOperation { TargetMethod: { IsStatic: true } method } invocation)
            return;

        if (method.Name != NewGuidMethodName || method.Parameters.Length != 0)
            return;

        if (!SymbolEqualityComparer.Default.Equals(method.ContainingType, guid))
            return;

        context.ReportDiagnostic(Diagnostic.Create(PreferVersion7, invocation.Syntax.GetLocation()));
    }
}

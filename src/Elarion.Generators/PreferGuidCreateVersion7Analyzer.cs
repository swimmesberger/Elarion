using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Elarion.Generators;

/// <summary>
/// Flags every <c>Guid.NewGuid()</c> invocation and suggests <c>Guid.CreateVersion7()</c> (ELID001).
/// </summary>
/// <remarks>
/// <para>
/// Elarion's identity doctrine: the application owns entity identity and mints ids in code; the recommended
/// value is a UUIDv7 (<c>Guid.CreateVersion7()</c>), whose time-ordered prefix keeps primary-key b-tree
/// inserts append-mostly instead of scattering like random v4 ids. Flagging <em>every</em>
/// <c>Guid.NewGuid()</c> — not just "entity id" positions — is deliberate: detecting "is this a key?"
/// without the EF model is heuristic and brittle, and v7 is the better default for nearly every use anyway.
/// </para>
/// <para>
/// The default severity is Warning — "noting", not banning: under <c>TreatWarningsAsErrors</c> it enforces,
/// apps that want it advisory dial it down in <c>.editorconfig</c>, and the rare legitimate v4 use (an id
/// that must be unpredictable — v7 embeds its creation timestamp) becomes a visible,
/// suppressed-with-justification site. The rule stays silent on target frameworks without
/// <c>Guid.CreateVersion7()</c> (&lt; .NET 9), where there is nothing better to suggest.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferGuidCreateVersion7Analyzer : DiagnosticAnalyzer
{
    private const string GuidMetadataName = "System.Guid";
    private const string NewGuidName = "NewGuid";
    private const string CreateVersion7Name = "CreateVersion7";

    private static readonly DiagnosticDescriptor PreferCreateVersion7 = new(
        "ELID001",
        "Prefer Guid.CreateVersion7() over Guid.NewGuid()",
        "Guid.NewGuid() creates a random (v4) Guid: prefer Guid.CreateVersion7() for entity keys (time-ordered, index-friendly)",
        "Elarion.Identifiers",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description:
        "The application owns entity identity and mints ids in code. Prefer Guid.CreateVersion7(): its " +
        "time-ordered prefix keeps primary-key b-tree inserts append-mostly, where random v4 ids scatter. " +
        "Two conscious trade-offs to know: a v7 id embeds its creation instant (visible wherever the id is, " +
        "e.g. in URLs), and ids created within the same millisecond are mutually unordered — never use the " +
        "id as a business sort key (use a CreatedAt column). Where an id must be unpredictable (a " +
        "capability-style token), Guid.NewGuid() is the right call — suppress this diagnostic there with a " +
        "justification.");

    private static readonly ImmutableArray<DiagnosticDescriptor> SupportedDiagnosticsArray =
        ImmutableArray.Create(PreferCreateVersion7);

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => SupportedDiagnosticsArray;

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(start =>
        {
            var guid = start.Compilation.GetTypeByMetadataName(GuidMetadataName);
            if (guid is null)
                return;

            // No CreateVersion7 on this target framework — there is nothing better to suggest.
            var hasCreateVersion7 = guid.GetMembers(CreateVersion7Name)
                .OfType<IMethodSymbol>()
                .Any(method => method.IsStatic && method.Parameters.IsEmpty);
            if (!hasCreateVersion7)
                return;

            start.RegisterOperationAction(operationContext =>
            {
                var invocation = (IInvocationOperation)operationContext.Operation;
                var method = invocation.TargetMethod;
                if (method is { Name: NewGuidName, IsStatic: true, Parameters.IsEmpty: true } &&
                    SymbolEqualityComparer.Default.Equals(method.ContainingType, guid))
                {
                    operationContext.ReportDiagnostic(
                        Diagnostic.Create(PreferCreateVersion7, invocation.Syntax.GetLocation()));
                }
            }, OperationKind.Invocation);
        });
    }
}

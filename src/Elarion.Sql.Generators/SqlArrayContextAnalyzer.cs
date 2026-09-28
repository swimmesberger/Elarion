using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Elarion.Sql.Generators;

/// <summary>
/// Reports a collection interpolated into a SQL statement where the SQL expects a single <b>array value</b>
/// (<c>ELSQL012</c>): directly inside <c>ANY(</c>/<c>ALL(</c>/<c>SOME(</c>, or as an operand of the array
/// operators <c>@&gt;</c>, <c>&lt;@</c>, and <c>&amp;&amp;</c>.
/// </summary>
/// <remarks>
/// <para>
/// The SQL interpolation handler expands every collection hole (other than <c>string</c> and <c>byte[]</c>) into
/// a parenthesized parameter list for <c>IN</c>. In an array position that renders <c>= ANY((@p0, @p1))</c>, which
/// compiles, passes statement-rendering tests, and is rejected by the database only at run time. The fix is one
/// of two explicit spellings, both named in the message: <c>IN {collection}</c> for a list, or
/// <c>SqlArray.Of(collection)</c> to bind one array parameter.
/// </para>
/// <para>
/// Detection reads the literal SQL immediately around the hole, within the same interpolated string (including
/// <c>$"…" + $"…"</c> concatenations). It is a text heuristic, so it deliberately looks only at those adjacent
/// literals: SQL assembled from separate fragments, a collection typed as <see cref="object"/>, or other
/// array-valued positions (<c>unnest(…)</c>, <c>ARRAY[…]</c>, function arguments) are not inspected. Severity
/// is Warning: it enforces under <c>TreatWarningsAsErrors</c>, and a rare false positive (for example array
/// syntax inside a SQL string literal) can be suppressed with a justification.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SqlArrayContextAnalyzer : DiagnosticAnalyzer {
    private const string HandlerMetadataName = "Elarion.Sql.SqlInterpolatedStringHandler";

    // Operators whose operands are arrays in PostgreSQL (contains, is-contained-by, overlaps).
    private static readonly string[] ArrayOperators = ["@>", "<@", "&&"];

    // Quantified comparisons whose parenthesized operand is an array value (SOME is the synonym of ANY).
    private static readonly string[] ArrayQuantifiers = ["ANY", "ALL", "SOME"];

    private static readonly DiagnosticDescriptor CollectionInArrayContext = new(
        "ELSQL012",
        "Collection interpolated where SQL expects an array value",
        "Collection '{0}' expands to an IN list '(@p0, @p1, …)', but '{1}' expects a single array value; write "
        + "'IN {{collection}}' for a list, or 'SqlArray.Of({0})' to bind one array parameter",
        "Elarion.Sql",
        DiagnosticSeverity.Warning,
        true,
        "An interpolated collection always expands to a parenthesized parameter list for IN. In an array "
        + "position (= ANY(…), <> ALL(…), @>, <@, &&) that renders a row value instead of an array, which the "
        + "database rejects only at run time. Use IN {collection} for a list, or wrap the collection in "
        + "SqlArray.Of(…) to bind it as one array parameter.");

    private static readonly ImmutableArray<DiagnosticDescriptor> SupportedDiagnosticsArray =
        ImmutableArray.Create(CollectionInArrayContext);

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => SupportedDiagnosticsArray;

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context) {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(start => {
            // Matched by symbol, so a look-alike handler in another namespace is never inspected; a compilation
            // that does not reference Elarion.Sql registers nothing.
            var handler = start.Compilation.GetTypeByMetadataName(HandlerMetadataName);
            if (handler is null)
                return;

            start.RegisterOperationAction(
                operation => Analyze(operation, handler),
                OperationKind.InterpolatedStringHandlerCreation);
        });
    }

    private static void Analyze(OperationAnalysisContext context, INamedTypeSymbol handler) {
        var creation = (IInterpolatedStringHandlerCreationOperation)context.Operation;
        if (!SymbolEqualityComparer.Default.Equals(creation.Type, handler))
            return;

        var parts = new List<Part>();
        Flatten(creation.Content, parts);

        for (var i = 0; i < parts.Count; i++) {
            var hole = parts[i].Hole;
            if (hole?.Type is not { } type || !IsExpandedCollection(type))
                continue;

            var before = i > 0 ? parts[i - 1].Literal : null;
            var after = i + 1 < parts.Count ? parts[i + 1].Literal : null;
            var arrayContext = FindArrayContext(before, after);
            if (arrayContext is null)
                continue;

            context.ReportDiagnostic(Diagnostic.Create(
                CollectionInArrayContext, hole.Syntax.GetLocation(), hole.Syntax.ToString(), arrayContext));
        }
    }

    // One piece of the interpolated string in source order: literal text, or a hole's value.
    private readonly struct Part(string? literal, IOperation? hole) {
        public string? Literal { get; } = literal;
        public IOperation? Hole { get; } = hole;
    }

    private static void Flatten(IOperation content, List<Part> parts) {
        switch (content) {
            case IInterpolatedStringAdditionOperation addition:
                Flatten(addition.Left, parts);
                Flatten(addition.Right, parts);
                break;
            case IInterpolatedStringOperation interpolated:
                foreach (var part in interpolated.Parts)
                    parts.Add(ToPart(part));
                break;
        }
    }

    private static Part ToPart(IInterpolatedStringContentOperation part) {
        switch (part) {
            // The handler-lowered shape: each part is an AppendLiteral/AppendFormatted call.
            case IInterpolatedStringAppendOperation { AppendCall: IInvocationOperation { Arguments.Length: > 0 } call }:
                var argument = call.Arguments[0].Value;
                return part.Kind == OperationKind.InterpolatedStringAppendLiteral
                    ? new Part(argument.ConstantValue.Value as string, null)
                    : new Part(null, Unwrap(argument));
            case IInterpolatedStringTextOperation text:
                return new Part(text.Text.ConstantValue.Value as string, null);
            case IInterpolationOperation interpolation:
                return new Part(null, Unwrap(interpolation.Expression));
            default:
                return new Part(null, null);
        }
    }

    // A hole bound through a non-generic overload may carry an implicit conversion; the collection is its operand.
    private static IOperation Unwrap(IOperation value) {
        while (value is IConversionOperation { IsImplicit: true } conversion)
            value = conversion.Operand;
        return value;
    }

    // Mirrors the handler's runtime rule: IEnumerable expands, except string and byte[] (always scalars).
    private static bool IsExpandedCollection(ITypeSymbol type) {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
            type = nullable.TypeArguments[0];

        if (type.SpecialType == SpecialType.System_String)
            return false;
        if (type is IArrayTypeSymbol { Rank: 1, ElementType.SpecialType: SpecialType.System_Byte })
            return false;
        if (type.SpecialType == SpecialType.System_Collections_IEnumerable)
            return true;

        foreach (var implemented in type.AllInterfaces)
            if (implemented.SpecialType == SpecialType.System_Collections_IEnumerable)
                return true;

        return false;
    }

    // Returns the SQL construct that expects an array value around the hole, or null when there is none.
    private static string? FindArrayContext(string? before, string? after) {
        if (before is not null) {
            var end = before.Length;
            while (end > 0 && char.IsWhiteSpace(before[end - 1]))
                end--;

            foreach (var op in ArrayOperators)
                if (EndsWithAt(before, end, op))
                    return op;

            if (end > 0 && before[end - 1] == '(') {
                var keywordEnd = end - 1;
                while (keywordEnd > 0 && char.IsWhiteSpace(before[keywordEnd - 1]))
                    keywordEnd--;

                foreach (var quantifier in ArrayQuantifiers)
                    if (EndsWithAt(before, keywordEnd, quantifier) && IsWordStart(before, keywordEnd - quantifier.Length))
                        return quantifier + "(";
            }
        }

        if (after is not null) {
            var start = 0;
            while (start < after.Length && char.IsWhiteSpace(after[start]))
                start++;

            foreach (var op in ArrayOperators)
                if (string.CompareOrdinal(after, start, op, 0, op.Length) == 0)
                    return op;
        }

        return null;
    }

    private static bool EndsWithAt(string text, int end, string value) {
        var start = end - value.Length;
        return start >= 0 && string.Compare(text, start, value, 0, value.Length, StringComparison.OrdinalIgnoreCase) == 0;
    }

    // The keyword must not be the tail of a longer identifier (COMPANY(, my_any().
    private static bool IsWordStart(string text, int index) {
        if (index == 0)
            return true;

        var previous = text[index - 1];
        return !char.IsLetterOrDigit(previous) && previous != '_' && previous != '.' && previous != '"';
    }
}

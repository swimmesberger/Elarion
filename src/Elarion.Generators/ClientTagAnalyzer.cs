using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Elarion.Generators;

/// <summary>
/// Checks the <c>[ClientTag]</c> values a client interprets: ELRPC013 when a <c>[ClientTag("timeout", …)]</c> value is
/// not one the generated TypeScript client parses.
/// </summary>
/// <remarks>
/// The generated client reads the tag at run time and falls back to the client's default timeout for a value it cannot
/// parse, so a typo would silently drop the method's timeout. The format here is the generated client's
/// <c>parseTimeoutTag</c> (<c>src/elarion-jsonrpc-client-generator/src/rpc-client-source.ts</c>); both are tested
/// against the same cases (<c>tests/timeout-tag-cases.json</c> of that package). A
/// <see cref="DiagnosticAnalyzer"/> (not a generator) because nothing is emitted and a tag may sit on a base handler
/// class that no transport generator looks at. An error, not a warning: the value is wrong for every client, and a
/// silent run-time fallback is what the check replaces.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ClientTagAnalyzer : DiagnosticAnalyzer {
    private const string ClientTagAttributeMetadataName = "Elarion.Abstractions.ClientTagAttribute";

    /// <summary>The tag key the generated client reads as a method's timeout.</summary>
    internal const string TimeoutKey = "timeout";

    /// <summary>The largest timeout a JavaScript timer accepts, in milliseconds (a larger one fires at once).</summary>
    internal const double MaxTimeoutMilliseconds = 2_147_483_647;

    // Keep in sync with parseTimeoutTag in rpc-client-source.ts: ASCII digits, an optional fraction, an optional
    // unit (ms, s, m; seconds when omitted), any case, spaces or tabs around. \z, because .NET's $ also matches before
    // a final line feed and JavaScript's does not; the unit's case spelled out, because .NET's IgnoreCase folds more
    // characters (such as the long s) than JavaScript's /i does.
    private static readonly Regex TimeoutPattern = new(
        "^[ \\t]*([0-9]+(?:\\.[0-9]+)?)[ \\t]*([mM][sS]|[sS]|[mM])?[ \\t]*\\z",
        RegexOptions.CultureInvariant);

    private static readonly DiagnosticDescriptor InvalidTimeoutTag = new(
        "ELRPC013",
        "Invalid timeout client tag",
        "[ClientTag(\"timeout\", \"{0}\")] on '{1}' is not a timeout the generated client can read, so it would use its "
        + "default timeout for this method. Write a positive number with a unit, such as \"1500ms\", \"30s\" or "
        + "\"3m\" (a bare number is seconds), of at most 2147483647 ms.",
        "Elarion.JsonRpc",
        DiagnosticSeverity.Error,
        true);

    private static readonly ImmutableArray<DiagnosticDescriptor> SupportedDiagnosticsArray =
        ImmutableArray.Create(InvalidTimeoutTag);

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => SupportedDiagnosticsArray;

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context) {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(static startContext => {
            var clientTag = startContext.Compilation.GetTypeByMetadataName(ClientTagAttributeMetadataName);
            if (clientTag is null)
                return;

            startContext.RegisterSymbolAction(
                symbolContext => AnalyzeType(symbolContext, clientTag),
                SymbolKind.NamedType);
        });
    }

    /// <summary>
    /// Reads a timeout tag value as the generated client does: the timeout in milliseconds, or <see langword="false"/>
    /// when the client would ignore it.
    /// </summary>
    internal static bool TryParseTimeout(string? value, out double milliseconds) {
        milliseconds = 0;
        if (value is null)
            return false;

        var match = TimeoutPattern.Match(value);
        if (!match.Success)
            return false;

        var amount = double.Parse(match.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        var unit = match.Groups[2].Success ? match.Groups[2].Value.ToLowerInvariant() : "s";
        milliseconds = unit switch {
            "ms" => amount,
            "m" => amount * 60_000,
            _ => amount * 1000
        };
        return milliseconds is > 0 and <= MaxTimeoutMilliseconds;
    }

    private static void AnalyzeType(SymbolAnalysisContext context, INamedTypeSymbol clientTag) {
        foreach (var attribute in context.Symbol.GetAttributes()) {
            if (!SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, clientTag))
                continue;

            var args = attribute.ConstructorArguments;
            if (args.Length != 2 || args[0].Value is not string key || key != TimeoutKey)
                continue;

            var value = args[1].Value as string;
            if (TryParseTimeout(value, out _))
                continue;

            var location = attribute.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation()
                           ?? context.Symbol.Locations.FirstOrDefault();
            context.ReportDiagnostic(Diagnostic.Create(
                InvalidTimeoutTag, location, value ?? "null", context.Symbol.Name));
        }
    }
}

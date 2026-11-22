using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Elarion.Generators;

public sealed partial class HandlerRegistrationGenerator {
    internal const string PreviewableRequestMetadataName = "Elarion.Abstractions.Previews.IPreviewableRequest";
    private const string PreviewOutcomeMetadataName = "Elarion.Abstractions.Previews.IPreviewOutcome`1";

    private static readonly DiagnosticDescriptor PreviewableResponseNotPreviewOutcomeDescriptor = new(
        "ELPREV001",
        "Previewable command handler does not return a preview outcome",
        "Handler '{0}' handles previewable command '{1}' but responds with '{2}'; the preview decorator needs a "
        + "Result<T> whose T implements IPreviewOutcome<T> to stamp and compare the outcome's fingerprint, so the "
        + "request's Preview and PreviewFingerprint would be silently ignored. Return Result<TOutcome> with "
        + "TOutcome : IPreviewOutcome<TOutcome>, or stop implementing IPreviewableRequest.",
        "Elarion.Abstractions.Previews",
        DiagnosticSeverity.Error,
        true);

    // Decides whether the preview decorator attaches (ADR-0104). Like [Idempotent], attachment is a compile-time
    // decision, but keyed on the request type implementing IPreviewableRequest rather than on an attribute: the
    // request carries the Preview/PreviewFingerprint members the decorator reads. Returns the outcome type's FQN (the
    // T of Result<T>), or null when the handler is not previewable. A previewable request whose response cannot carry
    // a fingerprint is ELPREV001 — never a decorator that silently ignores a preview and commits.
    private static string? ParsePreviewable(
        ClassDeclarationSyntax classDecl,
        INamedTypeSymbol classSymbol,
        ITypeSymbol requestType,
        ITypeSymbol responseType,
        Compilation compilation,
        SymbolDisplayFormat fmt,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics) {
        if (!RequestImplementsMarker(requestType, compilation, PreviewableRequestMetadataName))
            return null;

        if (responseType is INamedTypeSymbol { TypeArguments.Length: 1 } result
            && result.OriginalDefinition.ToDisplayString() == "Elarion.Abstractions.Result<T>"
            && IsPreviewOutcome(result.TypeArguments[0], compilation))
            return result.TypeArguments[0].ToDisplayString(fmt);

        diagnostics.Add(DiagnosticInfo.Create(
            PreviewableResponseNotPreviewOutcomeDescriptor,
            classDecl.Identifier.GetLocation(),
            classSymbol.ToDisplayString(fmt),
            requestType.ToDisplayString(fmt),
            responseType.ToDisplayString(fmt)));
        return null;
    }

    /// <summary>
    /// Whether <paramref name="outcomeType"/> implements <c>IPreviewOutcome&lt;outcomeType&gt;</c> — the success value
    /// shape the preview decorator stamps. Shared with the error-contract discovery, which implies the preview errors
    /// exactly where the decorator attaches.
    /// </summary>
    internal static bool IsPreviewOutcome(ITypeSymbol outcomeType, Compilation compilation) {
        var outcomeInterface = compilation.GetTypeByMetadataName(PreviewOutcomeMetadataName);
        if (outcomeInterface is null)
            return false;

        var constructed = outcomeInterface.Construct(outcomeType);
        foreach (var iface in outcomeType.AllInterfaces)
            if (SymbolEqualityComparer.Default.Equals(iface, constructed))
                return true;

        return false;
    }

    /// <summary>Whether <paramref name="requestType"/> implements <c>IPreviewableRequest</c>.</summary>
    internal static bool IsPreviewableRequest(ITypeSymbol requestType, Compilation compilation) {
        return RequestImplementsMarker(requestType, compilation, PreviewableRequestMetadataName);
    }

    // ELSG013 for the preview decorator: it resolves the per-dispatch IUnitOfWork when the chain is built, so a
    // singleton chain (built once from the root provider) would capture it.
    private static void VerifySingletonPreview(
        ClassDeclarationSyntax classDecl,
        string handlerFqn,
        string? previewOutcomeFqn,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics) {
        if (previewOutcomeFqn is null)
            return;

        diagnostics.Add(DiagnosticInfo.Create(
            SingletonHandlerScopedPipelineDescriptor,
            classDecl.Identifier.GetLocation(),
            handlerFqn,
            "the preview decorator (a previewable command)"));
    }

    // The preview decorator sits just inside idempotency and outside the [DecoratorList] entries: it owns the unit of
    // work of a preview and of a fingerprinted commit (a savepoint under [Idempotent]), and an application transaction
    // decorator inside it nests. The unit of work is resolved softly so an ordinary call — which passes straight
    // through — still works on a host without one; a preview there answers preview.unsupported (fail closed).
    private static void AppendPreviewDecorator(StringBuilder sb, HandlerInfo handler) {
        if (handler.PreviewOutcomeFqn is null)
            return;

        sb.AppendLine(
            $"                handler = new global::Elarion.Pipeline.PreviewDecorator<{handler.RequestFqn}, {handler.PreviewOutcomeFqn}>(");
        sb.AppendLine("                    handler,");
        sb.AppendLine("                    sp.GetService<global::Elarion.Abstractions.Pipeline.IUnitOfWork>(),");
        // An outcome with members visible by permission is fingerprinted as the caller sees it (ADR-0103/0104), so the
        // fingerprint cannot confirm a guess of a hidden member.
        if (handler.Visibility is { } visibility && visibility.ValueFqn == handler.PreviewOutcomeFqn) {
            sb.AppendLine(
                "                    sp.GetRequiredService<global::Elarion.Abstractions.Serialization.IElarionJsonSerialization>(),");
            sb.AppendLine($"                    sp.GetService<{VisibilityPermissionCheckerFqn}>(),");
            sb.AppendLine("                    __Hide0);");
        }
        else {
            sb.AppendLine(
                "                    sp.GetRequiredService<global::Elarion.Abstractions.Serialization.IElarionJsonSerialization>());");
        }
        AppendPipelineStep(sb, "                ", "global::Elarion.Pipeline.PreviewDecorator<,>", false);
    }
}

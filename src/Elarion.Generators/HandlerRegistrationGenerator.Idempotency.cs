using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Elarion.Generators;

public sealed partial class HandlerRegistrationGenerator {
    private const string CommandMetadataName = "Elarion.Abstractions.ICommand";

    // Decides whether the idempotency decorator attaches to this handler. Like authorization/feature-gate,
    // attachment is a compile-time presence decision (any [Idempotent] on the handler). A closed gate/replay
    // returns TResponse.Failure(...) or a replayed result, so the response must implement
    // IResultFailureFactory<TResponse> (ELIDEM001) or the decorator would be silently skipped. Idempotency only
    // applies to commands (ELIDEM002), retention must be positive (ELIDEM003), and it is mutually exclusive with
    // caching (ELIDEM004).
    private static IdempotentInfo? ParseIdempotent(
        ClassDeclarationSyntax classDecl,
        INamedTypeSymbol classSymbol,
        ITypeSymbol requestType,
        ITypeSymbol responseType,
        Compilation compilation,
        SymbolDisplayFormat fmt,
        CacheableInfo? cacheable,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics) {
        var attributeSymbol = compilation.GetTypeByMetadataName(IdempotentAttributeMetadataName);
        if (attributeSymbol is null)
            return null;

        var attribute = classSymbol.GetAttributes()
            .FirstOrDefault(candidate => SymbolEqualityComparer.Default.Equals(candidate.AttributeClass, attributeSymbol));
        if (attribute is null)
            return null;

        var location = classDecl.Identifier.GetLocation();

        // ELIDEM001: the decorator synthesizes 400/409/422/replay via TResponse.Failure — the response must be a Result.
        if (!ResponseSupportsFailure(responseType, compilation)) {
            diagnostics.Add(DiagnosticInfo.Create(
                IdempotentResponseNotFailureCapable,
                location,
                classSymbol.ToDisplayString(fmt),
                responseType.ToDisplayString(fmt)));
            return null;
        }

        // ELIDEM002: idempotency only applies to state-changing commands.
        if (!ImplementsCommand(requestType, compilation)) {
            diagnostics.Add(DiagnosticInfo.Create(
                IdempotentOnNonCommandDescriptor,
                location,
                classSymbol.ToDisplayString(fmt),
                requestType.ToDisplayString(fmt)));
            return null;
        }

        var retentionHours = 24;
        var keyRequired = true;
        var scopeValue = 0;
        var fingerprint = true;
        var conflictBehaviorValue = 0;
        var storeFailuresValue = 0;

        foreach (var namedArgument in attribute.NamedArguments) {
            switch (namedArgument.Key) {
                case "RetentionHours" when namedArgument.Value.Value is int retention:
                    retentionHours = retention;
                    break;
                case "KeyRequired" when namedArgument.Value.Value is bool required:
                    keyRequired = required;
                    break;
                case "Scope" when namedArgument.Value.Value is int scope:
                    scopeValue = scope;
                    break;
                case "Fingerprint" when namedArgument.Value.Value is bool useFingerprint:
                    fingerprint = useFingerprint;
                    break;
                case "ConflictBehavior" when namedArgument.Value.Value is int conflict:
                    conflictBehaviorValue = conflict;
                    break;
                case "StoreFailures" when namedArgument.Value.Value is int storeFailures:
                    storeFailuresValue = storeFailures;
                    break;
            }
        }

        // ELIDEM003: retention must be positive.
        if (retentionHours <= 0) {
            diagnostics.Add(DiagnosticInfo.Create(
                InvalidIdempotentRetentionDescriptor,
                location,
                classSymbol.ToDisplayString(fmt)));
        }

        // ELIDEM004: caching is for queries, idempotency for commands.
        if (cacheable is not null) {
            diagnostics.Add(DiagnosticInfo.Create(
                IdempotentAndCacheableDescriptor,
                location,
                classSymbol.ToDisplayString(fmt)));
        }

        var resultValueFqn = TryGetResultValueFqn(responseType, fmt);
        return new IdempotentInfo(
            retentionHours,
            keyRequired,
            scopeValue,
            fingerprint,
            conflictBehaviorValue,
            storeFailuresValue,
            resultValueFqn);
    }

    private static bool ImplementsCommand(ITypeSymbol requestType, Compilation compilation) {
        var commandSymbol = compilation.GetTypeByMetadataName(CommandMetadataName);
        if (commandSymbol is null)
            return false;

        if (SymbolEqualityComparer.Default.Equals(requestType, commandSymbol))
            return true;

        foreach (var iface in requestType.AllInterfaces) {
            if (SymbolEqualityComparer.Default.Equals(iface, commandSymbol))
                return true;
        }

        return false;
    }
}

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
            resultValueFqn,
            Owner: null);
    }

    // The inbox retention is deliberately NOT per-consumer: the invariant it serves — retention must exceed the
    // delivery tier's maximum retry window — is a transport property (OutboxOptions), not a business one. 24 h
    // is ~33× the default outbox retry window; a global knob can be added if a deployment ever configures
    // retries beyond it.
    private const int InboxRetentionHours = 24;

    // The inbox (ADR-0022): every handler-form consumer whose request is an IIntegrationEvent is deduped by
    // default — integration delivery remains at-least-once across the consumer-commit/finalize crash window,
    // so dedup must be the pit of success. The synthesized policy reuses the
    // idempotency decorator with Consumer scope: owner = this handler's identity (baked in below), key = the
    // message id seeded by the delivery tier, WaitThenReplay so a lease-race loser waits for the winner's claim and
    // replays it (never acknowledging success while the winner is still uncommitted), no fingerprint (the payload
    // IS the message), and KeyRequired=false so a direct invocation without a seeded id (tests, hand-rolled
    // dispatch) passes through un-deduped. [AllowDuplicates] opts out — the consumer-side [AllowAnonymous]: a
    // positive declaration that duplicate deliveries are harmless here. [Idempotent] never combines (it is inert
    // on non-commands — ELIDEM002).
    private static IdempotentInfo? ParseInbox(
        ClassDeclarationSyntax classDecl,
        INamedTypeSymbol classSymbol,
        ITypeSymbol requestType,
        ITypeSymbol responseType,
        Compilation compilation,
        SymbolDisplayFormat fmt,
        ImmutableArray<DiagnosticInfo>.Builder diagnostics) {
        var allowDuplicatesSymbol = compilation.GetTypeByMetadataName(AllowDuplicatesAttributeMetadataName);
        var allowDuplicates = allowDuplicatesSymbol is not null
            && classSymbol.GetAttributes().Any(candidate =>
                SymbolEqualityComparer.Default.Equals(candidate.AttributeClass, allowDuplicatesSymbol));

        var isIntegrationEvent = RequestImplementsMarker(requestType, compilation, IntegrationEventMetadataName);
        if (!isIntegrationEvent) {
            // ELINBX001: [AllowDuplicates] on a non-integration-event handler has no effect (domain-event
            // consumers are exactly-once by atomicity; commands/queries use [Idempotent]).
            if (allowDuplicates) {
                diagnostics.Add(DiagnosticInfo.Create(
                    AllowDuplicatesOnNonIntegrationEventDescriptor,
                    classDecl.Identifier.GetLocation(),
                    classSymbol.ToDisplayString(fmt),
                    requestType.ToDisplayString(fmt)));
            }

            return null;
        }

        if (allowDuplicates)
            return null;

        // A handler-form consumer is IHandler<TEvent, Result<Unit>> (ELEVT005 rejects other shapes at consumer
        // registration), so a response without IResultFailureFactory is not a consumer — skip silently.
        if (!ResponseSupportsFailure(responseType, compilation))
            return null;

        return CreateInboxInfo(ComputeConsumerOwner(classSymbol), TryGetResultValueFqn(responseType, fmt));
    }

    // The canonical inbox policy (ADR-0022), built in one place so every emitter — the handler generator's
    // structural discovery and the actor generator's synthesized relay (ADR-0046) — produces the same
    // Consumer-scoped dedupe. Only the owner discriminator and the stored result-value type vary per consumer.
    internal static IdempotentInfo CreateInboxInfo(string owner, string? resultValueFqn) =>
        new(
            InboxRetentionHours,
            KeyRequired: false,
            ScopeValue: 2, // IdempotencyScope.Consumer
            Fingerprint: false,
            ConflictBehaviorValue: 1, // IdempotencyConflictBehavior.WaitThenReplay
            StoreFailuresValue: 0, // IdempotencyFailureStorage.None — a failed consumer retries
            resultValueFqn,
            Owner: owner);

    // The Consumer-scope owner discriminator: the handler's fully qualified name, verbatim while it fits the
    // store's 128-char owner column, else truncated with a stable SHA-256 suffix so two long names never collide.
    // Computed at generation time — deterministic across builds, no runtime hashing.
    private static string ComputeConsumerOwner(INamedTypeSymbol classSymbol) =>
        TruncateOwner(classSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));

    // Truncation of the owner discriminator, shared with the actor generator (ADR-0046), which computes it
    // from the relay's fully qualified name string (the relay class has no symbol at discovery time).
    internal static string TruncateOwner(string fullName) {
        const int maxOwnerLength = 128;
        if (fullName.StartsWith("global::", StringComparison.Ordinal))
            fullName = fullName.Substring("global::".Length);

        if (fullName.Length <= maxOwnerLength)
            return fullName;

        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(fullName));
        var suffix = new System.Text.StringBuilder(16);
        for (var i = 0; i < 8; i++)
            suffix.Append(hash[i].ToString("x2"));

        return fullName.Substring(0, maxOwnerLength - 17) + "-" + suffix;
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

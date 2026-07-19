using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Elarion.AspNetCore.OpenApi.Transformers;

/// <summary>
/// Normalizes operation ids so generated clients get readable method names. The generator sets each endpoint's
/// name to the handler's fully-qualified type name (e.g. <c>Billing.Invoicing.GetInvoice</c>), which OpenAPI uses
/// verbatim as the <c>operationId</c>. This transformer strips the namespace and a trailing <c>Handler</c>/
/// <c>Endpoint</c> suffix (→ <c>GetInvoice</c>). It runs as a document transformer so it can de-collide: if two
/// operations would normalize to the same id, both keep their original (unique) id — correctness over prettiness.
/// </summary>
internal sealed class ElarionOperationIdDocumentTransformer : IOpenApiDocumentTransformer {
    private static readonly string[] StrippedSuffixes = ["Handler", "Endpoint"];

    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken) {
        if (document.Paths is null) return Task.CompletedTask;

        var operations = document.Paths.Values
            .Where(pathItem => pathItem.Operations is not null)
            .SelectMany(pathItem => pathItem.Operations!.Values)
            .Where(operation => !string.IsNullOrEmpty(operation.OperationId))
            .ToList();

        var candidates =
            operations.ToDictionary(operation => operation, operation => Normalize(operation.OperationId!));

        var occurrences = candidates.Values
            .GroupBy(candidate => candidate, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        foreach (var operation in operations) {
            var candidate = candidates[operation];
            if (occurrences[candidate] == 1) operation.OperationId = candidate;
        }

        return Task.CompletedTask;
    }

    private static string Normalize(string operationId) {
        var lastDot = operationId.LastIndexOf('.');
        var simple = lastDot >= 0 ? operationId[(lastDot + 1)..] : operationId;

        foreach (var suffix in StrippedSuffixes)
            if (simple.Length > suffix.Length && simple.EndsWith(suffix, StringComparison.Ordinal)) {
                simple = simple[..^suffix.Length];
                break;
            }

        return simple;
    }
}

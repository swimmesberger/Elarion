using Elarion.Abstractions.Features;
using Microsoft.Extensions.Configuration;

namespace Billing.Application.Modules.Clients.Features;

/// <summary>A code-defined flag that reads its switch from configuration — resolvers may inject services.</summary>
[FeatureFlag("bulk-import", Description = "Bulk client import (Features:BulkImport).", ExposeToClient = true)]
public sealed class BulkImportFlag(IConfiguration configuration) : IFeatureFlagResolver {
    public ValueTask<bool> IsEnabledAsync(FeatureEvaluationContext context, CancellationToken ct) {
        return ValueTask.FromResult(bool.TryParse(configuration["Features:BulkImport"], out var enabled) && enabled);
    }
}

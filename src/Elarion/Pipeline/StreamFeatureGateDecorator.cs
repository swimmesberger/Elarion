using System.Reflection;
using Elarion.Abstractions;
using Elarion.Abstractions.Features;
using Elarion.Abstractions.Pipeline;
using Elarion.Diagnostics;
using System.Diagnostics;

namespace Elarion.Pipeline;

/// <summary>Applies feature gates before a stream is accepted.</summary>
public sealed class StreamFeatureGateDecorator<TRequest, TItem>(
    IStreamHandler<TRequest, TItem> inner,
    StreamHandlerMetadata metadata,
    IFeatureFlagService features
) : IStreamHandler<TRequest, TItem> {
    public async ValueTask<Result<IAsyncEnumerable<TItem>>> HandleAsync(TRequest request, CancellationToken ct) {
        foreach (var gate in metadata.HandlerType.GetCustomAttributes<FeatureGateAttribute>(inherit: true)) {
            // Generator diagnostics make this visible at build time. At runtime an empty gate is deliberately
            // inert (including Negate=true), matching FeatureGateDecorator's unary semantics.
            var effectiveFeatures = gate.Features.Where(static feature => !string.IsNullOrWhiteSpace(feature)).ToArray();
            if (effectiveFeatures.Length == 0)
                continue;
            var enabled = gate.Requirement == FeatureRequirement.All;
            foreach (var feature in effectiveFeatures) {
                var current = await features.IsEnabledAsync(feature, ct).ConfigureAwait(false);
                if (gate.Requirement == FeatureRequirement.All && !current) { enabled = false; break; }
                if (gate.Requirement == FeatureRequirement.Any && current) { enabled = true; break; }
            }

            if (gate.Negate ? enabled : !enabled) {
                Activity.Current?.SetTag("elarion.feature_gate.outcome", "closed");
                HandlerTelemetry.RecordFeatureGateClosed(metadata.HandlerType.Name);
                return AppError.NotFound("The requested resource was not found.");
            }
        }

        return await inner.HandleAsync(request, ct).ConfigureAwait(false);
    }
}

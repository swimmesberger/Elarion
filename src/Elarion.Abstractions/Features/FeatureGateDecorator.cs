using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Elarion.Abstractions.Diagnostics;
using Elarion.Abstractions.Pipeline;

namespace Elarion.Abstractions.Features;

/// <summary>
/// Enforces the <see cref="FeatureGateAttribute"/>s declared on a handler. The gates are read off the concrete
/// handler type via <see cref="HandlerMetadata"/> — never <c>inner.GetType()</c> — so the guard is correct at any
/// position in the decorator chain (the generator places it just inside the authorization gate, so a disabled
/// feature never touches caching, the pipeline, or the handler). A gate that is not satisfied short-circuits with
/// <see cref="AppError.NotFound(string)"/>, deliberately mirroring how Microsoft's MVC <c>[FeatureGate]</c> returns
/// a 404 so a disabled feature is indistinguishable from a missing resource. The failure message is generic on
/// purpose — echoing the gated feature name would leak the very thing the 404 hides.
/// </summary>
public sealed class FeatureGateDecorator<TRequest, TResponse>(
    IHandler<TRequest, TResponse> inner,
    HandlerMetadata metadata,
    IFeatureFlagService features
) : IHandler<TRequest, TResponse>
    where TResponse : IResultFailureFactory<TResponse> {
    // A handler's gates never change, so the parsed-from-attributes set is cached per concrete handler type and the
    // single reflection pass runs once per type — mirroring AuthorizationDecorator.
    private static readonly ConditionalWeakTable<Type, GatesBox> Cache = new();

    private const string NotFoundMessage = "The requested resource was not found.";

    /// <inheritdoc />
    public async ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken ct) {
        foreach (var gate in ResolveGates()) {
            if (!await IsSatisfiedAsync(gate, ct).ConfigureAwait(false)) {
                // The wire response stays an opaque 404, but telemetry is operator-facing, so the
                // closed gate is tagged on the handler span and counted per handler.
                Activity.Current?.SetTag("elarion.feature_gate.outcome", "closed");
                HandlerTelemetry.RecordFeatureGateClosed(metadata.HandlerType.Name);
                return TResponse.Failure(AppError.NotFound(NotFoundMessage));
            }
        }

        return await inner.HandleAsync(request, ct).ConfigureAwait(false);
    }

    private async ValueTask<bool> IsSatisfiedAsync(Gate gate, CancellationToken ct) {
        // A gate with no features is treated as satisfied so an empty [FeatureGate] never spuriously 404s; the
        // generator reports it at build time.
        if (gate.Features.Length == 0) {
            return true;
        }

        bool enabled;
        if (gate.Requirement == FeatureRequirement.Any) {
            enabled = false;
            foreach (var feature in gate.Features) {
                if (await features.IsEnabledAsync(feature, ct).ConfigureAwait(false)) {
                    enabled = true;
                    break;
                }
            }
        } else {
            enabled = true;
            foreach (var feature in gate.Features) {
                if (!await features.IsEnabledAsync(feature, ct).ConfigureAwait(false)) {
                    enabled = false;
                    break;
                }
            }
        }

        return gate.Negate ? !enabled : enabled;
    }

    private Gate[] ResolveGates() =>
        Cache.GetValue(metadata.HandlerType, static type => new GatesBox(Parse(type))).Value;

    private static Gate[] Parse(Type handlerType) =>
        handlerType.GetCustomAttributes<FeatureGateAttribute>(inherit: true)
            .Select(static attribute => new Gate(
                attribute.Features.ToArray(),
                attribute.Requirement,
                attribute.Negate))
            .ToArray();

    private readonly record struct Gate(string[] Features, FeatureRequirement Requirement, bool Negate);

    // ConditionalWeakTable requires a reference-type value, so the parsed gates are boxed once per type.
    private sealed class GatesBox(Gate[] value) {
        public Gate[] Value { get; } = value;
    }
}

using System.Text.Json.Serialization.Metadata;
using Elarion.Abstractions.Modules;

namespace EdgeTelemetry.Api.Modules.Telemetry;

/// <summary>
/// The telemetry module: the row contracts, the handlers, and the JSON context live under it, and the
/// generated bootstrapper registers all of it (<c>AddElarion</c>) gated by
/// <c>Modules:Telemetry:Enabled</c>. The handlers carry no decorator attributes, so only the always-on
/// pair wraps the typed <c>IHandler&lt;TRequest, Result&lt;TResponse&gt;&gt;</c> registrations: tracing
/// (a span + duration metric per call, near-zero cost when nothing listens) and user-context
/// enrichment — gate decorators (authorization, feature gates, validation) attach only per attribute.
/// </summary>
[AppModule("Telemetry")]
public static partial class TelemetryModule {
    public static IJsonTypeInfoResolver GetJsonTypeInfoResolver() {
        return TelemetryJsonContext.Default;
    }
}

using System.Text.Json.Serialization.Metadata;
using Elarion.Abstractions.Modules;

namespace EdgeTelemetry.Api.Modules.Telemetry;

/// <summary>
/// The telemetry module: the row contracts, the handlers, and the JSON context live under it, and the
/// generated bootstrapper registers all of it (<c>AddElarion</c>) gated by
/// <c>Modules:Telemetry:Enabled</c>. The handlers carry no decorator attributes, so they register as
/// plain typed <c>IHandler&lt;TRequest, Result&lt;TResponse&gt;&gt;</c> services — the pipeline costs
/// nothing until an attribute asks for a decorator.
/// </summary>
[AppModule("Telemetry")]
public static partial class TelemetryModule {
    public static IJsonTypeInfoResolver GetJsonTypeInfoResolver() => TelemetryJsonContext.Default;
}

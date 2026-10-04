using System.Text.Json.Serialization.Metadata;
using Elarion.Abstractions.Modules;

namespace Billing.Application.Modules.Clients;

/// <summary>A feature module. The class is intentionally minimal — just the marker and a JSON resolver.
/// The generator emits <c>ClientsModuleElarionModuleServices.ConfigureDefaultServices</c>, which the
/// host bootstrapper calls to register this module's handlers/services/validators. There are no
/// hand-written <c>AddClientsHandlers()</c> calls. The module's feature flags ("client-portal-v2",
/// "bulk-import") are declared once on their resolver classes under <c>Features/</c>; they are exposed to the
/// frontend by the session snapshot and need no server-side <c>[FeatureGate]</c> behind them.</summary>
[AppModule("Clients")]
public static partial class ClientsModule {
    public static IJsonTypeInfoResolver GetJsonTypeInfoResolver() {
        return ClientsJsonContext.Default;
    }
}

using System.Text.Json.Serialization.Metadata;
using Elarion.Abstractions.Modules;

namespace Billing.Application.Modules.Clients;

/// <summary>A feature module. The class is intentionally minimal — just the marker and a JSON resolver.
/// The generator emits <c>ClientsModuleElarionModuleServices.ConfigureDefaultServices</c>, which the
/// host bootstrapper calls to register this module's handlers/services/validators. There are no
/// hand-written <c>AddClientsHandlers()</c> calls.</summary>
[AppModule("Clients")]
// Names exposed to the frontend via the client-capability bootstrap (ADR-0020). "client-portal-v2" is a pure
// UI flag with no server-side [FeatureGate] behind it — still first-class, evaluated with the user's context.
[ClientFeatures("client-portal-v2", "bulk-import")]
public static partial class ClientsModule {
    public static IJsonTypeInfoResolver GetJsonTypeInfoResolver() => ClientsJsonContext.Default;
}

using System.Text.Json.Serialization.Metadata;
using Elarion.Abstractions.Modules;

namespace Billing.Application.Modules.Clients;

/// <summary>A feature module. The class is intentionally minimal — just the marker and a JSON resolver.
/// The generator emits <c>ClientsModuleElarionModuleServices.ConfigureDefaultServices</c>, which the
/// host bootstrapper calls to register this module's handlers/services/validators. There are no
/// hand-written <c>AddClientsHandlers()</c> calls.</summary>
[AppModule("Clients")]
public static partial class ClientsModule {
    public static IJsonTypeInfoResolver GetJsonTypeInfoResolver() => ClientsJsonContext.Default;
}

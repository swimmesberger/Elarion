using System.Text.Json.Serialization;

namespace Elarion.ClientEvents.AspNetCore;

/// <summary>Source-gen context for the endpoint's wire types (AOT-safe, independent of the host's
/// canonical options). The request record itself is transport-neutral and lives in
/// <c>Elarion.ClientEvents</c>, shared with connection adapters.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ClientEventSubscriptionRequest[]))]
internal sealed partial class ClientEventEndpointJsonContext : JsonSerializerContext;

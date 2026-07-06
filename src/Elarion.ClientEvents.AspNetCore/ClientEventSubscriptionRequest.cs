using System.Text.Json.Serialization;

namespace Elarion.ClientEvents.AspNetCore;

/// <summary>
/// One requested subscription from the wire: a topic, optionally narrowed to a resource key. A topic-only
/// request observes the topic's global scope plus the caller's own user scope; a resource request observes
/// exactly that resource scope (and must pass the <c>IClientEventSubscriptionAuthorizer</c>).
/// </summary>
internal sealed record ClientEventSubscriptionRequest {
    /// <summary>The topic name (e.g. <c>"invoicing.invoiceChanged"</c>).</summary>
    public required string Topic { get; init; }

    /// <summary>The application-defined resource key (e.g. <c>"customer:42"</c>), when subscribing a
    /// resource scope.</summary>
    public string? Resource { get; init; }
}

/// <summary>Source-gen context for the endpoint's own wire types (AOT-safe, independent of the host's
/// canonical options).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ClientEventSubscriptionRequest[]))]
internal sealed partial class ClientEventEndpointJsonContext : JsonSerializerContext;

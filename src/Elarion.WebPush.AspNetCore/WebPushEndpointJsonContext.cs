using System.Text.Json.Serialization;

namespace Elarion.WebPush.AspNetCore;

/// <summary>The public-key response body.</summary>
/// <param name="PublicKey">The VAPID public key (base64url).</param>
internal sealed record WebPushPublicKeyResponse(string PublicKey);

/// <summary>The unsubscribe request body.</summary>
/// <param name="Endpoint">The subscription endpoint to delete.</param>
internal sealed record WebPushUnsubscribeRequest(string? Endpoint);

/// <summary>
/// Source-gen context for the endpoints' wire types (AOT-safe, independent of the host's JSON options): the
/// shapes are the browser package's fixed contract and must not follow the host's naming policy.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PushSubscriptionRequest))]
[JsonSerializable(typeof(WebPushUnsubscribeRequest))]
[JsonSerializable(typeof(WebPushPublicKeyResponse))]
internal sealed partial class WebPushEndpointJsonContext : JsonSerializerContext;

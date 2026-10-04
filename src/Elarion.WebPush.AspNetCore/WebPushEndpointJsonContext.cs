using System.Text.Json.Serialization;

namespace Elarion.WebPush.AspNetCore;

/// <summary>
/// Source-gen context for the endpoints' wire types (AOT-safe, independent of the host's JSON options): the
/// shapes are the browser package's fixed contract and must not follow the host's naming policy.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PushSubscriptionRequest))]
[JsonSerializable(typeof(WebPushUnsubscribeRequest))]
[JsonSerializable(typeof(WebPushPublicKeyResponse))]
internal sealed partial class WebPushEndpointJsonContext : JsonSerializerContext;

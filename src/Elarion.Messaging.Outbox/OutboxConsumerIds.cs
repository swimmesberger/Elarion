using System.Text.Json;
using System.Text.Json.Serialization;

namespace Elarion.Messaging.Outbox;

internal static class OutboxConsumerIds {
    public static string Serialize(IReadOnlyCollection<string> consumerIds) {
        ArgumentNullException.ThrowIfNull(consumerIds);
        return JsonSerializer.Serialize(
            consumerIds.Count == 0 ? [] : consumerIds.ToArray(),
            OutboxInfrastructureJsonContext.Default.StringArray);
    }

    public static string[] Deserialize(string json) {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize(json, OutboxInfrastructureJsonContext.Default.StringArray) ?? [];
    }
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(string[]))]
internal sealed partial class OutboxInfrastructureJsonContext : JsonSerializerContext;

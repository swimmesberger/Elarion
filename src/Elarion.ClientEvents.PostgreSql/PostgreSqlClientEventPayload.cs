using System.Text.Json;
using System.Text.Json.Serialization;
using Elarion.Abstractions.ClientEvents;

namespace Elarion.ClientEvents.PostgreSql;

/// <summary>
/// The JSON payload carried on the notification channel: a <see cref="ClientEventEnvelope"/> flattened to
/// wire-stable fields. Client events are light by convention (ids/refs, not entity bodies — ADR-0043), so a
/// well-formed publish fits PostgreSQL's ~8000-byte notification payload limit; the broadcaster rejects
/// oversized ones at publish time rather than letting nodes silently diverge.
/// </summary>
internal sealed record PostgreSqlClientEventPayload {
    public required Guid Id { get; init; }

    public required string Topic { get; init; }

    public required int ScopeKind { get; init; }

    public string? ScopeValue { get; init; }

    public required string Payload { get; init; }

    public static string Serialize(ClientEventEnvelope envelope) =>
        JsonSerializer.Serialize(
            new PostgreSqlClientEventPayload {
                Id = envelope.Id,
                Topic = envelope.Topic,
                ScopeKind = (int)envelope.Scope.Kind,
                ScopeValue = envelope.Scope.Value,
                Payload = envelope.Payload,
            },
            PostgreSqlClientEventJsonContext.Default.PostgreSqlClientEventPayload);

    public static bool TryDeserialize(string payload, out ClientEventEnvelope? envelope) {
        try {
            var parsed = JsonSerializer.Deserialize(
                payload, PostgreSqlClientEventJsonContext.Default.PostgreSqlClientEventPayload);
            if (parsed is { Topic.Length: > 0, Payload: not null } && TryCreateScope(parsed, out var scope)) {
                envelope = new ClientEventEnvelope {
                    Id = parsed.Id,
                    Topic = parsed.Topic,
                    Scope = scope,
                    Payload = parsed.Payload,
                };
                return true;
            }
        }
        catch (JsonException) {
            // Fall through to the failure result; the caller logs the malformed payload.
        }

        envelope = null;
        return false;
    }

    private static bool TryCreateScope(PostgreSqlClientEventPayload parsed, out ClientEventScope scope) {
        switch ((ClientEventScopeKind)parsed.ScopeKind) {
            case ClientEventScopeKind.Global:
                scope = ClientEventScope.Global;
                return true;
            case ClientEventScopeKind.User when parsed.ScopeValue is { Length: > 0 } userId:
                scope = ClientEventScope.User(userId);
                return true;
            case ClientEventScopeKind.Resource when parsed.ScopeValue is { Length: > 0 } key:
                scope = ClientEventScope.Resource(key);
                return true;
            default:
                scope = default;
                return false;
        }
    }
}

[JsonSerializable(typeof(PostgreSqlClientEventPayload))]
internal sealed partial class PostgreSqlClientEventJsonContext : JsonSerializerContext;

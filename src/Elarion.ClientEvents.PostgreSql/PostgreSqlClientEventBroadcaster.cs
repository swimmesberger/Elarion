using System.Text;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Elarion.ClientEvents.PostgreSql;

/// <summary>
/// The cross-node <see cref="IClientEventBroadcaster"/>: publishes each envelope as a <c>pg_notify</c> on the
/// configured channel, where every node's <see cref="PostgreSqlClientEventListener"/> — including this node's
/// own, the loop-back that keeps a single delivery path — hands it to local subscribers. Uses a pooled
/// connection per publish; client events ride the recommended after-commit producer (a <c>[ConsumeEvent]</c>
/// projection), so no transaction gating is needed here, and a direct in-handler publish (the ephemeral
/// progress tier) is pre-commit by design.
/// </summary>
internal sealed class PostgreSqlClientEventBroadcaster : IClientEventBroadcaster, IDisposable, IAsyncDisposable {
    // PostgreSQL rejects notification payloads of 8000 bytes or more; a light event (ids/refs) never gets
    // close. Checked at publish so an oversized payload fails loud on the publishing node instead of
    // delivering on some nodes and erroring on others.
    private const int MaxPayloadBytes = 7999;

    private readonly bool _ownsDataSource;
    private readonly PostgreSqlClientEventOptions _options;
    private readonly ILogger<PostgreSqlClientEventBroadcaster> _logger;

    public PostgreSqlClientEventBroadcaster(
        NpgsqlDataSource dataSource,
        bool ownsDataSource,
        PostgreSqlClientEventOptions options,
        ILogger<PostgreSqlClientEventBroadcaster> logger) {
        DataSource = dataSource;
        _ownsDataSource = ownsDataSource;
        _options = options;
        _logger = logger;
    }

    /// <summary>The data source the listener shares for its dedicated <c>LISTEN</c> connection.</summary>
    internal NpgsqlDataSource DataSource { get; }

    public async ValueTask BroadcastAsync(ClientEventEnvelope envelope, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(envelope);

        var payload = PostgreSqlClientEventPayload.Serialize(envelope);
        if (Encoding.UTF8.GetByteCount(payload) > MaxPayloadBytes) {
            _logger.LogError(
                "Dropping client event '{Topic}': its serialized payload exceeds PostgreSQL's notification " +
                "payload limit ({Limit} bytes). Client events are hints — publish ids/refs and let the " +
                "client re-query, not entity bodies.",
                envelope.Topic,
                MaxPayloadBytes);
            return;
        }

        await using var command = DataSource.CreateCommand("SELECT pg_notify($1, $2)");
        command.Parameters.AddWithValue(_options.ChannelName);
        command.Parameters.AddWithValue(payload);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public void Dispose() {
        if (_ownsDataSource) DataSource.Dispose();
    }

    public ValueTask DisposeAsync() {
        if (_ownsDataSource) return DataSource.DisposeAsync();

        return ValueTask.CompletedTask;
    }
}

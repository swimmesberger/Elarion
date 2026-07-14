using Elarion.Abstractions.ClientEvents;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Elarion.ClientEvents.PostgreSql;

/// <summary>
/// The hosted <c>LISTEN</c> loop behind <see cref="PostgreSqlClientEventBroadcaster"/>: holds a dedicated
/// connection subscribed to the notification channel and hands each received envelope to this node's local
/// subscribers. On a connection failure it reconnects with exponential backoff and, because PostgreSQL does
/// not queue notifications for absent listeners, delivers a <see cref="ClientEventControlEvents.Connected"/>
/// control event to <b>every</b> local subscriber after each successful <c>LISTEN</c> establishment — the
/// first included, since subscribers can attach before the first connect succeeds — so clients re-query
/// instead of trusting a stream with a hole in it. While listening, the wait is bounded by
/// <see cref="PostgreSqlClientEventOptions.ConnectionProbeInterval"/> and idle windows probe the connection,
/// so a half-open connection surfaces as an error instead of a permanent silent hang.
/// </summary>
internal sealed class PostgreSqlClientEventListener(
    PostgreSqlClientEventBroadcaster broadcaster,
    IClientEventLocalDelivery delivery,
    PostgreSqlClientEventOptions options,
    ILogger<PostgreSqlClientEventListener> logger) : BackgroundService {
    private readonly TaskCompletionSource _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once the first <c>LISTEN</c> is established — lets tests await readiness.</summary>
    internal Task Listening => _listening.Task;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        var delay = options.InitialReconnectDelay;

        while (!stoppingToken.IsCancellationRequested) {
            try {
                await using var connection = await broadcaster.DataSource.OpenConnectionAsync(stoppingToken).ConfigureAwait(false);
                connection.Notification += OnNotification;
                try {
                    await using (var command = connection.CreateCommand()) {
                        command.CommandText = "LISTEN " + QuoteIdentifier(options.ChannelName);
                        await command.ExecuteNonQueryAsync(stoppingToken).ConfigureAwait(false);
                    }

                    logger.LogInformation(
                        "Listening for client events on PostgreSQL channel '{Channel}'.", options.ChannelName);
                    delay = options.InitialReconnectDelay;

                    // Every establishment — the first included — may follow a window in which subscribers
                    // existed but no listen connection did (SSE serves immediately; the first connect attempt
                    // can back off). Notifications sent in that window are lost, so make every client
                    // re-query; a spurious re-query is cheap and always safe.
                    delivery.DeliverToAll(new ClientEventEnvelope {
                        Id = Guid.CreateVersion7(),
                        Topic = ClientEventControlEvents.Connected,
                        Scope = ClientEventScope.Global,
                        Payload = "{}",
                    });

                    _listening.TrySetResult();

                    while (true) {
                        if (await connection.WaitAsync(options.ConnectionProbeInterval, stoppingToken).ConfigureAwait(false)) {
                            continue;
                        }

                        // Nothing arrived within the probe window. A half-open connection (NAT idle timeout,
                        // failover without a FIN/RST) completes neither the wait nor an error, so run a cheap
                        // round-trip: a dead connection throws here and falls into the reconnect path.
                        await using var probe = connection.CreateCommand();
                        probe.CommandText = "SELECT 1";
                        await probe.ExecuteScalarAsync(stoppingToken).ConfigureAwait(false);
                    }
                }
                finally {
                    connection.Notification -= OnNotification;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                return;
            }
            catch (Exception exception) {
                logger.LogWarning(
                    exception,
                    "The client-event LISTEN connection failed; reconnecting in {Delay}. Events published on " +
                    "other nodes are not delivered here until the connection is re-established.",
                    delay);
            }

            try {
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) {
                return;
            }

            var doubled = delay * 2;
            delay = doubled > options.MaxReconnectDelay ? options.MaxReconnectDelay : doubled;
        }
    }

    private void OnNotification(object sender, NpgsqlNotificationEventArgs args) {
        if (!string.Equals(args.Channel, options.ChannelName, StringComparison.Ordinal)) {
            return;
        }

        if (PostgreSqlClientEventPayload.TryDeserialize(args.Payload, out var envelope)) {
            delivery.Deliver(envelope!);
            return;
        }

        logger.LogWarning(
            "Ignoring a malformed client-event notification on channel '{Channel}'.", options.ChannelName);
    }

    private static string QuoteIdentifier(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"") + "\"";
}

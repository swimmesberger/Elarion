using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Elarion.Settings.PostgreSql;

/// <summary>
/// The hosted <c>LISTEN</c> loop behind <see cref="PostgreSqlSettingsChangeSource"/>: holds a dedicated
/// connection subscribed to the notification channel and fires the matching watch tokens for each received
/// payload. On a connection failure it reconnects with exponential backoff and, because PostgreSQL does not
/// queue notifications for absent listeners, fires <b>all</b> watches after a successful reconnect so watchers
/// re-read and converge on the current state.
/// </summary>
internal sealed class PostgreSqlSettingsChangeListener(
    PostgreSqlSettingsChangeSource changeSource,
    PostgreSqlSettingsChangeOptions options,
    ILogger<PostgreSqlSettingsChangeListener> logger) : BackgroundService {
    private readonly TaskCompletionSource _listening = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once the first <c>LISTEN</c> is established — lets tests await readiness.</summary>
    internal Task Listening => _listening.Task;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        var delay = options.InitialReconnectDelay;
        var hadEstablishedConnection = false;

        while (!stoppingToken.IsCancellationRequested) {
            try {
                await using var connection = await changeSource.DataSource.OpenConnectionAsync(stoppingToken).ConfigureAwait(false);
                connection.Notification += OnNotification;
                try {
                    await using (var command = connection.CreateCommand()) {
                        command.CommandText = "LISTEN " + QuoteIdentifier(options.ChannelName);
                        await command.ExecuteNonQueryAsync(stoppingToken).ConfigureAwait(false);
                    }

                    logger.LogInformation(
                        "Listening for settings changes on PostgreSQL channel '{Channel}'.", options.ChannelName);
                    delay = options.InitialReconnectDelay;

                    if (hadEstablishedConnection) {
                        // Notifications sent while we were disconnected are lost; make watchers re-read.
                        changeSource.FireAll();
                    }

                    hadEstablishedConnection = true;
                    _listening.TrySetResult();

                    while (true) {
                        await connection.WaitAsync(stoppingToken).ConfigureAwait(false);
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
                    "The settings LISTEN connection failed; reconnecting in {Delay}. Changes made on other nodes " +
                    "are not observed until the connection is re-established.",
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

        if (PostgreSqlSettingsChangePayload.TryDeserialize(args.Payload, out var scope, out var key)) {
            changeSource.FireMatching(scope, key);
            return;
        }

        logger.LogWarning(
            "Ignoring a malformed settings change notification on channel '{Channel}'.", options.ChannelName);
    }

    private static string QuoteIdentifier(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"") + "\"";
}

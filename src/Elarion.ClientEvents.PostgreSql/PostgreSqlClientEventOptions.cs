namespace Elarion.ClientEvents.PostgreSql;

/// <summary>Options for the PostgreSQL <c>LISTEN/NOTIFY</c> client-event broadcaster.</summary>
public sealed class PostgreSqlClientEventOptions {
    /// <summary>
    /// The PostgreSQL notification channel client events are published on. All nodes of an application must
    /// use the same channel; two applications sharing a database can isolate their event traffic by choosing
    /// distinct channels. Defaults to <c>elarion_client_events</c>.
    /// </summary>
    public string ChannelName { get; set; } = "elarion_client_events";

    /// <summary>The delay before the first reconnect attempt after the listen connection drops. Defaults to 1 second.</summary>
    public TimeSpan InitialReconnectDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The cap for the exponential reconnect backoff. Defaults to 30 seconds.</summary>
    public TimeSpan MaxReconnectDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long the listener waits for a notification before probing the connection with a cheap round-trip
    /// (<c>SELECT 1</c>). A half-open TCP connection — a NAT idle timeout or a failover that never sends a
    /// FIN/RST — neither delivers notifications nor surfaces an error, so an unbounded wait would leave the
    /// node silently deaf forever; the probe makes such a connection throw and fall into the normal
    /// reconnect path. Defaults to 30 seconds.
    /// </summary>
    public TimeSpan ConnectionProbeInterval { get; set; } = TimeSpan.FromSeconds(30);
}

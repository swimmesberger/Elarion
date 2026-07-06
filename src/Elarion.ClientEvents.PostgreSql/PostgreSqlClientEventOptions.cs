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
}

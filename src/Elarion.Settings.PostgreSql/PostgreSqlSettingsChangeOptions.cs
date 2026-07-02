namespace Elarion.Settings.PostgreSql;

/// <summary>Options for the PostgreSQL <c>LISTEN/NOTIFY</c> settings change source.</summary>
public sealed class PostgreSqlSettingsChangeOptions {
    /// <summary>
    /// The PostgreSQL notification channel settings changes are published on. All nodes of an application must
    /// use the same channel; two applications sharing a database can isolate their settings traffic by choosing
    /// distinct channels. Defaults to <c>elarion_settings_changed</c>.
    /// </summary>
    public string ChannelName { get; set; } = "elarion_settings_changed";

    /// <summary>The delay before the first reconnect attempt after the listen connection drops. Defaults to 1 second.</summary>
    public TimeSpan InitialReconnectDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The cap for the exponential reconnect backoff. Defaults to 30 seconds.</summary>
    public TimeSpan MaxReconnectDelay { get; set; } = TimeSpan.FromSeconds(30);
}

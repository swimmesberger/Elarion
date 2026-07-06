namespace Elarion.ClientEvents;

/// <summary>
/// The reserved control-event names the client-event stream uses alongside application topics. Clients treat
/// every <see cref="Connected"/> occurrence — the initial one when the stream opens, and any later one a
/// cross-node backend injects after a delivery gap (e.g. a dropped <c>LISTEN</c> connection) — as "you may
/// have missed events": re-query, don't patch.
/// </summary>
public static class ClientEventControlEvents {
    /// <summary>The stream is (re-)live and events may have been missed; consumers re-query.</summary>
    public const string Connected = "elarion.connected";

    /// <summary>Periodic idle signal so proxies keep the connection open; carries no information.</summary>
    public const string KeepAlive = "elarion.keepAlive";
}

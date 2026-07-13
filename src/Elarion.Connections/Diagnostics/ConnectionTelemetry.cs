using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Elarion.Connections.Diagnostics;

/// <summary>
/// Central OpenTelemetry-compatible instrumentation for client connections. Signals are collected only when
/// a host registers <see cref="MeterName"/> with its OpenTelemetry providers; without listeners every call
/// site is a no-op.
/// </summary>
/// <remarks>
/// Tags stay <b>bounded</b> per the framework telemetry rules: the transport name (a handful of adapters),
/// never connection ids or principal ids — high-cardinality identity belongs on spans, and per-message
/// spans are the codec's/adapter's business (the kernel never sees individual messages).
/// </remarks>
public static class ConnectionTelemetry {
    /// <summary>The meter name to register with OpenTelemetry metrics.</summary>
    public const string MeterName = "Elarion.Connections";

    private static readonly Meter MeterInstance = new(MeterName);

    /// <summary>Tracks currently registered client connections by transport.</summary>
    public static readonly UpDownCounter<long> ActiveConnections =
        MeterInstance.CreateUpDownCounter<long>(
            "connection.active",
            description: "Number of currently registered client connections");

    /// <summary>Counts connection registrations by transport.</summary>
    public static readonly Counter<long> OpenedCount =
        MeterInstance.CreateCounter<long>(
            "connection.opened",
            description: "Total number of client connections registered");

    /// <summary>Counts connection unregistrations by transport.</summary>
    public static readonly Counter<long> ClosedCount =
        MeterInstance.CreateCounter<long>(
            "connection.closed",
            description: "Total number of client connections unregistered");

    /// <summary>Tracks live client-event subscriptions served over connections (the bridge's pumps).</summary>
    public static readonly UpDownCounter<long> ActiveEventSubscriptions =
        MeterInstance.CreateUpDownCounter<long>(
            "connection.event_subscriptions.active",
            description: "Number of live client-event subscriptions delivered over connections");

    internal static void RecordOpened(string transport) {
        var tags = new TagList { { "elarion.connection.transport", transport } };
        OpenedCount.Add(1, tags);
        ActiveConnections.Add(1, tags);
    }

    internal static void RecordClosed(string transport) {
        var tags = new TagList { { "elarion.connection.transport", transport } };
        ClosedCount.Add(1, tags);
        ActiveConnections.Add(-1, tags);
    }
}

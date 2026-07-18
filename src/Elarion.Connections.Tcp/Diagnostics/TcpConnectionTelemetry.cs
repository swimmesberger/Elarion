using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Elarion.Connections.Tcp.Diagnostics;

/// <summary>
/// Central OpenTelemetry-compatible instrumentation for the TCP connection adapter. Signals are collected
/// only when a host registers <see cref="MeterName"/> with its OpenTelemetry providers; without listeners
/// every call site is a no-op.
/// </summary>
/// <remarks>
/// Tags stay <b>bounded</b> per the framework telemetry rules: fixed stage/outcome vocabularies and the
/// endpoint's transport tag only — never connection or principal ids, payloads, endpoint addresses,
/// operation names, certificate subjects/thumbprints, or raw exception text. High-cardinality identity
/// belongs on spans, and per-message spans are the codec's business.
/// </remarks>
public static class TcpConnectionTelemetry {
    /// <summary>The meter name to register with OpenTelemetry metrics.</summary>
    public const string MeterName = "Elarion.Connections.Tcp";

    private static readonly Meter MeterInstance = new(MeterName);

    // OTel semconv duration buckets (seconds). Without explicit advice the SDK's default
    // boundaries are millisecond-scaled and useless for second-valued histograms.
    private static readonly InstrumentAdvice<double> DurationAdvice = new() {
        HistogramBucketBoundaries = [0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10]
    };

    /// <summary>Records TLS handshake duration in seconds by outcome (<c>ok</c>/<c>failed</c>/<c>timeout</c>).</summary>
    public static readonly Histogram<double> TlsHandshakeDuration =
        MeterInstance.CreateHistogram<double>(
            "elarion.tcp.tls.handshake.duration",
            unit: "s",
            description: "Duration of TCP TLS handshakes",
            advice: DurationAdvice);

    /// <summary>Counts connections that ended in a failure, by bounded establishment/lifecycle stage.</summary>
    public static readonly Counter<long> ConnectionFailures =
        MeterInstance.CreateCounter<long>(
            "elarion.tcp.connection.failures",
            description: "TCP connections that failed, by bounded stage");

    /// <summary>Counts completed connection closes by mode (<c>graceful</c>/<c>forced</c>).</summary>
    public static readonly Counter<long> ConnectionCloses =
        MeterInstance.CreateCounter<long>(
            "elarion.tcp.connection.closed",
            description: "TCP connection teardowns, by close mode");

    /// <summary>Counts idle-window hooks fired on silent links.</summary>
    public static readonly Counter<long> IdleEvents =
        MeterInstance.CreateCounter<long>(
            "elarion.tcp.idle",
            description: "Idle-window events fired for silent TCP connections");

    /// <summary>Tracks admitted outbound sends (queued plus in-progress) across TCP connections.</summary>
    public static readonly UpDownCounter<long> OutboundPending =
        MeterInstance.CreateUpDownCounter<long>(
            "elarion.tcp.outbound.pending",
            description: "Outbound TCP sends admitted and not yet settled");

    /// <summary>Counts sends rejected because the bounded outbound queue was saturated.</summary>
    public static readonly Counter<long> OutboundSaturated =
        MeterInstance.CreateCounter<long>(
            "elarion.tcp.outbound.saturated",
            description: "Outbound TCP sends rejected at full queue capacity");

    internal static void RecordTlsHandshake(string outcome, TimeSpan elapsed) =>
        TlsHandshakeDuration.Record(elapsed.TotalSeconds,
            new TagList { { "elarion.tcp.tls.outcome", outcome } });

    internal static void RecordFailure(string transport, string stage) =>
        ConnectionFailures.Add(1, new TagList {
            { "elarion.connection.transport", transport },
            { "elarion.tcp.failure.stage", stage }
        });

    internal static void RecordClosed(string transport, bool forced) =>
        ConnectionCloses.Add(1, new TagList {
            { "elarion.connection.transport", transport },
            { "elarion.tcp.close.mode", forced ? "forced" : "graceful" }
        });

    internal static void RecordIdle(string transport) =>
        IdleEvents.Add(1, new TagList { { "elarion.connection.transport", transport } });

    internal static void RecordOutboundAdmitted(string transport) =>
        OutboundPending.Add(1, new TagList { { "elarion.connection.transport", transport } });

    internal static void RecordOutboundSettled(string transport) =>
        OutboundPending.Add(-1, new TagList { { "elarion.connection.transport", transport } });

    internal static void RecordOutboundSaturated(string transport) =>
        OutboundSaturated.Add(1, new TagList { { "elarion.connection.transport", transport } });
}

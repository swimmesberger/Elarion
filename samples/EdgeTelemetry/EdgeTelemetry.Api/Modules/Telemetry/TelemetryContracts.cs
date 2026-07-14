using System.Text.Json.Serialization;
using Elarion.Sql;

namespace EdgeTelemetry.Api.Modules.Telemetry;

/// <summary>
/// The stored reading — a row of the TimescaleDB hypertable. <c>[SqlRecord]</c> generates
/// <c>ReadingRowSqlMapper</c> at compile time: ordinal-cached typed reads, typed parameter binding,
/// and the <c>TableName</c>/<c>Columns</c> constants the handlers compose their SQL from. The
/// composite natural key (device, metric, instant) is TimescaleDB's partition-column rule and the
/// ingest idempotency constraint in one — no surrogate id.
/// </summary>
[SqlRecord("readings")]
public sealed record ReadingRow {
    public required string DeviceId { get; init; }

    public required string Metric { get; init; }

    public required DateTimeOffset RecordedAt { get; init; }

    public required double Value { get; init; }

    /// <summary>Optional structured metadata, stored as jsonb through the canonical JSON accessor.</summary>
    [SqlJson]
    public ReadingMeta? Meta { get; init; }
}

public sealed record ReadingMeta {
    public string? Unit { get; init; }

    public string? Source { get; init; }

    public int? Quality { get; init; }
}

/// <summary>
/// A projection row for the hourly-stats query — a <c>[SqlRecord]</c> does not need a physical table;
/// the mapper binds whatever the SELECT produces, by column name (the derived table name is unused).
/// </summary>
[SqlRecord]
public sealed record MetricBucket {
    public required DateTimeOffset Bucket { get; init; }

    public required long Samples { get; init; }

    public required double AvgValue { get; init; }

    public required double MinValue { get; init; }

    public required double MaxValue { get; init; }
}

/// <summary>One ingested sample; the server defaults the timestamp.</summary>
public sealed record ReadingInput {
    public required string DeviceId { get; init; }

    public required string Metric { get; init; }

    public required double Value { get; init; }

    public DateTimeOffset? RecordedAt { get; init; }

    public ReadingMeta? Meta { get; init; }
}

public sealed record IngestResult(int Written);

/// <summary>
/// The module's source-generated JSON context — the bootstrapper collects it into the canonical
/// options (ADR-0023), so minimal-API binding, the <c>Result&lt;T&gt;</c> error envelope, and the
/// <c>[SqlJson]</c> column all read one configuration. Reflection stays off everywhere
/// (<c>JsonSerializerIsReflectionEnabledByDefault=false</c>).
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ReadingInput[]))]
[JsonSerializable(typeof(ReadingRow))]
[JsonSerializable(typeof(List<ReadingRow>))]
[JsonSerializable(typeof(List<MetricBucket>))]
[JsonSerializable(typeof(IngestResult))]
public sealed partial class TelemetryJsonContext : JsonSerializerContext;

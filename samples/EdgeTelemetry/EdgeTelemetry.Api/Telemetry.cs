using System.Text.Json.Serialization;
using Elarion.Sql;

// PostgreSQL provider trigger: [SqlJson] parameters bind as jsonb (a plain string parameter would
// fail PostgreSQL's type check for the meta column).
[assembly: UseElarionSql(Provider = SqlProvider.Npgsql)]

namespace EdgeTelemetry.Api;

/// <summary>
/// The stored reading. <c>[SqlRecord]</c> generates <c>ReadingRowSqlMapper</c> at compile time:
/// ordinal-cached typed reads, typed parameter binding, and the <c>TableName</c>/<c>Columns</c>
/// constants the endpoints compose their SQL from. No reflection — if it builds, it maps.
/// </summary>
[SqlRecord("readings")]
public sealed record ReadingRow {
    public required Guid Id { get; init; }

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

/// <summary>One ingested sample; the server assigns the id and defaults the timestamp.</summary>
public sealed record ReadingInput {
    public required string DeviceId { get; init; }

    public required string Metric { get; init; }

    public required double Value { get; init; }

    public DateTimeOffset? RecordedAt { get; init; }

    public ReadingMeta? Meta { get; init; }
}

public sealed record IngestResult(int Written);

/// <summary>
/// The one source-generated JSON context for the whole host: minimal-API binding reads it from
/// <c>Http.Json</c>, and the <c>[SqlJson]</c> column reads it through Elarion's canonical accessor —
/// reflection stays off everywhere (<c>JsonSerializerIsReflectionEnabledByDefault=false</c>).
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ReadingInput[]))]
[JsonSerializable(typeof(ReadingRow))]
[JsonSerializable(typeof(List<ReadingRow>))]
[JsonSerializable(typeof(List<MetricBucket>))]
[JsonSerializable(typeof(IngestResult))]
public sealed partial class TelemetryJsonContext : JsonSerializerContext;

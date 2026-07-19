using System.Text.Json.Serialization;
using Elarion.Sql;

// The Sql integration tests run against real PostgreSQL, so the test assembly opts into
// provider-aware emission ([SqlJson] parameters bind as jsonb).
[assembly: UseElarionSql(Provider = SqlProvider.Npgsql)]

namespace Elarion.Tests.SqlMapping;

public enum SqlItemStatus {
    Draft = 0,
    Active = 1,
    Archived = 2
}

public sealed record SqlItemProfile {
    public required string Color { get; init; }

    public int Weight { get; init; }
}

[JsonSerializable(typeof(SqlItemProfile))]
public sealed partial class SqlTestJsonContext : JsonSerializerContext;

/// <summary>The kitchen-sink row: every supported column shape the mapper generator emits.</summary>
[SqlRecord("sql_items")]
public sealed partial record SqlItem {
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    [SqlColumn("note_text")] public string? Note { get; init; }

    public required int Quantity { get; init; }

    public long Sequence { get; init; }

    public decimal Price { get; init; }

    public bool Active { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public SqlItemStatus Status { get; init; }

    public SqlItemStatus? PreviousStatus { get; init; }

    public byte[]? Payload { get; init; }

    public DateOnly? DueOn { get; init; }

    [SqlJson] public SqlItemProfile? Profile { get; init; }

    [SqlIgnore] public string? Transient { get; init; }

    // Derived member (no setter): skipped by the mapper, per the state-record convention.
    public bool IsNamed => Name.Length > 0;
}

/// <summary>Positional-record shape: mapped through the primary constructor, no parameterless ctor.</summary>
[SqlRecord]
public sealed partial record SqlPositionalRow(Guid Id, string Label, int Count);

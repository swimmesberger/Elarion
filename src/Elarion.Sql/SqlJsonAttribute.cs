namespace Elarion.Sql;

/// <summary>
/// Maps a property of a <see cref="SqlRecordAttribute">[SqlRecord]</see> type as a JSON column: the
/// generated mapper serializes and deserializes it through the canonical JSON accessor's
/// <c>JsonTypeInfo&lt;T&gt;</c> (<c>IElarionJsonSerialization</c>, ADR-0023) — AOT-strict, one JSON
/// configuration everywhere. The property type must therefore be part of a registered
/// <c>JsonSerializerContext</c>.
/// </summary>
/// <remarks>
/// A mapper with JSON columns takes the accessor as a constructor parameter instead of exposing the
/// static <c>Instance</c>; the generated DI registration wires it automatically. Under the Npgsql
/// provider trigger (<see cref="UseElarionSqlAttribute"/>) parameters bind as <c>jsonb</c>.
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SqlJsonAttribute : Attribute;

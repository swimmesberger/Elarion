namespace Elarion.Sql;

/// <summary>
/// Marks a row type for SQL mapper generation: the bundled generator emits a sealed
/// <c>{Type}SqlMapper : ISqlRowMapper&lt;T&gt;</c> with ordinal-cached typed reads, typed parameter
/// binding, and <c>TableName</c>/column-name constants. There is no reflection twin — a type without
/// <c>[SqlRecord]</c> has no mapper to call, so an unmapped type is a compile error, never a silent
/// runtime fallback (ADR-0058).
/// </summary>
/// <remarks>
/// Column names default to the snake_case of the property name (matching the EF convention); override
/// per property with <see cref="SqlColumnAttribute"/>. The table name defaults to the snake_case of the
/// type name — no pluralization is guessed; pass <paramref name="tableName"/> when the table is named
/// differently.
/// </remarks>
/// <example>
/// <code>
/// [SqlRecord("orders")]
/// public sealed record Order {
///     public required Guid Id { get; init; }
///     public required string CustomerName { get; init; }
///     public string? Note { get; init; }
/// }
///
/// var orders = await connection.QueryAsync(
///     OrderSqlMapper.Instance,
///     $"SELECT {OrderSqlMapper.Columns.All:raw} FROM {OrderSqlMapper.TableName:raw} WHERE id = {id}");
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class SqlRecordAttribute(string? tableName = null) : Attribute {
    /// <summary>The table name; <see langword="null"/> derives the snake_case of the type name.</summary>
    public string? TableName { get; } = tableName;
}

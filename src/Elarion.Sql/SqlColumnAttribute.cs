namespace Elarion.Sql;

/// <summary>
/// Overrides the column name of a property on a <see cref="SqlRecordAttribute">[SqlRecord]</see> type
/// (the default is the snake_case of the property name).
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SqlColumnAttribute(string name) : Attribute {
    /// <summary>The column name as it appears in SQL.</summary>
    public string Name { get; } = name;
}

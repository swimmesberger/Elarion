namespace Elarion.Sql;

/// <summary>
/// Excludes a property of a <see cref="SqlRecordAttribute">[SqlRecord]</see> type from mapping: it is
/// neither read nor bound as a parameter. A <c>required</c> property cannot be ignored (the generated
/// object initializer could not construct the row) — that combination is a compile error (ELSQL003).
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SqlIgnoreAttribute : Attribute;

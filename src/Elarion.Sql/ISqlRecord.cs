namespace Elarion.Sql;

/// <summary>
/// The self-mapping contract a <see cref="SqlRecordAttribute">[SqlRecord]</see> type implements
/// (ADR-0058): the generated partial exposes the row's mapper as a static member, so the query
/// extensions resolve it from the type argument alone — <c>connection.QueryAsync&lt;Order&gt;($"…")</c>,
/// no mapper instance threaded through the call. Resolution is a static field read (the cached
/// singleton), devirtualized under AOT; there is no reflection and no per-row dispatch change.
/// </summary>
/// <remarks>
/// C# cannot locate a separate <c>OrderSqlMapper</c> class from <c>Order</c> alone (no associated
/// types), so the mapper is advertised through this static-abstract member on the row type itself.
/// The generator implements it on a generated partial, so your declaration is untouched; the row type
/// must be declared <c>partial</c> (else <c>ELSQL010</c>). The generated partial also exposes
/// <c>Table</c> and <c>Select</c> fragments for composing hand-written SQL without <c>:raw</c>.
/// </remarks>
public interface ISqlRecord<TSelf> where TSelf : ISqlRecord<TSelf> {
    /// <summary>The row's generated mapper (a cached singleton).</summary>
    static abstract ISqlRowMapper<TSelf> SqlMapper { get; }
}

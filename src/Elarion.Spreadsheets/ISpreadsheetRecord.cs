namespace Elarion.Spreadsheets;

/// <summary>
/// The self-mapping contract a <see cref="SpreadsheetRecordAttribute">[SpreadsheetRecord]</see> class implements through
/// its generated half (ADR-0108), mirroring <c>ISqlRecord&lt;TSelf&gt;</c>: the writer and the reader find the record's
/// mapper from the type argument alone, as a static member read, with no reflection.
/// </summary>
/// <typeparam name="TSelf">The record type itself.</typeparam>
/// <remarks>
/// The generator implements this interface on a generated <c>partial</c> declaration, together with a nested
/// <c>Columns</c> class holding one <see cref="SpreadsheetColumn{T}"/> per column; do not implement it by hand. A member
/// of the record named <c>Columns</c> or <c>SpreadsheetMapper</c> collides with the generated ones (<c>ELSHEET007</c>).
/// </remarks>
public interface ISpreadsheetRecord<TSelf> where TSelf : class, ISpreadsheetRecord<TSelf> {
    /// <summary>The record's generated mapper (a cached singleton).</summary>
    static abstract ISpreadsheetRowMapper<TSelf> SpreadsheetMapper { get; }
}

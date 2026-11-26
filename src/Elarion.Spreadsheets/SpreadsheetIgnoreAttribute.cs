namespace Elarion.Spreadsheets;

/// <summary>
/// Leaves a property of a <see cref="SpreadsheetRecordAttribute">[SpreadsheetRecord]</see> out of the mapping: it has no
/// column and is neither written nor read. Use it for a property whose type the mapping does not support
/// (<c>ELSHEET001</c>).
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class SpreadsheetIgnoreAttribute : Attribute;

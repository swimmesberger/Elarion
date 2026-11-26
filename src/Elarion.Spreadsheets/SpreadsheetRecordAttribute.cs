namespace Elarion.Spreadsheets;

/// <summary>
/// Marks a <c>partial</c> class as a spreadsheet record (ADR-0108): its public instance properties become the columns
/// of a worksheet, and the bundled generator emits the mapping between a record and a row, plus a
/// <see cref="SpreadsheetColumn{T}"/> constant per column under the record's nested <c>Columns</c> class. There is no
/// reflection twin: a property the mapping cannot carry is a build error (<c>ELSHEET001</c>), never a runtime fallback.
/// </summary>
/// <remarks>
/// <para>
/// The type must be a non-nested, non-generic, non-abstract <c>partial</c> class or record class (<c>ELSHEET002</c>,
/// <c>ELSHEET006</c>). Every public instance property with a getter is a column, in declaration order (base type
/// first) unless <see cref="SpreadsheetColumnAttribute.Order"/> moves it; <see cref="SpreadsheetIgnoreAttribute"/>
/// leaves one out. A column's header is the <see cref="SpreadsheetColumnAttribute.Header"/>, else the
/// <c>[Display(Name)]</c> of the property, else the property's name.
/// </para>
/// <para>
/// The attribute accepts structs only so that the generator can report <c>ELSHEET006</c> with an explanation instead
/// of the compiler's generic attribute-target error.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [SpreadsheetRecord("Contacts")]
/// public sealed partial class ContactRow {
///     public required string Name { get; init; }
///     [SpreadsheetColumn("E-mail", Aliases = ["Mail"])]
///     public string? Email { get; init; }
///     [Display(Name = "Customer since")]
///     public DateOnly? CustomerSince { get; init; }
/// }
///
/// await SpreadsheetWorkbook.WriteAsync(stream, contacts, cancellationToken: ct);
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class SpreadsheetRecordAttribute(string? worksheetName = null) : Attribute {
    /// <summary>
    /// The worksheet's name; <see langword="null"/> uses the type's name. It must be 1 to 31 characters long, must not
    /// contain <c>[ ] : * ? / \</c> and must not start or end with an apostrophe (<c>ELSHEET004</c>).
    /// </summary>
    public string? WorksheetName { get; } = worksheetName;
}

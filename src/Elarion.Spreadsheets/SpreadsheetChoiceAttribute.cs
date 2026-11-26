namespace Elarion.Spreadsheets;

/// <summary>
/// Gives a member of an enum that a <see cref="SpreadsheetRecordAttribute">[SpreadsheetRecord]</see> property uses
/// extra spellings that read as the member: an older label, a word without umlauts, a short form. An alias only reads;
/// it is never written and never offered in a column's dropdown list, which show the member's <c>[Display(Name)]</c>,
/// else its name.
/// </summary>
/// <remarks>
/// Aliases match like labels and names, ignoring case and surrounding white space. The labels, names and aliases of
/// two members of one enum must differ ignoring case (<c>ELSHEET008</c>); an alias equal to its own member's label or
/// name changes nothing, and an empty alias is ignored.
/// </remarks>
/// <example>
/// <code>
/// public enum EnergySource {
///     [Display(Name = "Erdgas")]
///     [SpreadsheetChoice(Aliases = ["gas", "naturalgas"])]
///     NaturalGas,
///
///     [Display(Name = "Wärme")]
///     [SpreadsheetChoice(Aliases = ["waerme"])]
///     Heating
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Field, Inherited = false)]
public sealed class SpreadsheetChoiceAttribute : Attribute {
    /// <summary>The spellings that read as the member besides its label and its name.</summary>
    public string[] Aliases { get; set; } = [];
}

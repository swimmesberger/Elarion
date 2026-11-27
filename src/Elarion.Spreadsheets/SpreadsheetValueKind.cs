namespace Elarion.Spreadsheets;

/// <summary>The kind of value a spreadsheet column holds, derived from its property's type.</summary>
public enum SpreadsheetValueKind {
    /// <summary>A <c>string</c>, written as a text cell.</summary>
    Text,

    /// <summary>An integral number (<c>byte</c> to <c>long</c>, signed or not), written as a number cell.</summary>
    Integer,

    /// <summary>A <c>float</c>, <c>double</c> or <c>decimal</c>, written as a number cell.</summary>
    Number,

    /// <summary>A <c>bool</c> without labels, written as a boolean cell.</summary>
    Boolean,

    /// <summary>A <c>DateOnly</c>, written as a date cell.</summary>
    Date,

    /// <summary>A <c>DateTime</c>, written as a date-and-time cell.</summary>
    DateTime,

    /// <summary>A <c>TimeOnly</c>, written as a time cell.</summary>
    Time,

    /// <summary>A <c>DateTimeOffset</c>, written as ISO 8601 text with its offset.</summary>
    DateTimeOffset,

    /// <summary>A <c>Guid</c>, written as a text cell.</summary>
    Guid,

    /// <summary>An enum, or a <c>bool</c> with labels, written as the text of its label.</summary>
    Choice,

    /// <summary>
    /// Text or numbers mixed in one column, each value written as its own type with its own default format and the
    /// column without a format of its own, so a cell typed below the data stays the spreadsheet's "General". Only a
    /// run-time column has it (<see cref="SpreadsheetColumnDefinition.General"/>); a record's property never does.
    /// </summary>
    General
}

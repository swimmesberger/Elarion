namespace Elarion.Calendars;

/// <summary>The options of a write (spec 0004 FR-047, FR-048, FR-052).</summary>
public sealed record CalendarWriteOptions {
    /// <summary>The default of <see cref="ProductId"/>.</summary>
    public const string DefaultProductId = "-//Elarion//Elarion.Calendars//EN";

    /// <summary>The default options.</summary>
    public static CalendarWriteOptions Default { get; } = new();

    /// <summary>The producing program, <c>PRODID</c>, when the calendar header names none.</summary>
    public string ProductId { get; init; } = DefaultProductId;

    /// <summary>
    /// The <c>DTSTAMP</c> of an item that has neither <see cref="CalendarItem.Timestamp"/> nor
    /// <see cref="CalendarItem.LastModified"/>. The writer never reads a clock, so the same input writes the same bytes
    /// (FR-053); without any of the three, writing the item fails.
    /// </summary>
    public DateTimeOffset? Timestamp { get; init; }

    /// <summary>
    /// Whether a <c>VTIMEZONE</c> is written for zones named with an IANA id, which every major program resolves on its
    /// own; zones without one always get theirs. <see langword="true"/> by default (FR-052).
    /// </summary>
    public bool IncludeTimeZones { get; init; } = true;
}

/// <summary>
/// An item the writer cannot write (spec 0004 FR-048, FR-049): no <c>UID</c>, no timestamp, an end before its start, a
/// zone without an id, or a tree with a name or value that cannot be written. This is the application's mistake, not a
/// user's input, so it is an exception.
/// </summary>
public sealed class CalendarWriteException : Exception {
    /// <summary>Creates the exception.</summary>
    public CalendarWriteException(string message, int? itemIndex = null, string? uid = null, string? property = null) : base(message) {
        ItemIndex = itemIndex;
        Uid = uid;
        Property = property;
    }

    /// <summary>The 1-based index of the item among the items added.</summary>
    public int? ItemIndex { get; }

    /// <summary>The item's <c>UID</c>, when it has one.</summary>
    public string? Uid { get; }

    /// <summary>The property at fault.</summary>
    public string? Property { get; }
}

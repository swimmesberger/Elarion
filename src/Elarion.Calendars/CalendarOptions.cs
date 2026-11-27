namespace Elarion.Calendars;

/// <summary>
/// The application's calendar settings, bound by <c>AddElarionCalendars(configuration)</c> from the
/// <see cref="SectionName"/> section, for example <c>Calendars:ReadDefaults:MaxBytes</c>.
/// </summary>
public sealed class CalendarOptions {
    /// <summary>The conventional configuration section: <c>Calendars</c>.</summary>
    public const string SectionName = "Calendars";

    /// <summary>The read options a read uses when it passes none.</summary>
    public CalendarReadOptions ReadDefaults { get; set; } = new();

    /// <summary>The expand options <see cref="CalendarReadResult.Expand"/> uses when it is passed none.</summary>
    public CalendarExpandOptions ExpandDefaults { get; set; } = new();
}

/// <summary>
/// The limits and decoding of a read (spec 0004 FR-005, FR-054, FR-055). File limits (<see cref="MaxBytes"/>,
/// <see cref="MaxComponents"/>) stop the read with one error; per-item limits cost at most that item.
/// </summary>
/// <remarks>
/// The defaults are sized for the 1–10 node tier (ADR-0025). The properties are settable because the configuration
/// binder fills <see cref="CalendarOptions.ReadDefaults"/> in place; derive per-call options with <c>with</c>.
/// </remarks>
public sealed record CalendarReadOptions {
    /// <summary>The default of <see cref="MaxBytes"/>: 16 MiB.</summary>
    public const long DefaultMaxBytes = 16L * 1024 * 1024;

    /// <summary>The default of <see cref="MaxComponents"/>: 50,000.</summary>
    public const int DefaultMaxComponents = 50_000;

    /// <summary>The default of <see cref="MaxLineBytes"/>: 4 MiB.</summary>
    public const int DefaultMaxLineBytes = 4 * 1024 * 1024;

    /// <summary>The default of <see cref="MaxPropertiesPerComponent"/>: 1,000.</summary>
    public const int DefaultMaxPropertiesPerComponent = 1_000;

    /// <summary>The default of <see cref="MaxDepth"/>: 8.</summary>
    public const int DefaultMaxDepth = 8;

    /// <summary>The default of <see cref="FallbackEncoding"/>: <c>windows-1252</c>.</summary>
    public const string DefaultFallbackEncoding = "windows-1252";

    /// <summary>The default of <see cref="MaxLines"/>: 1,000,000.</summary>
    public const int DefaultMaxLines = 1_000_000;

    /// <summary>The default of <see cref="MaxItems"/>: 50,000.</summary>
    public const int DefaultMaxItems = 50_000;

    /// <summary>The default of <see cref="MaxTimeZoneIdLength"/>: 128 characters.</summary>
    public const int DefaultMaxTimeZoneIdLength = 128;

    /// <summary>The most bytes read; a larger input is one <see cref="CalendarErrorCodes.TooLarge"/> error.</summary>
    public long MaxBytes { get; set; } = DefaultMaxBytes;

    /// <summary>The most components of the file; reading stops at the limit and keeps the items before it.</summary>
    public int MaxComponents { get; set; } = DefaultMaxComponents;

    /// <summary>The longest unfolded line; a longer one is dropped with <see cref="CalendarErrorCodes.LineTooLong"/>.</summary>
    public int MaxLineBytes { get; set; } = DefaultMaxLineBytes;

    /// <summary>The most properties of one component; past it the item has no typed value.</summary>
    public int MaxPropertiesPerComponent { get; set; } = DefaultMaxPropertiesPerComponent;

    /// <summary>The deepest nesting of components, the calendar counted as level 1; past it the item has no typed value.</summary>
    public int MaxDepth { get; set; } = DefaultMaxDepth;

    /// <summary>
    /// The encoding of a line whose bytes are not valid UTF-8, a name the runtime or its code-page provider knows
    /// (<c>windows-1252</c>, <c>windows-1250</c>, <c>iso-8859-2</c>, …). An unknown name makes the read throw.
    /// </summary>
    public string FallbackEncoding { get; set; } = DefaultFallbackEncoding;

    /// <summary>
    /// The most logical lines of the file; reading stops at the limit with <see cref="CalendarErrorCodes.TooManyLines"/>
    /// and keeps the items before it.
    /// </summary>
    public int MaxLines { get; set; } = DefaultMaxLines;

    /// <summary>
    /// The most events and to-dos of the file; reading stops at the limit with <see cref="CalendarErrorCodes.TooManyItems"/>
    /// and keeps the items before it. Unlike <see cref="MaxComponents"/>, alarms, time zones and other components do not count.
    /// </summary>
    public int MaxItems { get; set; } = DefaultMaxItems;

    /// <summary>
    /// The longest <c>TZID</c> that is resolved, in characters. A longer one is <see cref="CalendarErrorCodes.UnknownTimeZone"/>
    /// without a lookup, and a <c>VTIMEZONE</c> or <c>X-WR-TIMEZONE</c> with a longer id is not used. Real ids, vendor
    /// prefixes and Outlook display names included, stay far below the default.
    /// </summary>
    public int MaxTimeZoneIdLength { get; set; } = DefaultMaxTimeZoneIdLength;

    /// <summary>
    /// Whether to report what the lenient reader otherwise accepts silently: a recurrence rule with both <c>COUNT</c> and
    /// <c>UNTIL</c> (<see cref="CalendarErrorCodes.InvalidValue"/>, so the item has no value) and a line whose value holds a
    /// control character other than tab (<see cref="CalendarErrorCodes.ControlCharacter"/>, the line is skipped).
    /// <see cref="CalendarErrorCodes.IsStructural"/> names the errors an application that refuses broken files refuses on.
    /// </summary>
    public bool Strict { get; set; }

    internal void Validate() {
        if (MaxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(MaxBytes), MaxBytes, "The byte limit must be positive.");
        if (MaxComponents <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxComponents), MaxComponents, "The component limit must be positive.");
        if (MaxLineBytes <= 0) throw new ArgumentOutOfRangeException(nameof(MaxLineBytes), MaxLineBytes, "The line limit must be positive.");
        if (MaxPropertiesPerComponent <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxPropertiesPerComponent), MaxPropertiesPerComponent, "The property limit must be positive.");
        if (MaxDepth < 2) throw new ArgumentOutOfRangeException(nameof(MaxDepth), MaxDepth, "The depth limit must be at least 2.");
        if (MaxLines <= 0) throw new ArgumentOutOfRangeException(nameof(MaxLines), MaxLines, "The line limit must be positive.");
        if (MaxItems <= 0) throw new ArgumentOutOfRangeException(nameof(MaxItems), MaxItems, "The item limit must be positive.");
        if (MaxTimeZoneIdLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxTimeZoneIdLength), MaxTimeZoneIdLength, "The time-zone id limit must be positive.");
    }
}

/// <summary>The caps and the floating zone of an expansion (spec 0004 FR-028, FR-033, FR-034).</summary>
public sealed record CalendarExpandOptions {
    /// <summary>The default of <see cref="MaxOccurrences"/>: 10,000.</summary>
    public const int DefaultMaxOccurrences = 10_000;

    /// <summary>The default of <see cref="MaxIterations"/>: 100,000.</summary>
    public const int DefaultMaxIterations = 100_000;

    /// <summary>The default of <see cref="MaxCandidates"/>: 1,000,000.</summary>
    public const int DefaultMaxCandidates = 1_000_000;

    /// <summary>The most occurrences one expansion returns; past it, <see cref="CalendarErrorCodes.TooManyOccurrences"/>.</summary>
    public int MaxOccurrences { get; set; } = DefaultMaxOccurrences;

    /// <summary>
    /// The most candidates a rule examines without producing an occurrence; past it,
    /// <see cref="CalendarErrorCodes.RecurrenceLimit"/> on the series.
    /// </summary>
    public int MaxIterations { get; set; } = DefaultMaxIterations;

    /// <summary>
    /// The most candidates one rule of a series builds, across all its periods and within one period, before
    /// <c>BYSETPOS</c>, <c>COUNT</c> and the window apply; past it, <see cref="CalendarErrorCodes.RecurrenceLimit"/> on the
    /// series and the occurrences found so far. It bounds the work of a rule whose <c>BY</c> parts multiply
    /// (<c>BYHOUR</c> × <c>BYMINUTE</c> × <c>BYSECOND</c> on every day of a year) and of a rule with <c>COUNT</c> that
    /// must be counted from a distant <c>DTSTART</c>.
    /// </summary>
    public int MaxCandidates { get; set; } = DefaultMaxCandidates;

    /// <summary>
    /// Whether, of several overrides with one <c>UID</c> and <c>RECURRENCE-ID</c> (an update sent again), only the
    /// latest counts: the highest <c>SEQUENCE</c>, then the latest <c>DTSTAMP</c>, then the later in the input. On by
    /// default, as calendar clients do; off, each override is an occurrence.
    /// </summary>
    public bool ResolveOverrideVersions { get; set; } = true;

    /// <summary>
    /// Whether items with <c>STATUS:CANCELLED</c> are left out: a cancelled override removes its instance, a cancelled
    /// master its series, a cancelled single item itself. Off by default, because the expansion reports what the file
    /// states (CalDAV time-range queries need cancelled instances); an application that shows a calendar turns it on.
    /// </summary>
    public bool SkipCancelled { get; set; }

    /// <summary>
    /// The zone floating times and dates are placed in; <see langword="null"/> compares them as UTC wall-clock times
    /// and leaves their occurrences' UTC times <see langword="null"/>.
    /// </summary>
    public TimeZoneInfo? FloatingTimeZone { get; set; }

    internal void Validate() {
        if (MaxOccurrences <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxOccurrences), MaxOccurrences, "The occurrence limit must be positive.");
        if (MaxIterations <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxIterations), MaxIterations, "The iteration limit must be positive.");
        if (MaxCandidates <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxCandidates), MaxCandidates, "The candidate limit must be positive.");
    }
}

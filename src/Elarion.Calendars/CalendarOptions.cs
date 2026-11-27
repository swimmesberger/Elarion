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

    internal void Validate() {
        if (MaxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(MaxBytes), MaxBytes, "The byte limit must be positive.");
        if (MaxComponents <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxComponents), MaxComponents, "The component limit must be positive.");
        if (MaxLineBytes <= 0) throw new ArgumentOutOfRangeException(nameof(MaxLineBytes), MaxLineBytes, "The line limit must be positive.");
        if (MaxPropertiesPerComponent <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxPropertiesPerComponent), MaxPropertiesPerComponent, "The property limit must be positive.");
        if (MaxDepth < 2) throw new ArgumentOutOfRangeException(nameof(MaxDepth), MaxDepth, "The depth limit must be at least 2.");
    }
}

/// <summary>The caps and the floating zone of an expansion (spec 0004 FR-028, FR-033, FR-034).</summary>
public sealed record CalendarExpandOptions {
    /// <summary>The default of <see cref="MaxOccurrences"/>: 10,000.</summary>
    public const int DefaultMaxOccurrences = 10_000;

    /// <summary>The default of <see cref="MaxIterations"/>: 100,000.</summary>
    public const int DefaultMaxIterations = 100_000;

    /// <summary>The most occurrences one expansion returns; past it, <see cref="CalendarErrorCodes.TooManyOccurrences"/>.</summary>
    public int MaxOccurrences { get; set; } = DefaultMaxOccurrences;

    /// <summary>
    /// The most candidates a rule examines without producing an occurrence; past it,
    /// <see cref="CalendarErrorCodes.RecurrenceLimit"/> on the series.
    /// </summary>
    public int MaxIterations { get; set; } = DefaultMaxIterations;

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
    }
}

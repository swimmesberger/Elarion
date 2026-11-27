namespace Elarion.Calendars;

/// <summary>Where an occurrence's end comes from (spec 0004 FR-035), as the time-range rules of RFC 4791 need it.</summary>
public enum CalendarEndKind {
    /// <summary>The item states its end, <c>DTEND</c> or <c>DUE</c>.</summary>
    Stated,

    /// <summary>The end is the start plus the item's <c>DURATION</c>.</summary>
    Duration,

    /// <summary>The item starts on a date and states no end: it lasts one day.</summary>
    DefaultDay,

    /// <summary>The item starts at a time and states no end: it lasts no time.</summary>
    DefaultZero,
}

/// <summary>One occurrence of an item within an expansion window (spec 0004 FR-035).</summary>
public sealed class CalendarOccurrence {
    /// <summary>Creates an occurrence, for a handler's tests.</summary>
    public CalendarOccurrence(CalendarItem item, CalendarItem? master, CalendarDateTime recurrenceId, CalendarDateTime start,
        CalendarDateTime end, DateTimeOffset? startUtc, DateTimeOffset? endUtc, CalendarEndKind endKind) {
        ArgumentNullException.ThrowIfNull(item);
        Item = item;
        Master = master;
        RecurrenceId = recurrenceId;
        Start = start;
        End = end;
        StartUtc = startUtc;
        EndUtc = endUtc;
        EndKind = endKind;
    }

    /// <summary>The item the occurrence comes from: the series' master, or the override that replaces this instance.</summary>
    public CalendarItem Item { get; }

    /// <summary>The series' master when <see cref="Item"/> is an override, else <see langword="null"/>.</summary>
    public CalendarItem? Master { get; }

    /// <summary>The instance's recurrence id: its original start.</summary>
    public CalendarDateTime RecurrenceId { get; }

    /// <summary>The start as the file states it.</summary>
    public CalendarDateTime Start { get; }

    /// <summary>The end as the file states it, or as computed.</summary>
    public CalendarDateTime End { get; }

    /// <summary>The start in UTC, when it resolves (a floating time needs a floating zone).</summary>
    public DateTimeOffset? StartUtc { get; }

    /// <summary>The end in UTC, when it resolves.</summary>
    public DateTimeOffset? EndUtc { get; }

    /// <summary>Where the end comes from.</summary>
    public CalendarEndKind EndKind { get; }

    /// <inheritdoc/>
    public override string ToString() => $"{Item.Uid}: {Start} – {End}";
}

/// <summary>The occurrences of an expansion, in start order, with its errors (spec 0004 FR-030).</summary>
public sealed class CalendarOccurrences {
    /// <summary>Creates a result.</summary>
    public CalendarOccurrences(IReadOnlyList<CalendarOccurrence> occurrences, IReadOnlyList<CalendarError>? errors = null) {
        ArgumentNullException.ThrowIfNull(occurrences);
        Occurrences = occurrences;
        Errors = errors ?? [];
    }

    /// <summary>The occurrences that overlap the window, in start order.</summary>
    public IReadOnlyList<CalendarOccurrence> Occurrences { get; }

    /// <summary>The errors: a series that hit the iteration cap, or the expansion hitting the occurrence cap.</summary>
    public IReadOnlyList<CalendarError> Errors { get; }
}

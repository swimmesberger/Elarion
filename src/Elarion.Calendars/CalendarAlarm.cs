namespace Elarion.Calendars;

/// <summary>The <c>ACTION</c> of an alarm.</summary>
public enum CalendarAlarmAction {
    /// <summary>An action the model does not name, such as Apple's <c>NONE</c>; no trigger time resolves for it.</summary>
    Unknown,

    /// <summary><c>AUDIO</c>.</summary>
    Audio,

    /// <summary><c>DISPLAY</c>.</summary>
    Display,

    /// <summary><c>EMAIL</c>.</summary>
    Email,
}

/// <summary>What a relative alarm trigger counts from, <c>RELATED</c>.</summary>
public enum CalendarAlarmRelation {
    /// <summary>The start of the occurrence.</summary>
    Start,

    /// <summary>The end of the occurrence.</summary>
    End,
}

/// <summary>
/// A <c>VALARM</c> (spec 0004 FR-038, FR-039), with the alarm <c>UID</c> and <c>ACKNOWLEDGED</c> of RFC 9074.
/// </summary>
public sealed class CalendarAlarm {
    /// <summary>The action, <c>ACTION</c>.</summary>
    public CalendarAlarmAction Action { get; set; } = CalendarAlarmAction.Display;

    /// <summary>The action as written, kept for an <see cref="CalendarAlarmAction.Unknown"/> one.</summary>
    public string? ActionText { get; set; }

    /// <summary>The alarm's own identifier, <c>UID</c> (RFC 9074).</summary>
    public string? Uid { get; set; }

    /// <summary>A trigger relative to the occurrence, negative before it.</summary>
    public TimeSpan? Trigger { get; set; }

    /// <summary>What <see cref="Trigger"/> counts from.</summary>
    public CalendarAlarmRelation Related { get; set; }

    /// <summary>A trigger at a fixed instant, <c>TRIGGER;VALUE=DATE-TIME</c>.</summary>
    public DateTimeOffset? AbsoluteTrigger { get; set; }

    /// <summary>How often the alarm repeats after the first trigger, <c>REPEAT</c>.</summary>
    public int Repeat { get; set; }

    /// <summary>The time between repeats, <c>DURATION</c>.</summary>
    public TimeSpan? RepeatInterval { get; set; }

    /// <summary>The text shown, <c>DESCRIPTION</c>.</summary>
    public string? Description { get; set; }

    /// <summary>The subject of an e-mail alarm, <c>SUMMARY</c>.</summary>
    public string? Summary { get; set; }

    /// <summary>When the alarm was acknowledged, <c>ACKNOWLEDGED</c> (RFC 9074).</summary>
    public DateTimeOffset? Acknowledged { get; set; }

    /// <summary>The properties the model does not map, in file order.</summary>
    public IList<CalendarProperty> AdditionalProperties { get; set; } = new List<CalendarProperty>();

    /// <summary>The sub-components, in file order.</summary>
    public IList<CalendarComponent> AdditionalComponents { get; set; } = new List<CalendarComponent>();

    /// <summary>
    /// The instants the alarm triggers for <paramref name="occurrence"/> (FR-038): the trigger relative to the
    /// occurrence's start or end, or absolute, then each repeat; none for an action other than <c>AUDIO</c>,
    /// <c>DISPLAY</c> and <c>EMAIL</c>, or when the occurrence has no UTC time.
    /// </summary>
    public IReadOnlyList<DateTimeOffset> GetTriggerTimes(CalendarOccurrence occurrence) {
        ArgumentNullException.ThrowIfNull(occurrence);
        if (Action == CalendarAlarmAction.Unknown) return [];
        DateTimeOffset first;
        if (AbsoluteTrigger is { } absolute) {
            first = absolute;
        }
        else if (Trigger is { } trigger) {
            var basis = Related == CalendarAlarmRelation.End ? occurrence.EndUtc : occurrence.StartUtc;
            if (basis is null) return [];
            first = basis.Value + trigger;
        }
        else {
            return [];
        }

        var times = new List<DateTimeOffset> { first };
        if (Repeat > 0 && RepeatInterval is { } interval && interval > TimeSpan.Zero)
            for (var i = 1; i <= Repeat; i++) times.Add(first + interval * i);

        return times;
    }
}

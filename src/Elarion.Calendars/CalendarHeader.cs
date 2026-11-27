namespace Elarion.Calendars;

/// <summary>
/// The calendar's own properties (spec 0004 FR-012, FR-047): what <c>VCALENDAR</c> states about itself, from the
/// RFC 7986 property or its widely read <c>X-WR-</c> twin.
/// </summary>
public sealed class CalendarHeader {
    /// <summary>The producing program, <c>PRODID</c>.</summary>
    public string? ProductId { get; set; }

    /// <summary>The iTIP method, <c>METHOD</c>, read and written as a property only.</summary>
    public string? Method { get; set; }

    /// <summary>The calendar's name, <c>NAME</c> or <c>X-WR-CALNAME</c>.</summary>
    public string? Name { get; set; }

    /// <summary>The calendar's description, <c>DESCRIPTION</c> or <c>X-WR-CALDESC</c>.</summary>
    public string? Description { get; set; }

    /// <summary>The calendar's color, <c>COLOR</c> or <c>X-APPLE-CALENDAR-COLOR</c>.</summary>
    public string? Color { get; set; }

    /// <summary>How often subscribers should refresh, <c>REFRESH-INTERVAL</c> or <c>X-PUBLISHED-TTL</c>.</summary>
    public TimeSpan? RefreshInterval { get; set; }

    /// <summary>Where the calendar is published, <c>URL</c> or <c>SOURCE</c>.</summary>
    public Uri? Url { get; set; }

    /// <summary>
    /// The zone <c>X-WR-TIMEZONE</c> names, when it resolves: a suggestion for showing floating times, which stay
    /// floating (FR-028).
    /// </summary>
    public TimeZoneInfo? SuggestedTimeZone { get; set; }

    /// <summary>The calendar's properties the model does not map, in file order.</summary>
    public IList<CalendarProperty> AdditionalProperties { get; set; } = new List<CalendarProperty>();
}

/// <summary>
/// The components of one <c>UID</c> — a series' master and its overrides, or one item — as a CalDAV server stores
/// them in one resource. Converts implicitly from a <see cref="CalendarEvent"/>, a <see cref="CalendarTodo"/> or a
/// component holding items of one <c>UID</c>.
/// </summary>
public sealed class CalendarObject {
    private CalendarObject(string uid, IReadOnlyList<CalendarComponent> components, IReadOnlyList<CalendarItem> items) {
        Uid = uid;
        Components = components;
        Items = items;
    }

    /// <summary>The <c>UID</c> every component shares.</summary>
    public string Uid { get; }

    /// <summary>The components, without <c>VTIMEZONE</c>s: the writer adds one per zone they use.</summary>
    public IReadOnlyList<CalendarComponent> Components { get; }

    /// <summary>The typed items the object was made from, when it was made from items.</summary>
    public IReadOnlyList<CalendarItem> Items { get; }

    /// <summary>An object of typed items: a master and its overrides, all with the same <c>UID</c>.</summary>
    /// <exception cref="ArgumentException">An item has no <c>UID</c>, or the items' <c>UID</c>s differ.</exception>
    public static CalendarObject FromItems(params IEnumerable<CalendarItem> items) {
        ArgumentNullException.ThrowIfNull(items);
        var list = items.ToList();
        if (list.Count == 0) throw new ArgumentException("A calendar object needs at least one item.", nameof(items));
        var uid = list[0].Uid;
        if (string.IsNullOrEmpty(uid)) throw new ArgumentException("A calendar object's items need a UID.", nameof(items));
        if (list.Any(item => item.Uid != uid))
            throw new ArgumentException($"The items of a calendar object must share one UID; '{uid}' and others were given.", nameof(items));

        return new CalendarObject(uid, [.. list.Select(static item => item.ToComponent())], list);
    }

    /// <summary>
    /// An object of components: either items directly, or a <c>VCALENDAR</c> whose items are taken (its
    /// <c>VTIMEZONE</c>s are left out, the writer adds them).
    /// </summary>
    /// <exception cref="ArgumentException">A component has no <c>UID</c>, or the <c>UID</c>s differ.</exception>
    public static CalendarObject FromComponents(params IEnumerable<CalendarComponent> components) {
        ArgumentNullException.ThrowIfNull(components);
        var list = new List<CalendarComponent>();
        foreach (var component in components) {
            if (component.Name == "VCALENDAR") list.AddRange(component.Components.Where(static c => c.Name != "VTIMEZONE"));
            else if (component.Name != "VTIMEZONE") list.Add(component);
        }

        if (list.Count == 0) throw new ArgumentException("A calendar object needs at least one component.", nameof(components));
        var uids = list.Select(static c => c.FindProperty("UID")?.GetText()).Distinct().ToList();
        if (uids.Count != 1 || string.IsNullOrEmpty(uids[0]))
            throw new ArgumentException("The components of a calendar object must share one UID.", nameof(components));

        return new CalendarObject(uids[0]!, list, []);
    }

    /// <summary>An object of one event.</summary>
    public static implicit operator CalendarObject(CalendarEvent item) => FromItems(item);

    /// <summary>An object of one to-do.</summary>
    public static implicit operator CalendarObject(CalendarTodo item) => FromItems(item);

    /// <summary>An object of a component or a calendar holding items of one <c>UID</c>.</summary>
    public static implicit operator CalendarObject(CalendarComponent component) => FromComponents(component);
}

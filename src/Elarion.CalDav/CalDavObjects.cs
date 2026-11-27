using Elarion.Calendars;
using Elarion.WebDav;

namespace Elarion.CalDav;

/// <summary>How a calendar object becomes a stored object: its name, its bytes, its content type and its time span.</summary>
internal static class CalDavObjects {
    /// <summary>The media type of calendar data as stored and served.</summary>
    public const string ContentType = "text/calendar; charset=utf-8";

    // Floating times have no instant; their span is widened by the largest UTC offsets, so a query in any zone finds them.
    private static readonly TimeSpan FloatingMargin = TimeSpan.FromHours(14);
    private static readonly DateTimeOffset WindowStart = new(1900, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WindowEnd = new(2200, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The member name of <paramref name="uid"/>: the UID itself when it is 1 to 200 of <c>A–Z a–z 0–9 . _ @ -</c>, else the
    /// base32 of its SHA-256; <c>.ics</c> appended. Stable, so re-publishing a UID never moves its URL.
    /// </summary>
    public static string NameOf(string uid) => DavObjectNames.NameOf(uid, ".ics");

    /// <summary>The media type of an object, with the RFC 4791 <c>component</c> parameter of its first component.</summary>
    public static string ContentTypeOf(CalendarObject calendarObject) {
        var name = calendarObject.Components.Count > 0 ? calendarObject.Components[0].Name : "VEVENT";
        return ContentType + "; component=" + name.ToLowerInvariant();
    }

    /// <summary>
    /// Writes <paramref name="calendarObject"/> with the <c>Elarion.Calendars</c> writer and one <c>VTIMEZONE</c> per
    /// <c>TZID</c> (FR-008). No clock value is passed, so equal input writes equal bytes.
    /// </summary>
    /// <exception cref="ArgumentException">The object holds a <c>METHOD</c> property (FR-009).</exception>
    /// <exception cref="CalendarWriteException">An item has no <c>DTSTAMP</c> nor <c>LAST-MODIFIED</c>, or cannot be written.</exception>
    public static async ValueTask<byte[]> WriteAsync(CalendarObject calendarObject, string productId, CancellationToken cancellationToken) {
        foreach (var component in calendarObject.Components)
            if (HoldsMethod(component))
                throw new ArgumentException(
                    $"The calendar object '{calendarObject.Uid}' holds a METHOD property; a published calendar is not an iTIP message.",
                    nameof(calendarObject));

        using var stream = new MemoryStream();
        var writer = CalendarWriter.Create(stream, null, new CalendarWriteOptions { ProductId = productId });
        await using (writer.ConfigureAwait(false)) await writer.AddAsync(calendarObject, cancellationToken).ConfigureAwait(false);
        return stream.ToArray();

        static bool HoldsMethod(CalendarComponent component) =>
            component.FindProperty("METHOD") is not null || component.Components.Any(HoldsMethod);
    }

    /// <summary>
    /// The first and last instant the object's occurrences cover (FR-022 candidates): <see langword="null"/> start for an
    /// object without a time, <see langword="null"/> end for an unbounded series.
    /// </summary>
    public static (DateTimeOffset? Start, DateTimeOffset? End) RangeOf(CalendarReadResult calendar, CalendarExpandOptions caps) {
        var items = calendar.Items.ToList();
        if (items.Count == 0) return (null, null);
        // An item without a start (a to-do with only DUE, a journal) is a candidate of every query.
        if (items.Any(static item => item.Start is null)) return (null, null);

        var floating = items.Any(static item => item.Start is { Kind: CalendarDateTimeKind.Floating or CalendarDateTimeKind.Date });
        var unbounded = items.Any(static item => item.RecurrenceRules.Any(static rule => rule.Count is null && rule.Until is null));
        var options = caps with { FloatingTimeZone = TimeZoneInfo.Utc };
        // An endless series needs only its first occurrence; a window past its latest start finds it without walking on.
        var windowEnd = WindowEnd;
        if (unbounded) {
            var latest = items.Max(static item => item.Start!.Value.ToUtc(TimeZoneInfo.Utc) ?? WindowStart);
            windowEnd = latest.AddDays(400) < WindowEnd ? latest.AddDays(400) : WindowEnd;
        }

        var occurrences = CalendarRecurrence.Expand(items, WindowStart, windowEnd, options);
        if (occurrences.Errors.Count > 0) unbounded = true;

        DateTimeOffset? start = null, end = null;
        foreach (var occurrence in occurrences.Occurrences) {
            if (occurrence.StartUtc is { } s && (start is null || s < start)) start = s;
            var e = occurrence.EndUtc ?? occurrence.StartUtc;
            if (e is { } stop && (end is null || stop > end)) end = stop;
        }

        if (start is null) {
            // Every occurrence lies outside the window or the items expand to nothing: keep them as candidates.
            return (null, null);
        }

        if (floating) {
            start -= FloatingMargin;
            end += FloatingMargin;
        }

        return (start, unbounded ? null : end);
    }
}

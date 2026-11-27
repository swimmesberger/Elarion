using System.Text.RegularExpressions;
using Elarion.Abstractions;
using Elarion.Calendars.Reading;
using Elarion.ContentLines;

namespace Elarion.Calendars;

/// <summary>
/// Writes iCalendar files (spec 0004 FR-043–FR-053): UTF-8 without a byte-order mark, CRLF, lines folded at 75 octets,
/// one <c>VTIMEZONE</c> per zone before the first item that uses it, and the same bytes for the same input. Items are
/// written as they are added, so a calendar of any size streams.
/// </summary>
/// <remarks>
/// The header is written with the first item; <see cref="DisposeAsync"/> writes <c>END:VCALENDAR</c> and flushes. A
/// writer is not thread-safe.
/// </remarks>
/// <example>
/// <code>
/// // A small calendar returned by any handler:
/// return await CalendarWriter.ToFileAsync(events, "waste-collection.ics", new CalendarHeader { Name = "Waste collection" }, ct: ct);
///
/// // A feed streamed by an HTTP endpoint:
/// return CalendarWriter.ToStreamedFile(db.Shifts.AsNoTracking().AsAsyncEnumerable().Select(ToEvent), "shifts.ics");
/// </code>
/// </example>
public sealed partial class CalendarWriter : IAsyncDisposable {
    /// <summary>The media type of an iCalendar file, without its charset; files are served as <c>text/calendar; charset=utf-8</c>.</summary>
    public const string ContentType = "text/calendar";

    private const string FileContentType = "text/calendar; charset=utf-8";
    private const int FlushThreshold = 32 * 1024;

    private readonly ContentLineWriter _lines;
    private readonly CalendarHeader? _header;
    private readonly CalendarWriteOptions _options;
    private readonly HashSet<string> _zonesWritten = new(StringComparer.OrdinalIgnoreCase);
    private bool _started;
    private bool _disposed;
    private bool _faulted;
    private int _itemIndex;

    private CalendarWriter(Stream destination, CalendarHeader? header, CalendarWriteOptions options) {
        _lines = new ContentLineWriter(destination);
        _header = header;
        _options = options;
    }

    /// <summary>Creates a writer over <paramref name="destination"/>, which it does not close.</summary>
    /// <param name="destination">The stream written to.</param>
    /// <param name="calendar">The calendar's own properties, or <see langword="null"/>.</param>
    /// <param name="options">The write options.</param>
    public static CalendarWriter Create(Stream destination, CalendarHeader? calendar = null, CalendarWriteOptions? options = null) {
        ArgumentNullException.ThrowIfNull(destination);
        return new CalendarWriter(destination, calendar, options ?? CalendarWriteOptions.Default);
    }

    /// <summary>Adds an event or a to-do, after the <c>VTIMEZONE</c>s of zones it uses that were not written yet.</summary>
    /// <exception cref="CalendarWriteException">The item cannot be written (FR-048, FR-049).</exception>
    public async ValueTask AddAsync(CalendarItem item, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(item);
        Guard();
        var index = ++_itemIndex;
        // Validation runs before anything is written, so a refused item leaves the calendar intact.
        var component = Prepare(item, index);
        Start();
        foreach (var zone in item.GetTimeZones()) WriteZone(zone, CalendarTimeZones.GetWrittenId(zone));
        WriteComponent(component, index, item.Uid);

        await FlushIfFullAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds a component as it holds its properties and sub-components (FR-046), after a <c>VTIMEZONE</c> for each
    /// <c>TZID</c> it uses that the system knows and that was not written yet.
    /// </summary>
    /// <exception cref="CalendarWriteException">A name or a value cannot be written.</exception>
    public async ValueTask AddAsync(CalendarComponent component, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(component);
        Guard();
        var index = ++_itemIndex;
        Start();
        if (component.Name == "VTIMEZONE") {
            if (component.FindProperty("TZID")?.GetText() is { } tzid && !_zonesWritten.Add(tzid)) return;
        }
        else {
            WriteZonesOf(component);
        }

        WriteComponent(component, index, component.FindProperty("UID")?.GetText());
        await FlushIfFullAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Adds the components of one <c>UID</c>, with one <c>VTIMEZONE</c> per zone they use (spec 0006 FR-008).</summary>
    /// <exception cref="CalendarWriteException">A component cannot be written.</exception>
    public async ValueTask AddAsync(CalendarObject calendarObject, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(calendarObject);
        if (calendarObject.Items.Count > 0) {
            foreach (var item in calendarObject.Items) await AddAsync(item, cancellationToken).ConfigureAwait(false);
            return;
        }

        foreach (var component in calendarObject.Components) await AddAsync(component, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes <c>END:VCALENDAR</c> and flushes; a writer whose write failed is only flushed.</summary>
    public async ValueTask DisposeAsync() {
        if (_disposed) return;
        _disposed = true;
        try {
            if (!_faulted) {
                Start();
                _lines.WriteLine("END:VCALENDAR");
            }

            await _lines.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally {
            _lines.Dispose();
        }
    }

    /// <summary>Writes <paramref name="items"/> as one calendar to <paramref name="destination"/>.</summary>
    public static async ValueTask WriteAsync(Stream destination, IEnumerable<CalendarItem> items, CalendarHeader? calendar = null,
        CalendarWriteOptions? options = null, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(items);
        var writer = Create(destination, calendar, options);
        await using (writer.ConfigureAwait(false)) {
            try {
                foreach (var item in items) await writer.AddAsync(item, cancellationToken).ConfigureAwait(false);
            }
            catch {
                writer._faulted = true;
                throw;
            }
        }
    }

    /// <summary>Writes <paramref name="items"/>, produced one at a time, as one calendar to <paramref name="destination"/>.</summary>
    public static async ValueTask WriteAsync(Stream destination, IAsyncEnumerable<CalendarItem> items, CalendarHeader? calendar = null,
        CalendarWriteOptions? options = null, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(items);
        var writer = Create(destination, calendar, options);
        await using (writer.ConfigureAwait(false)) {
            try {
                await foreach (var item in items.WithCancellation(cancellationToken).ConfigureAwait(false))
                    await writer.AddAsync(item, cancellationToken).ConfigureAwait(false);
            }
            catch {
                writer._faulted = true;
                throw;
            }
        }
    }

    /// <summary>
    /// Writes a component tree in the order it holds (FR-046): a <c>VCALENDAR</c> as it is, anything else inside a
    /// calendar of its own.
    /// </summary>
    public static async ValueTask WriteAsync(Stream destination, CalendarComponent root, CalendarWriteOptions? options = null,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(root);
        if (root.Name != "VCALENDAR") {
            var writer = Create(destination, null, options);
            await using (writer.ConfigureAwait(false)) await writer.AddAsync(root, cancellationToken).ConfigureAwait(false);
            return;
        }

        using var lines = new ContentLineWriter(destination);
        WriteComponentTo(lines, root, null, null);
        await lines.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes <paramref name="items"/> into memory and returns the calendar as a file, for a small export to any transport.</summary>
    public static async ValueTask<ElarionFile> ToFileAsync(IEnumerable<CalendarItem> items, string fileName, CalendarHeader? calendar = null,
        CalendarWriteOptions? options = null, CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        using var stream = new MemoryStream();
        await WriteAsync(stream, items, calendar, options, cancellationToken).ConfigureAwait(false);
        return ToFile(stream, fileName);
    }

    /// <summary>Writes <paramref name="items"/> into memory and returns the calendar as a file, for a small export to any transport.</summary>
    public static async ValueTask<ElarionFile> ToFileAsync(IAsyncEnumerable<CalendarItem> items, string fileName, CalendarHeader? calendar = null,
        CalendarWriteOptions? options = null, CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        using var stream = new MemoryStream();
        await WriteAsync(stream, items, calendar, options, cancellationToken).ConfigureAwait(false);
        return ToFile(stream, fileName);
    }

    /// <summary>
    /// A streamed calendar holding <paramref name="items"/>, written to the response body as they come, for an HTTP
    /// endpoint that exports more than fits a buffered <see cref="ElarionFile"/>.
    /// </summary>
    /// <param name="items">Enumerated once when the response is written, after the handler's pipeline and transaction.</param>
    /// <param name="fileName">The name the client saves the file with.</param>
    /// <param name="calendar">The calendar's own properties.</param>
    /// <param name="options">The write options.</param>
    public static ElarionStreamedFile ToStreamedFile(IAsyncEnumerable<CalendarItem> items, string fileName, CalendarHeader? calendar = null,
        CalendarWriteOptions? options = null) {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        return ElarionStreamedFile.FromWriter(
            (body, cancellationToken) => WriteAsync(body, items, calendar, options, cancellationToken).AsTask(),
            FileContentType) with { FileName = fileName };
    }

    /// <summary>A streamed calendar that <paramref name="write"/> fills; the writer is completed after it returns.</summary>
    /// <param name="fileName">The name the client saves the file with.</param>
    /// <param name="write">Adds the items; runs when the response is written. Do not dispose the writer.</param>
    /// <param name="calendar">The calendar's own properties.</param>
    /// <param name="options">The write options.</param>
    public static ElarionStreamedFile ToStreamedFile(string fileName, Func<CalendarWriter, CancellationToken, ValueTask> write,
        CalendarHeader? calendar = null, CalendarWriteOptions? options = null) {
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        ArgumentNullException.ThrowIfNull(write);
        return ElarionStreamedFile.FromWriter(
            async (body, cancellationToken) => {
                var writer = Create(body, calendar, options);
                try {
                    await write(writer, cancellationToken).ConfigureAwait(false);
                }
                catch {
                    writer._faulted = true;
                    await writer.DisposeAsync().ConfigureAwait(false);
                    throw;
                }

                await writer.DisposeAsync().ConfigureAwait(false);
            },
            FileContentType) with { FileName = fileName };
    }

    private static ElarionFile ToFile(MemoryStream stream, string fileName) =>
        new(stream.GetBuffer().AsMemory(0, (int)stream.Length), FileContentType) { FileName = fileName };

    private void Guard() {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_faulted) throw new InvalidOperationException("An earlier write of this calendar failed; it takes no further items.");
    }

    private async ValueTask FlushIfFullAsync(CancellationToken cancellationToken) {
        if (_lines.BufferedBytes >= FlushThreshold) await _lines.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private CalendarComponent Prepare(CalendarItem item, int index) {
        if (string.IsNullOrEmpty(item.Uid))
            throw new CalendarWriteException($"Item {index} ({item.ComponentName}) has no UID.", index, null, "UID");
        var stamp = item.Timestamp ?? item.LastModified ?? _options.Timestamp
                    ?? throw new CalendarWriteException(
                        $"Item {index} ({item.ComponentName}, UID {item.Uid}) has neither DTSTAMP nor LAST-MODIFIED, and the write options give no timestamp.",
                        index, item.Uid, "DTSTAMP");

        var (endName, end) = item switch {
            CalendarEvent e => ("DTEND", e.End),
            CalendarTodo t => ("DUE", t.Due),
            _ => ("DTEND", null),
        };
        if (item.Start is { } start && end is { } stated && Compare(stated) < Compare(start))
            throw new CalendarWriteException($"Item {index} ({item.ComponentName}, UID {item.Uid}) ends before it starts.", index, item.Uid, endName);

        foreach (var (name, value) in DateTimes(item)) {
            if (value is { Kind: CalendarDateTimeKind.Zoned, TimeZone: { } zone } && string.IsNullOrWhiteSpace(zone.Id))
                throw new CalendarWriteException($"Item {index} ({item.ComponentName}, UID {item.Uid}) uses a time zone without an id.", index,
                    item.Uid, name);
        }

        return CalendarItemMapper.ToComponent(item, stamp);

        static DateTime Compare(CalendarDateTime value) => value.ToUtc(TimeZoneInfo.Utc)?.UtcDateTime ?? value.Value;
    }

    private static IEnumerable<(string Name, CalendarDateTime? Value)> DateTimes(CalendarItem item) {
        yield return ("DTSTART", item.Start);
        if (item is CalendarEvent e) yield return ("DTEND", e.End);
        if (item is CalendarTodo t) yield return ("DUE", t.Due);
        yield return ("RECURRENCE-ID", item.RecurrenceId);
        foreach (var date in item.RecurrenceDates) yield return ("RDATE", date);
        foreach (var date in item.ExceptionDates) yield return ("EXDATE", date);
    }

    private void Start() {
        if (_started) return;
        _started = true;
        _lines.WriteLine("BEGIN:VCALENDAR");
        _lines.WriteLine("VERSION:2.0");
        _lines.WriteProperty(null, "PRODID", null, ContentText.Escape(_header?.ProductId ?? _options.ProductId));
        _lines.WriteLine("CALSCALE:GREGORIAN");
        if (_header is null) return;
        if (_header.Method is not null) _lines.WriteProperty(null, "METHOD", null, _header.Method);
        if (_header.Name is not null) {
            _lines.WriteProperty(null, "NAME", null, ContentText.Escape(_header.Name));
            _lines.WriteProperty(null, "X-WR-CALNAME", null, ContentText.Escape(_header.Name));
        }

        if (_header.Description is not null) {
            _lines.WriteProperty(null, "DESCRIPTION", null, ContentText.Escape(_header.Description));
            _lines.WriteProperty(null, "X-WR-CALDESC", null, ContentText.Escape(_header.Description));
        }

        if (_header.Color is not null) {
            _lines.WriteProperty(null, "COLOR", null, ContentText.Escape(_header.Color));
            if (_header.Color.StartsWith('#')) _lines.WriteProperty(null, "X-APPLE-CALENDAR-COLOR", null, ContentText.Escape(_header.Color));
        }

        if (_header.RefreshInterval is { } refresh) {
            var duration = CalendarValues.FormatDuration(refresh);
            _lines.WriteProperty(null, "REFRESH-INTERVAL", [("VALUE", ["DURATION"])], duration);
            _lines.WriteProperty(null, "X-PUBLISHED-TTL", null, duration);
        }

        if (_header.Url is not null) _lines.WriteProperty(null, "URL", [("VALUE", ["URI"])], _header.Url.OriginalString);
        if (_header.SuggestedTimeZone is { } suggested)
            _lines.WriteProperty(null, "X-WR-TIMEZONE", null, ContentText.Escape(CalendarTimeZones.GetWrittenId(suggested)));
        foreach (var property in _header.AdditionalProperties) WriteProperty(_lines, property, null, null);
    }

    private void WriteZonesOf(CalendarComponent component) {
        foreach (var property in component.Properties) {
            var tzid = property.GetParameter("TZID");
            if (tzid is null || _zonesWritten.Contains(tzid)) continue;
            if (CalendarTimeZones.TryResolve(tzid, out var zone) && zone != TimeZoneInfo.Utc) WriteZone(zone, tzid);
        }

        foreach (var sub in component.Components) WriteZonesOf(sub);
    }

    private void WriteZone(TimeZoneInfo zone, string tzid) {
        if (!_zonesWritten.Add(tzid)) return;
        if (!_options.IncludeTimeZones && CalendarTimeZones.HasIanaId(zone)) return;
        var component = CalendarTimeZones.ToComponent(zone);
        if (!string.Equals(component.FindProperty("TZID")?.GetText(), tzid, StringComparison.Ordinal)) {
            component = component.Clone();
            component.Properties[0] = CalendarProperty.Text("TZID", tzid);
        }

        WriteComponent(component, null, null);
    }

    private void WriteComponent(CalendarComponent component, int? index, string? uid) {
        try {
            WriteComponentTo(_lines, component, index, uid);
        }
        catch (CalendarWriteException) {
            _faulted = true;
            throw;
        }
    }

    private static void WriteComponentTo(ContentLineWriter lines, CalendarComponent component, int? index, string? uid) {
        if (!IsName(component.Name))
            throw new CalendarWriteException($"The component name '{component.Name}' cannot be written.", index, uid, null);
        lines.WriteLine("BEGIN:" + component.Name);
        foreach (var property in component.Properties) WriteProperty(lines, property, index, uid);
        foreach (var sub in component.Components) WriteComponentTo(lines, sub, index, uid);
        lines.WriteLine("END:" + component.Name);
    }

    private static void WriteProperty(ContentLineWriter lines, CalendarProperty property, int? index, string? uid) {
        if (!IsName(property.Name))
            throw new CalendarWriteException($"The property name '{property.Name}' cannot be written.", index, uid, property.Name);
        if (property.RawValue.AsSpan().IndexOfAny('\r', '\n') >= 0)
            throw new CalendarWriteException($"The value of {property.Name} holds a line break; escape it as \\n.", index, uid, property.Name);
        foreach (var parameter in property.Parameters)
            if (!IsName(parameter.Name))
                throw new CalendarWriteException($"The parameter name '{parameter.Name}' of {property.Name} cannot be written.", index, uid,
                    property.Name);

        lines.WriteProperty(null, property.Name,
            property.Parameters.Count == 0 ? null : property.Parameters.Select(static p => (p.Name, (IReadOnlyList<string>)[.. p.Values])),
            property.RawValue);
    }

    private static bool IsName(string name) => NamePattern().IsMatch(name);

    [GeneratedRegex("^[A-Za-z0-9-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}

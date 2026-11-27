using Elarion.Calendars.Reading;
using Elarion.Calendars.TimeZones;
using Elarion.ContentLines;
using Microsoft.Extensions.Options;

namespace Elarion.Calendars;

/// <summary>
/// Reads iCalendar files (spec 0004 FR-004–FR-029, FR-054, FR-055) leniently: every event and to-do with its values,
/// its component and its located errors, the calendar's own properties, and the component tree. It throws only on
/// cancellation and misconfiguration, never because of what the file contains.
/// </summary>
/// <remarks>Registered as a singleton by <c>AddElarionCalendars</c>; it holds no per-read state.</remarks>
/// <example>
/// <code>
/// [Handler]
/// public sealed class ImportCollections(CalendarReader reader) : IHandler&lt;ImportCalendar, Result&lt;int&gt;&gt; {
///     public async ValueTask&lt;Result&lt;int&gt;&gt; HandleAsync(ImportCalendar command, CancellationToken ct) {
///         await using var stream = command.File.OpenReadStream();
///         var result = await reader.ReadAsync(stream, cancellationToken: ct);
///         if (result.HasErrors) return result.ToAppError("file");
///         // map result.Events to the application's entities
///     }
/// }
/// </code>
/// </example>
public sealed class CalendarReader {
    private readonly CalendarOptions _options;

    /// <summary>Creates a reader with the application's options, or the defaults.</summary>
    public CalendarReader(IOptions<CalendarOptions>? options = null) {
        _options = options?.Value ?? new CalendarOptions();
    }

    /// <summary>Reads a calendar from <paramref name="calendar"/>.</summary>
    /// <param name="calendar">The file's bytes; read to its end or to one byte past the size limit, not closed.</param>
    /// <param name="options">The limits and decoding; <see langword="null"/> uses <see cref="CalendarOptions.ReadDefaults"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="InvalidOperationException">The fallback encoding is not known (misconfiguration).</exception>
    public async ValueTask<CalendarReadResult> ReadAsync(Stream calendar, CalendarReadOptions? options = null, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(calendar);
        options ??= _options.ReadDefaults;
        options.Validate();
        var fallback = CodePages.Get(options.FallbackEncoding);

        using var buffer = await ContentBuffer.ReadAsync(calendar, options.MaxBytes, cancellationToken).ConfigureAwait(false);
        if (buffer.TooLarge) {
            return Empty(new CalendarError(CalendarErrorCodes.TooLarge, $"The file is larger than {options.MaxBytes} bytes.") {
                Limit = options.MaxBytes,
            });
        }

        var encoding = ContentTextDecoder.Detect(buffer.Memory.Span, out var transcoded);
        var data = transcoded is not null ? transcoded.AsMemory() : buffer.Memory[encoding.Skip..];
        var decoder = new ContentTextDecoder(fallback, encoding.Transcoded, useCharset: false);
        var builder = new CalendarTreeBuilder(options, decoder);
        builder.Build(data, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return Map(builder, encoding.ByteOrderMarkEncoding, decoder.UsedFallbackEncoding);
    }

    private CalendarReadResult Empty(CalendarError error) =>
        new(new CalendarHeader(), [], [], [error], null, null, false, _options.ExpandDefaults);

    private CalendarReadResult Map(CalendarTreeBuilder builder, string? byteOrderMark, bool usedFallback) {
        var root = builder.Root;
        var fileErrors = builder.FileErrors;
        var header = root is null ? new CalendarHeader() : ReadHeader(root, fileErrors);
        var resolver = root is null ? TimeZoneResolver.System : new TimeZoneResolver(root.Components.Where(static c => c.Name == "VTIMEZONE"));

        var events = new List<CalendarEntry<CalendarEvent>>();
        var todos = new List<CalendarEntry<CalendarTodo>>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var top in builder.TopLevel) {
            if (!top.IsItem) {
                fileErrors.AddRange(top.Errors);
                continue;
            }

            index++;
            CalendarItem? item = null;
            IReadOnlyList<CalendarError> mappingErrors = [];
            if (!top.Limited) CalendarItemMapper.TryMap(top.Component, resolver, out item, out mappingErrors);

            if (top.Component.Name == "VEVENT") {
                var entry = new CalendarEntry<CalendarEvent>(index, top.LineNumber, top.Component, item as CalendarEvent);
                AddErrors(entry.Add, top, mappingErrors, keys);
                events.Add(entry);
            }
            else {
                var entry = new CalendarEntry<CalendarTodo>(index, top.LineNumber, top.Component, item as CalendarTodo);
                AddErrors(entry.Add, top, mappingErrors, keys);
                todos.Add(entry);
            }
        }

        return new CalendarReadResult(header, events, todos, fileErrors, root, byteOrderMark, usedFallback, _options.ExpandDefaults);
    }

    private static void AddErrors(Action<CalendarError> add, TopLevelComponent top, IReadOnlyList<CalendarError> mapping, HashSet<string> keys) {
        foreach (var error in top.Errors) add(error);
        foreach (var error in mapping) add(error);

        // FR-016: an item without UID is fine; the same UID and RECURRENCE-ID twice is reported on the later one.
        var uid = top.Component.FindProperty("UID");
        if (uid is null) return;
        var recurrenceId = top.Component.FindProperty("RECURRENCE-ID");
        var key = top.Component.Name + "\n" + uid.GetText() + "\n" + recurrenceId?.RawValue.Trim() + "\n" + recurrenceId?.GetParameter("TZID");
        if (!keys.Add(key)) {
            add(new CalendarError(CalendarErrorCodes.DuplicateUid, "Another item has the same UID and RECURRENCE-ID.") {
                Property = "UID",
                LineNumber = uid.LineNumber,
                Value = uid.RawValue,
            });
        }
    }

    private static CalendarHeader ReadHeader(CalendarComponent root, List<CalendarError> errors) {
        var header = new CalendarHeader();
        string? name = null, wrName = null, description = null, wrDescription = null, color = null, appleColor = null;
        TimeSpan? refresh = null, ttl = null;
        Uri? url = null, source = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.Properties) {
            if (property.Name is not ("X-WR-TIMEZONE" or "VERSION" or "CALSCALE") && seen.Contains(property.Name)
                && property.Name is "PRODID" or "METHOD" or "NAME" or "X-WR-CALNAME" or "DESCRIPTION" or "X-WR-CALDESC" or "COLOR"
                    or "X-APPLE-CALENDAR-COLOR" or "REFRESH-INTERVAL" or "X-PUBLISHED-TTL" or "URL" or "SOURCE")
                continue;
            seen.Add(property.Name);
            switch (property.Name) {
                case "VERSION" or "CALSCALE": break;
                case "PRODID": header.ProductId = property.GetText(); break;
                case "METHOD": header.Method = property.GetText().Trim(); break;
                case "NAME": name = property.GetText(); break;
                case "X-WR-CALNAME": wrName = property.GetText(); break;
                case "DESCRIPTION": description = property.GetText(); break;
                case "X-WR-CALDESC": wrDescription = property.GetText(); break;
                case "COLOR": color = property.GetText(); break;
                case "X-APPLE-CALENDAR-COLOR": appleColor = property.GetText(); break;
                case "REFRESH-INTERVAL" or "X-PUBLISHED-TTL":
                    if (!property.TryGetDuration(out var interval)) {
                        errors.Add(CalendarItemMapper.Invalid(property, "duration"));
                        break;
                    }

                    if (property.Name == "REFRESH-INTERVAL") refresh = interval;
                    else ttl = interval;
                    break;
                case "URL" or "SOURCE":
                    if (!Uri.TryCreate(property.GetText().Trim(), UriKind.Absolute, out var uri)) {
                        errors.Add(CalendarItemMapper.Invalid(property, "uri"));
                        break;
                    }

                    if (property.Name == "URL") url = uri;
                    else source = uri;
                    break;
                case "X-WR-TIMEZONE":
                    if (header.SuggestedTimeZone is null && CalendarTimeZones.TryResolve(property.GetText(), out var zone)) header.SuggestedTimeZone = zone;
                    else header.AdditionalProperties.Add(property.Clone());
                    break;
                default:
                    header.AdditionalProperties.Add(property.Clone());
                    break;
            }
        }

        header.Name = name ?? wrName;
        header.Description = description ?? wrDescription;
        header.Color = color ?? appleColor;
        header.RefreshInterval = refresh ?? ttl;
        header.Url = url ?? source;
        return header;
    }
}

---
type: Plan
title: "Plan 0004: iCalendar files"
description: "How Elarion.Calendars of spec 0004 is built: project, public surface, content lines, reader, typed model, time zones, recurrence engine, writer, tests and tasks."
generated: { by: claude-code/claude-opus-5-5, at: 2026-11-26T18:00:00Z }
status: draft
---
# Plan 0004: iCalendar files

How [spec 0004](0004-icalendar-files.md) is built. The design and its rejected alternatives are in
[ADR-0109](../docs/decisions/0109-icalendar-model-reader-and-writer.md); this plan holds the implementation decisions
behind it. Names below are the intended ones and may move during review; the FR numbers are fixed.

## Projects

| Project | Target | Holds |
| --- | --- | --- |
| `src/Elarion.Calendars` | `net10.0;net11.0`, `IsAotCompatible` | Public model, reader, writer, recurrence engine, time zones, options, DI registration |

`Elarion.Calendars` references `Elarion.Abstractions` (`ElarionFile`, `ElarionStreamedFile`, `AppError`),
`Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.Options` and
`Microsoft.Extensions.Configuration.Binder` with the configuration-binding generator, as `Elarion.Spreadsheets` does.
No generator project, no third-party package (FR-001). The Windows-1252 fallback comes from
`CodePagesEncodingProvider.Instance.GetEncoding(1252)`, held by the package; `Encoding.RegisterProvider` is never
called (FR-005).

The content-line layer is internal and written so that it can move: no dependency on calendar types, its own limits
type, line numbers on every line. When the vCard package ships second, the layer moves to a shared internal place
(source-shared or a small package), decided then (ADR-0109, decision 2).

## Public surface

```csharp
namespace Elarion.Calendars;

// Registration and options (FR-001, FR-054, FR-055, FR-033, FR-034)
public static IServiceCollection AddElarionCalendars(this IServiceCollection services,
    IConfiguration? configuration = null, Action<CalendarOptions>? configure = null);   // TryAddSingleton<CalendarReader>
public sealed class CalendarOptions {            // section "Calendars"
    public CalendarReadOptions ReadDefaults { get; set; }
    public CalendarExpandOptions ExpandDefaults { get; set; }
}
public sealed record CalendarReadOptions {       // MaxBytes 16 MiB, MaxLineBytes 4 MiB, MaxComponents 50_000,
    long MaxBytes; int MaxLineBytes; int MaxComponents; int MaxPropertiesPerComponent; int MaxDepth;
    int LegacyCodePage;                          // 1252
}
public sealed record CalendarExpandOptions {     // MaxOccurrences 10_000, MaxIterations 100_000
    int MaxOccurrences; int MaxIterations; TimeZoneInfo? FloatingTimeZone;
}

// Reading (FR-004–FR-024)
public sealed class CalendarReader {
    public CalendarReader(IOptions<CalendarOptions>? options = null);
    public ValueTask<CalendarReadResult> ReadAsync(Stream calendar, CalendarReadOptions? options = null, CancellationToken ct = default);
}
public sealed class CalendarReadResult {
    public CalendarHeader Calendar { get; }                       // FR-012
    public CalendarComponent? Root { get; }                       // the VCALENDAR tree (FR-041)
    public IReadOnlyList<CalendarEntry<CalendarEvent>> Events { get; }
    public IReadOnlyList<CalendarEntry<CalendarTodo>> Todos { get; }
    public IReadOnlyList<CalendarError> FileErrors { get; }
    public IEnumerable<CalendarError> Errors { get; }             // file errors, then entries in line order
    public bool HasErrors { get; }
    public CalendarOccurrences Expand(DateTimeOffset from, DateTimeOffset to, CalendarExpandOptions? options = null);
    public AppError ToAppError(string? fieldPath = null, Func<CalendarError, string>? format = null, int maxMessages = 100);
}
public sealed class CalendarEntry<T> where T : CalendarItem {
    public int LineNumber { get; }
    public CalendarComponent Component { get; }
    public T? Value { get; }
    [MemberNotNullWhen(true, nameof(Value))] public bool IsValid { get; }   // FR-014: only time/identity invalidate
    public IReadOnlyList<CalendarError> Errors { get; }
    public void AddError(string code, string message, string? property = null);  // FR-023
}
public sealed class CalendarError {               // FR-020, FR-022; shape of SpreadsheetError
    string Code; string Message; int? LineNumber; string? Component; string? Uid; string? Property;
    string? Value; string? TimeZoneId; long? Limit;
}
public static class CalendarErrorCodes { /* the FR-021 codes as constants, plus Validation = "validation" */ }

// Component tree (FR-041, FR-042), mutable, public constructors
public sealed class CalendarComponent { string Name; IList<CalendarProperty> Properties; IList<CalendarComponent> Components;
    int? LineNumber; CalendarComponent Clone(); }
public sealed class CalendarProperty { string Name; IList<CalendarParameter> Parameters; string RawValue; int? LineNumber;
    /* typed accessors: GetText(), GetTexts(), TryGetDateTime(...), TryGetDuration(...), TryGetPeriod(...),
       TryGetRecurrenceRule(...), TryGetInteger(...); static factories for each type that escape correctly */ }
public sealed class CalendarParameter { string Name; IList<string> Values; }

// Typed model (FR-029, FR-039, FR-040)
public readonly struct CalendarDateTime {         // Kind: Date | Floating | Utc | Zoned
    CalendarDateTimeKind Kind; DateOnly Date; DateTime Value; TimeZoneInfo? TimeZone; string? TimeZoneId;
    static CalendarDateTime FromDate(DateOnly); Floating(DateTime); Utc(DateTimeOffset); Zoned(DateTime, TimeZoneInfo);
    DateTimeOffset? ToUtc(TimeZoneInfo? floatingZone = null);
}
public sealed class CalendarHeader { ProductId; Method; Name; Description; Color; RefreshInterval; Url;
    TimeZoneInfo? SuggestedTimeZone; IList<CalendarProperty> AdditionalProperties; }
public abstract class CalendarItem {              // what VEVENT and VTODO share
    string? Uid; DateTimeOffset? Timestamp; DateTimeOffset? Created; DateTimeOffset? LastModified; int? Sequence;
    CalendarDateTime? Start; CalendarDateTime? RecurrenceId; string? Summary; string? Description; string? Location;
    IList<string> Categories; CalendarClassification? Class; int? Priority; Uri? Url; (double, double)? Geo;
    CalendarOrganizer? Organizer; IList<CalendarAttendee> Attendees; string? Status;
    IList<CalendarRecurrenceRule> RecurrenceRules; IList<CalendarDateTime> RecurrenceDates; IList<CalendarDateTime> ExceptionDates;
    IList<CalendarAlarm> Alarms; string? Color; IList<CalendarConference> Conferences;
    IList<CalendarProperty> AdditionalProperties; IList<CalendarComponent> AdditionalComponents;
    CalendarComponent ToComponent();
}
public sealed class CalendarEvent : CalendarItem { CalendarDateTime? End; TimeSpan? Duration; bool IsAllDay;
    CalendarTransparency? Transparency; static bool TryFromComponent(CalendarComponent, ..., out CalendarEvent?, out errors); }
public sealed class CalendarTodo : CalendarItem { CalendarDateTime? Due; TimeSpan? Duration; DateTimeOffset? Completed;
    int? PercentComplete; ... }
public sealed class CalendarAlarm { string? Uid; CalendarAlarmAction Action /* Audio, Display, Email, Unknown */;
    TimeSpan? Trigger; CalendarAlarmRelation Related; DateTimeOffset? AbsoluteTrigger; int Repeat; TimeSpan? RepeatInterval;
    string? Description; string? Summary; DateTimeOffset? Acknowledged; ...;
    IReadOnlyList<DateTimeOffset> GetTriggerTimes(CalendarOccurrence occurrence); }        // FR-038
public sealed record CalendarRecurrenceRule { /* Frequency, Interval, Count, Until, By* lists, WeekStart;
    static TryParse; ToString writes the canonical form */ }

// Recurrence (FR-030–FR-037)
public static class CalendarRecurrence {
    public static CalendarOccurrences Expand(IEnumerable<CalendarItem> items, DateTimeOffset from, DateTimeOffset to,
        CalendarExpandOptions? options = null);   // groups by UID; overrides matched by RECURRENCE-ID
}
public sealed class CalendarOccurrences { IReadOnlyList<CalendarOccurrence> Occurrences; IReadOnlyList<CalendarError> Errors; }
public sealed class CalendarOccurrence { CalendarItem Item; CalendarItem? Master; CalendarDateTime RecurrenceId;
    CalendarDateTime Start; CalendarDateTime End; DateTimeOffset? StartUtc; DateTimeOffset? EndUtc;
    CalendarEndKind EndKind; }                     // Stated | Duration | DefaultDay | DefaultZero

// Time zones (FR-025–FR-027, FR-051)
public static class CalendarTimeZones {
    public static bool TryResolve(string tzid, out TimeZoneInfo zone);       // normalization + system lookup
    public static CalendarComponent ToComponent(TimeZoneInfo zone);          // VTIMEZONE from transitions since 1970
    public static bool TryFromComponent(CalendarComponent vtimezone, out TimeZoneInfo zone, out CalendarError? error);
}

// Writing (FR-043–FR-053): one type, as SpreadsheetWorkbook
public sealed class CalendarWriter : IAsyncDisposable {
    public const string ContentType = "text/calendar";
    public static CalendarWriter Create(Stream destination, CalendarHeader? calendar = null, CalendarWriteOptions? options = null);
    public ValueTask AddAsync(CalendarItem item, CancellationToken ct = default);
    public ValueTask AddAsync(CalendarComponent component, CancellationToken ct = default);
    public static ValueTask WriteAsync(Stream destination, CalendarHeader? calendar, IEnumerable<CalendarItem> | IAsyncEnumerable<CalendarItem> items, CalendarWriteOptions? options = null, CancellationToken ct = default);
    public static ValueTask WriteAsync(Stream destination, CalendarComponent root, CalendarWriteOptions? options = null, CancellationToken ct = default);
    public static ValueTask<ElarionFile> ToFileAsync(CalendarHeader? calendar, IEnumerable<CalendarItem> items, string fileName, CalendarWriteOptions? options = null, CancellationToken ct = default);
    public static ElarionStreamedFile ToStreamedFile(string fileName, CalendarHeader? calendar, IAsyncEnumerable<CalendarItem> items, CalendarWriteOptions? options = null);
    public static ElarionStreamedFile ToStreamedFile(string fileName, Func<CalendarWriter, CancellationToken, ValueTask> write, CalendarWriteOptions? options = null);
}
public sealed record CalendarWriteOptions { string ProductId; DateTimeOffset? Timestamp; bool IncludeTimeZones = true; }
public sealed class CalendarWriteException : Exception { string? Uid; int? ItemIndex; }
```

`ElarionFile.ContentType` is `text/calendar; charset=utf-8`; the file name is the caller's.

## Content lines and decoding

- Read the stream into a pooled buffer with the byte limit checked while copying (as `SpreadsheetReader` does), so a
  too-large upload stops at the limit (FR-054).
- Decode (FR-005): BOM sniff; otherwise validate the whole buffer as UTF-8 (`Utf8.IsValid`); on failure decode with the
  legacy code page. Decoding happens per logical line after unfolding at byte level, so a fold inside a multi-byte
  sequence is joined before it is decoded (FR-006). For UTF-16 input, unfold on decoded text instead.
- Unfold: a line break is CRLF, LF or CR; a following space or tab continues the line (one whitespace char removed).
  Line numbers are physical line numbers of a logical line's first physical line. Line limit is checked on the unfolded
  length (FR-055).
- Parse a content line with a hand-written scanner: name (any of `A-Za-z0-9-_./`, compared case-insensitively),
  parameters separated by `;`, values separated by `,`, quoted with `"`, RFC 6868 carets decoded, whitespace around
  delimiters skipped, then `:` and the raw value. A line without `:` or with an empty name is malformed (FR-009).
- `BEGIN`/`END` build the tree with a stack bounded by `MaxDepth`; an `END` that matches an outer component closes the
  inner ones (reported once as `calendar.unexpected_end` on the item); end of file closes all (FR-011).
- Before anything else, the first non-empty logical line must be `BEGIN:VCALENDAR`; otherwise `calendar.unreadable`
  (FR-010). Leading HTML, a PDF or binary content therefore fails at once. Several `VCALENDAR`s in one file (some
  exporters concatenate) are read as one; the header comes from the first.

## Typed mapping

- One mapper per component kind turns a `CalendarComponent` into a typed item and back, table-driven: property name →
  value type → member. Unmapped properties and sub-components go to `AdditionalProperties`/`AdditionalComponents` in
  order; `ToComponent` writes mapped properties in the fixed order (FR-046) and then the additional ones in their
  original order, then `VALARM`s, then additional components.
- The time-placing properties of FR-014 are read first; a failure there produces the entry without a value. Other
  properties failing to convert are recorded as `calendar.invalid_value` and skipped (FR-015).
- Value repairs of FR-017 live in one date-time parser used for every date-time value, with tests per repair.
- `X-WR-CALNAME`, `X-WR-CALDESC`, `X-WR-TIMEZONE`, `X-PUBLISHED-TTL` and `X-APPLE-CALENDAR-COLOR` feed the header when
  the RFC 7986 property is absent; on write, FR-047 pairs are emitted and the `X-` twins are not kept a second time.
- `X-MICROSOFT-CDO-ALLDAYEVENT` (FR-019) converts midnight date-times to dates during mapping; the property stays in
  `AdditionalProperties`.

## Time zones

- **Resolve (FR-025, FR-026).** Normalize; try `TimeZoneInfo.TryFindSystemTimeZoneById` (IANA and, with ICU, Windows
  ids); otherwise find the `VTIMEZONE` with that `TZID` (case-insensitive; collected in a first pass, so forward
  references work) and convert it. Results are cached per read. `UTC`, `GMT`, `Etc/UTC` and `Z` resolve to
  `TimeZoneInfo.Utc` without the system.
- **`VTIMEZONE` to `TimeZoneInfo`.** Expand each `STANDARD`/`DAYLIGHT` observance (its `DTSTART`, `RRULE`, `RDATE`) with
  the package's own recurrence engine into onsets with offsets, from its first `DTSTART` through 2100, giving a
  transition list. Build a custom `TimeZoneInfo` (`CreateCustomTimeZone`, id = the `TZID`) with one
  `AdjustmentRule` per year of the list (fixed-date transitions of that year, `BaseUtcOffsetDelta` for standard-offset
  changes). Task 1 spikes this, including a southern-hemisphere zone and Outlook's 1601 observances; if
  `AdjustmentRule`'s constraints refuse real definitions, the fallback is an internal zone type behind
  `CalendarDateTime` with the public `TimeZone` property still a `TimeZoneInfo` built best effort, decided in task 1.
- **`TimeZoneInfo` to `VTIMEZONE` (FR-051).** For each year 1970–2100 find the year's offset changes (binary search
  between daily samples of `GetUtcOffset`), describe each as month, rule (`n`th or last weekday, or day of month), local
  time, offset before and after, and daylight flag (`IsDaylightSavingTime`). Consecutive years with the same
  description become one observance with `RRULE:FREQ=YEARLY;BYMONTH=…;BYDAY=…` and `UNTIL`; the run that reaches 2100
  has no `UNTIL`; a lone transition is an observance without a rule. A zone without transitions is one `STANDARD`. The
  result is cached per zone id. The `TZID` written is the IANA id (`TryConvertWindowsIdToIanaId` for a Windows id;
  the id itself when that fails) (FR-050).

## Recurrence engine

- One iterator per rule following RFC 5545 section 3.3.10: generate period sets by `FREQ` and `INTERVAL`, expand or
  limit by each `BY` part in the RFC's order (the expand/limit table), apply `BYSETPOS` per period, sort, then `COUNT`
  and `UNTIL`. Computation on wall-clock `DateTime`s in the series' zone; conversion to UTC per candidate with the DST
  rules of FR-032 (gap: shift by the gap; fold: first offset). Invalid dates (February 30) are skipped, not clamped.
- `DTSTART` is the first instance even when the rule does not match it; `COUNT` counts it. `RDATE`s are added, `EXDATE`s
  removed after counting; duplicates collapse. A date `EXDATE` against date-time instances removes the instance on that
  date (FR-017).
- The series merge combines rules, `RDATE`s, `EXDATE`s and overrides with a min-heap, so the window's end stops all
  generators; candidates before `from` are skipped without being materialized. `MaxIterations` counts candidates
  examined since the last produced instance per rule (FR-034); `MaxOccurrences` counts produced occurrences per call
  (FR-033).
- Overrides replace the instance whose recurrence id they carry, even when the override moves it into or out of the
  window; an override whose recurrence id is not an instance is an extra occurrence (RFC behavior of common clients).
  Items whose master is missing (overrides only) expand as single occurrences. Duplicate masters (FR-016) each expand;
  overrides attach to the first.
- Window comparison for floating and date values uses `FloatingTimeZone`, or UTC wall-clock when none is given, in
  which case `StartUtc` and `EndUtc` stay null for them (FR-028).
- End per occurrence (FR-035, FR-036, FR-018): `DTEND`/`DUE` offset from the master's start (Stated), `DURATION`
  (Duration; nominal days and weeks in wall-clock time, exact durations in elapsed time), date start default one day
  (DefaultDay), date-time default zero (DefaultZero).

## Writer

- `CalendarWriter` writes `BEGIN:VCALENDAR`, `VERSION`, `PRODID`, `CALSCALE:GREGORIAN`, `METHOD` when set, the header
  pairs of FR-047, then items as they are added, then `END:VCALENDAR` on dispose. A content-line writer emits UTF-8 into
  a pooled buffer, folding at 75 octets on rune boundaries with CRLF + space, and flushes to the stream asynchronously.
- Before writing an item, the writer collects the zones of its date-times; for each zone not yet written, it writes
  `CalendarTimeZones.ToComponent(zone)` unless `IncludeTimeZones` is false and the id is IANA (FR-052). Writing a
  `VTIMEZONE` between items is valid RFC 5545 and satisfies "defined before use".
- `DTSTAMP` per FR-048; validation per FR-049 throws `CalendarWriteException`.
- Determinism (FR-053): no clock, no culture (invariant formatting everywhere), fixed property order, `Dictionary`
  iteration never reaches the output.
- `ToStreamedFile` uses `ElarionStreamedFile.FromWriter`; faults close what was written, as `SpreadsheetWorkbook` does.

## Tests

All in `tests/Elarion.Tests`, named after the acceptance scenarios.

- `Calendars/ContentLines` — unfolding (CRLF/LF/CR, space/tab, fold inside a multi-byte character), decoding ladder,
  RFC 6868, escaping, malformed lines, limits, line numbers.
- `Calendars/Reading` — the typed mapping per property, the leniency of FR-014/FR-015, the repairs of FR-017,
  all-day rules, errors and `ToAppError`, application errors. A corpus under `Calendars/Fixtures/` with one synthetic
  file per producer shape in the research note (Google feed, Outlook export with Windows zones and HTAB folding,
  "Customized Time Zone", Apple export with structured location and alarms, iCloud holiday feed, waste feed with BOM
  and no folding, timed waste feed with alarms, school-holiday feed), written by hand in the producers' form; no
  third-party file is committed.
- `Calendars/TimeZones` — resolution order, normalization, file fallback, unknown ids; `VTIMEZONE` → `TimeZoneInfo` for
  Outlook, Apple and Google definitions and a southern-hemisphere zone; `TimeZoneInfo` → `VTIMEZONE` → `TimeZoneInfo`
  agrees with the system zone at every hour 1970–2040 for a set of zones (Europe/Vienna, America/New_York,
  Australia/Sydney, America/Sao_Paulo, Asia/Kolkata, Pacific/Chatham, Europe/Moscow for its standard-offset changes).
  Tests that need system zones skip with a reason when tzdata is missing.
- `Calendars/Recurrence` — every example of RFC 5545 section 3.8.5.3; the recurrence test cases of python-dateutil
  (dual Apache-2.0/BSD licensed, ported with attribution); DST gap and fold cases; the bugs Ical.Net fixed in 2025–26
  (#1017 hourly across DST, #1016 start in a gap, #1008 UNTIL with BY parts that never match, #856 YEARLY with
  BYSETPOS, #985 repeated BYMONTH, #1035, #1037) as regression oracles; both caps with a time bound.
- `Calendars/Writing` — byte-exact golden files for a typed calendar and a tree, folding at boundaries, escaping, the
  exceptions of FR-048/FR-049, `VTIMEZONE` placement, determinism, streamed files; every written file read back
  (FR-056).
- AOT: the `EdgeTelemetry` sample (already `PublishAot`) gains a small calendar export and import of its readings'
  maintenance windows, so the existing CI NativeAOT publish fails on any IL2xxx/IL3xxx warning from the package
  (SC-004).
- `tests/Elarion.Benchmarks` — reading the 400 KB community-calendar shape, expanding 10,000 occurrences, writing
  100,000 events to a null stream (SC-005).
- SC-002 (Google, Outlook, Apple import a written file correctly) is a manual check recorded in the pull request.

## Documentation

- `docs/capabilities/calendars.mdx` (and `meta.json`): import with preview, feeds, time zones and the tzdata note for
  slim containers, recurrence windows and caps, the error codes; `docs/reference/packages.mdx`,
  `docs/reference/configuration.mdx` (`CalendarOptions`); `GLOSSARY.md` (calendar item, already added with the ADR);
  `CHANGELOG.md`, with a note whenever the written bytes change between versions.

## Tasks

1. **Time-zone spike** — `VTIMEZONE` → `TimeZoneInfo` against Outlook, Apple, Google and southern-hemisphere
   definitions, and `TimeZoneInfo` → `VTIMEZONE` round trip; decides the fallback in "Time zones".
2. **Package and content lines** — FR-001, FR-005–FR-011, FR-054, FR-055.
3. **Component tree and writer core** — FR-041–FR-046, FR-053, FR-056 for trees.
4. **Typed model and reading** — FR-004, FR-012–FR-024, FR-039, FR-040, FR-042.
5. **Time zones** — FR-003, FR-025–FR-029, FR-050–FR-052.
6. **Recurrence engine** — FR-030–FR-037.
7. **Typed writing and file tiers** — FR-043, FR-047–FR-049, FR-056.
8. **Alarms** — FR-038.
9. **AOT sample, benchmarks** — FR-002, SC-004, SC-005.
10. **Documentation** — capability page, references, changelog.

Task 1 goes first; 2 then 3 and 4; 5 needs 4 and the spike; 6 needs 2 only (it works on rules and wall-clock times) and
is the largest, so it can start in parallel with 3–5; 7 needs 3–5; 8 needs 6; 9 and 10 close.

## Risks

- **The recurrence engine is the bulk of the work and the main correctness risk.** The test oracles above, and the
  caps, are the mitigation; RFC errata 4271 and the python-dateutil semantics settle the ambiguous cases. Where the
  RFC and the major clients differ (an override whose recurrence id is not an instance), the plan follows the
  clients and the capability page says so.
- **`TimeZoneInfo.CreateCustomTimeZone` may refuse real `VTIMEZONE` definitions** (overlapping rules, deltas outside its
  range); task 1 decides before the model depends on it.
- **Results depend on the system's tzdata**, which differs between hosts and is absent in slim containers. Tests skip
  with a reason where a zone is missing; the capability page tells NativeAOT users to install tzdata.
- **The content-line layer will move** when the vCard package ships; keeping it free of calendar types makes that a
  file move, not a redesign.

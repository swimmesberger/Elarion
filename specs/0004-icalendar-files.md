---
type: Specification
title: "Spec 0004: iCalendar files"
description: "What Elarion.Calendars must do: read .ics files leniently into every item with its errors, keep every property other programs wrote, expand recurrences within a window, resolve time zones, and write calendars and feeds deterministically, under NativeAOT."
generated: { by: claude-code/claude-opus-5-5, at: 2026-11-26T17:30:00Z }
status: stable
---
# Spec 0004: iCalendar files

This spec describes the new opt-in package `Elarion.Calendars`. Why it is shaped this way, and what was rejected, is
in [ADR-0109](../docs/decisions/0109-icalendar-model-reader-and-writer.md); what real files look like is in the
[research note](../docs/research/2026-11-26-icalendar-libraries-and-producers.md). It shares its design and its
content-line layer with `Elarion.Contacts` ([spec 0005](0005-vcard-contacts.md)).

## Intent

A developer reads a calendar file a user uploaded or a feed the application downloaded, and gets every event and to-do
in it with its times resolved, its recurrences expanded within a window, and every error coded and located, so the
application can show a preview and commit what the person confirms; files from Google, Outlook, Apple and municipal
or school calendars read without the application repairing them first. The same developer publishes the application's
dates as an `.ics` file or a feed that calendar programs subscribe to, byte-identical for the same data. Properties the
package does not model, such as those other programs add, are kept through a read and a write. The model, the reader,
the writer and the recurrence expansion are the ones a CalDAV package builds on, and an application published with
NativeAOT uses all of it without warnings.

## User scenarios

### Import events from an uploaded calendar with a preview (P1)

- Given an uploaded `.ics` file with 120 all-day waste-collection events, when a handler reads the uploaded file's
  stream, then the result holds 120 events in file order, each with its summary, its start date, its end date and the
  line it starts on, and no error.
- Given a file in which the third event has a `DTSTART` of `2026-13-45`, when it is read, then the result holds every
  event, the third without a typed value and with a `calendar.invalid_value` error naming line, `UID`, `DTSTART` and the
  value, and the others with their typed values.
- Given a file in which one event has `PRIORITY:high`, when it is read, then that event has its typed value without a
  priority, the property is still in its component, and the result reports a `calendar.invalid_value` error for it.
- Given a read result, when the application adds its own error `import.date_in_past` to an event, then the event
  keeps its typed value, and the error appears among the result's errors, located like the package's own, and in its
  `AppError`.
- Given a file without any `UID` on its events, when it is read, then every event is read, without an error.

### Report errors in the application's language (P1)

- Given a result with errors, when the handler converts it with `ToAppError("file")`, then it gets an
  `AppError.Validation` whose field errors are keyed by `file` and hold one message per error in English.
- Given a formatter that words errors by their code and data, when the handler passes it to `ToAppError`, then each
  message is the formatter's.
- Given a file with 250 broken events, when the result is converted, then the `AppError` holds 100 messages and one
  that says how many more there are.

### Files as real producers write them (P1)

- Given a file with a UTF-8 byte-order mark and lines longer than 75 octets without folding, when it is read, then
  its events read as if it had neither.
- Given a file in Windows-1252 with `Müll` in a summary, when it is read without options, then the summary reads
  `Müll` and the result says that the fallback encoding was used.
- Given a UTF-8 file in which one line holds a Windows-1252 byte, when it is read, then every other line reads as
  UTF-8 and that line reads with the fallback encoding.
- Given a file with LF line ends and lines folded with a tab, when it is read, then it reads like the same file with
  CRLF and spaces.
- Given an Outlook export with `DTSTART;TZID="W. Europe Standard Time":20261201T090000` and a matching `VTIMEZONE`,
  when it is read, then the event starts at 09:00 in Central European Time.
- Given an event whose `TZID` is `/mozilla.org/20070129_1/Europe/Berlin`, when it is read, then it starts in
  `Europe/Berlin`.
- Given an event with `CREATED:20260101T000000ZT000000Z`, a `DTSTART;TZID=Europe/Vienna:20260505T`, or a
  `DTSTAMP;VALUE=DATE:19760401`, when it is read, then the value is repaired and no error is reported.
- Given an event from older Exchange with `X-MICROSOFT-CDO-ALLDAYEVENT:TRUE` and start and end at midnight, when it is
  read, then it is an all-day event.
- Given an all-day event without `DTEND`, when it is read, then it lasts one day.
- Given a file that is an HTML page, when it is read, then the result holds one `calendar.unreadable` error and no item.

### Time zones (P1)

- Given an event with `TZID=Europe/Vienna` on a system that knows the zone, when it is read, then its start converts
  to UTC through the system's zone, even if the file's `VTIMEZONE` for it differs.
- Given an event with `TZID=Customized Time Zone` and a `VTIMEZONE` of that id with a standard and a daylight rule,
  when it is read, then its start converts to UTC through that definition.
- Given an event whose `TZID` neither the system nor the file defines, when it is read, then the event has no typed
  value and a `calendar.unknown_time_zone` error naming the id.
- Given an event with a floating start (`DTSTART:20261201T090000`), when it is read, then its start is floating, and
  when its occurrences are expanded with the zone `Europe/Vienna`, then the occurrence starts at 08:00 UTC.
- Given a calendar with `X-WR-TIMEZONE:Europe/Berlin`, when it is read, then the result names that zone as the
  calendar's suggested zone and floating times stay floating.

### Expand recurring events within a window (P1)

- Given an event with `RRULE:FREQ=WEEKLY;BYDAY=TU;COUNT=10`, an `EXDATE` on its third instance and an override of its
  fifth instance moved by two hours, when its occurrences between its start and a year later are expanded, then there
  are nine, the fifth with the override's start and summary.
- Given `RRULE:FREQ=MONTHLY;BYDAY=TU;BYSETPOS=2` from an Outlook file, when it is expanded, then every occurrence is
  the second Tuesday of its month.
- Given a daily event at 02:30 in `Europe/Vienna`, when it is expanded across the last Sunday of March, then the
  occurrence on that day starts at 03:30, and across the last Sunday of October it starts at 02:30 at the first offset.
- Given an event repeating every minute without end, when it is expanded over ten years, then the expansion stops at
  10,000 occurrences with a `calendar.too_many_occurrences` error and returns the occurrences found.
- Given `RRULE:FREQ=YEARLY;BYMONTH=2;BYMONTHDAY=30`, when it is expanded over a hundred years, then it ends with a
  `calendar.recurrence_limit` error and no occurrence, within a bounded time.
- Given a recurring to-do with `DUE`, when it is expanded, then each occurrence ends at its due time.
- Given an event with `DURATION:PT1H`, one with neither `DTEND` nor `DURATION`, and an all-day one without `DTEND`,
  when they are expanded, then each occurrence says whether its end was stated, computed from a duration, or derived.

### Publish a calendar or a feed (P1)

- Given events with `UID`s, summaries, all-day dates and times in `Europe/Vienna`, when a handler returns them as an
  `ElarionFile`, then the caller receives a `text/calendar` file that Google Calendar, Outlook and Apple Calendar import
  with the same dates and times.
- Given the same events and the same timestamp written twice by the same package version, when the two files are
  compared, then they are byte-identical.
- Given an event without `DTSTAMP` and `LAST-MODIFIED`, and no timestamp in the write options, when it is written, then
  the write fails with an exception naming the event.
- Given an event without `UID`, or with an end before its start, when it is written, then the write fails with an
  exception naming the event.
- Given events in `Europe/Vienna` and `America/New_York`, when they are written, then the file contains one
  `VTIMEZONE` for each, before the first event that uses it, and each converts the events' times correctly in the
  importing programs.
- Given a summary with `;`, `,`, a newline and non-ASCII characters longer than 75 octets, when it is written and read
  back, then it reads unchanged, and no line of the file is longer than 75 octets.
- Given 100,000 events produced one at a time, when an HTTP-only handler returns them as a streamed file, then the
  first bytes reach the client before the last event is produced.
- Given a calendar name and a refresh interval, when the calendar is written, then both appear in their standard and
  their widely read form.

### Properties of other programs survive a round trip (P2)

- Given an event from Apple Calendar with `X-APPLE-STRUCTURED-LOCATION` and parameters holding quoted text with
  commas, when it is read and written back, then the property, its parameters and its value are unchanged.
- Given an event with an unknown component inside it, when it is read and written back, then the component is
  unchanged.

### Work on the component tree (P2)

- Given a read calendar, when code walks its component tree, then it finds every component, property and parameter
  the file states, with their names, values as written and line numbers.
- Given a component tree built in code, or a copy of a read one with a property removed, when it is written, then the
  file holds the components and properties in the order given.
- Given a typed event, when it is converted to its component and back, then the result equals the event.

### Alarms and to-dos (P2)

- Given an event with a `VALARM` that triggers 15 minutes before its start and repeats twice every 5 minutes, when its
  trigger times for an occurrence are resolved, then there are three, 15, 10 and 5 minutes before that occurrence.
- Given an Apple alarm with `ACTION:NONE` and the trigger `19760401T005545Z`, when it is read, then it is an alarm with
  an action the model names as unknown, and no trigger time is resolved for it.
- Given a to-do with `DUE`, `STATUS:COMPLETED`, `PERCENT-COMPLETE:100` and `COMPLETED`, when it is read and written
  back, then each property reads unchanged.

### Hostile files are bounded (P2)

- Given a file larger than the size limit, when it is read, then the result holds one `calendar.too_large` error
  without the file having been read beyond the limit.
- Given a file with more components than the limit, when it is read, then reading stops with one
  `calendar.too_many_components` error naming the limit, and the result keeps the items read before it.
- Given an event whose `DESCRIPTION` line is longer than the limit, when the file is read, then the event has its typed
  value without the description, its entry holds a `calendar.line_too_long` error naming the limit, and the events
  after it read.
- Given an event with more properties than the limit, or with components nested deeper than the limit inside it, when
  the file is read, then that event has no typed value and a `calendar.too_many_properties` or
  `calendar.nesting_too_deep` error, and the events after it read.
- Given a file with more lines, or more events and to-dos, than the limit, when it is read, then reading stops with one
  `calendar.too_many_lines` or `calendar.too_many_items` error naming the limit, and the result keeps the items read
  before it; alarms and time zones do not count as items.
- Given an event whose `TZID` is a vendor prefix of tens of thousands of segments ending in an IANA id, when the file is
  read without an id limit, then the id resolves in linear time; with the default limit of 128 characters, a longer id
  is `calendar.unknown_time_zone` without a lookup.
- Given many reads of files with distinct unknown `TZID`s, when they are done, then no process-wide cache holds any of
  them.
- Given a yearly rule with every month, day, hour, minute and second, when it is expanded, then the expansion stops at
  the candidate cap with `calendar.recurrence_limit` within the time and memory of SC-005, instead of building the
  31 million candidates of one year.
- Given a monthly or yearly rule without `COUNT` from a `DTSTART` centuries before the window, when it is expanded, then
  it skips ahead to the window and finds the window's occurrences under a small candidate cap.
- Given a cancelled token, when a calendar is expanded, then the expansion throws `OperationCanceledException`.

### Overrides, exception rules and strict reads (P3)

- Given two overrides of one instance (an update sent again), when the series is expanded, then only the one with the
  highest `SEQUENCE`, then the latest `DTSTAMP`, then the later one gives an occurrence; with the option off, both do.
- Given an override with `STATUS:CANCELLED`, when the series is expanded with cancelled items skipped, then its
  instance gives no occurrence; by default the override is an occurrence like any other.
- Given a weekly series with `EXRULE:FREQ=WEEKLY;INTERVAL=2`, when it is expanded, then every other instance is
  excluded except `DTSTART`, and the rule is the item's `ExceptionRules` and is written back.
- Given a rule with both `COUNT` and `UNTIL`, or a value with a control character, when the file is read strictly, then
  the rule is `calendar.invalid_value` (the item has no value) and the line is skipped with
  `calendar.control_character`; a lenient read accepts both.

## Requirements

- FR-001: The feature MUST ship as the opt-in package `Elarion.Calendars`, which MUST NOT depend on ASP.NET Core,
  EF Core, `Elarion.Blobs` or a third-party library, and MUST be registered with
  `AddElarionCalendars(IConfiguration?, Action<CalendarOptions>?)`, bound from the configuration section `Calendars`.
- FR-002: Reading, expanding and writing MUST NOT use reflection, so that an application published with NativeAOT
  gets no trimming or AOT warning from the package.
- FR-003: The public API MUST use only .NET's own date and time types (`DateOnly`, `DateTime`, `DateTimeOffset`,
  `TimeSpan`, `TimeZoneInfo`) for times and time zones.
- FR-004: Reading MUST accept a stream and return a result, and MUST throw only on cancellation and on
  misconfiguration.
- FR-005: Reading MUST decide the encoding of the whole file from a byte-order mark (UTF-8, UTF-16 LE, UTF-16 BE) or
  from `BEGIN` encoded as UTF-16 without one, and otherwise MUST decode each logical line on its own, as strict UTF-8
  when its bytes are valid UTF-8, else with the fallback encoding the read options name (`FallbackEncoding`),
  Windows-1252 by default, without registering an encoding provider globally. The result MUST say whether the
  fallback decoded any line (`UsedFallbackEncoding`) and which encoding a byte-order mark chose. This is the decoding
  ladder of `Elarion.Contacts` without its `CHARSET` step.
- FR-006: Reading MUST accept CRLF, LF and CR line ends, MUST unfold continuation lines that start with a space or a
  tab by removing that one character, and MUST unfold before decoding so that a fold inside a multi-byte character
  does not damage it.
- FR-007: Reading MUST accept lines of any length up to the line limit, property and parameter names in any case,
  spaces around `:`, `;` and `=`, and quoted parameter values in any parameter.
- FR-008: Reading MUST decode parameter values per RFC 6868 and text values per RFC 5545 escaping, and MUST read an
  unescaped `,` in a single-valued text property as part of the text.
- FR-009: A line that is not a content line MUST be skipped and reported as `calendar.malformed_line` on the item that
  contains it, or as a file error outside every item, without invalidating the item.
- FR-010: A file that does not contain a `VCALENDAR` component MUST be exactly one `calendar.unreadable` error.
- FR-011: A file that ends inside a component MUST close the open components, keep the items read, and report one
  `calendar.unexpected_end` error.
- FR-012: The result MUST hold the calendar's own properties: product id, method, name (from `NAME` or
  `X-WR-CALNAME`), description, color, refresh interval, and the zone `X-WR-TIMEZONE` names when it resolves.
- FR-013: The result MUST hold every `VEVENT` and `VTODO` of the calendar in file order, each with its 1-based index,
  its component, the line it starts on, its typed value as `Value` when it has one with `HasValue` saying so, and its
  errors.
- FR-014: An item MUST have no typed value exactly when one of `DTSTART`, `DTEND`, `DURATION`, `DUE`, `RRULE`,
  `RDATE`, `EXDATE`, `RECURRENCE-ID`, or a time zone one of them names, cannot be read, when a `VEVENT` has no
  `DTSTART` (`calendar.required`), or when the item exceeds the properties or nesting limit of FR-055.
- FR-015: Any other property of an item whose value cannot be read MUST be left out of the typed value, kept in the
  component, and reported as `calendar.invalid_value` without invalidating the item.
- FR-016: An item without `UID` MUST be read without an error, and two items with the same `UID` and the same
  `RECURRENCE-ID` (or none) MUST be reported as `calendar.duplicate_uid` on the later one without invalidating it.
- FR-017: Reading MUST repair, silently: a date-time without seconds, a date-time truncated after `T` (midnight), a
  repeated time suffix such as `T000000ZT000000Z`, a `TZID` on a date value (ignored), a date where `DTSTAMP`, `CREATED`
  or `LAST-MODIFIED` expect a date-time (midnight UTC), and a date `EXDATE` against date-time starts (it excludes the
  occurrence on that date).
- FR-018: An event with a date `DTSTART` and neither `DTEND` nor `DURATION` MUST last one day, and an event with a
  date-time `DTSTART` and neither MUST last no time.
- FR-019: An event whose `DTSTART` and `DTEND` are both at midnight and that carries
  `X-MICROSOFT-CDO-ALLDAYEVENT:TRUE` or `X-MICROSOFT-MSNCALENDAR-ALLDAYEVENT:TRUE` MUST read as an all-day event.
- FR-020: Every error MUST carry a stable code, the data its wording needs, and an English default message.
- FR-021: The codes MUST be `calendar.unreadable`, `calendar.too_large`, `calendar.line_too_long`,
  `calendar.too_many_components`, `calendar.too_many_properties`, `calendar.nesting_too_deep`,
  `calendar.malformed_line`, `calendar.unexpected_end`, `calendar.required`, `calendar.invalid_value`,
  `calendar.unknown_time_zone`, `calendar.duplicate_uid`, `calendar.too_many_occurrences` and
  `calendar.recurrence_limit`.
- FR-022: An error about an item MUST carry the item's index and the line it starts on, the component name and the
  `UID` when there is one, and, when it concerns a property, the property's name, its line and its value as written;
  an error about a value MUST carry the expected kind, an error about a limit the limit, and
  `calendar.unknown_time_zone` the time-zone id. These are the location fields `Elarion.Contacts` errors carry too.
- FR-023: The application MUST be able to add its own errors to an item with its own code, and optionally a property
  and a line (`AddError(code, message, property?, lineNumber?)`), and they MUST appear in the result's errors and in
  its `AppError` like the package's own; an added error MUST NOT remove the typed value.
- FR-024: The result MUST convert to an `AppError.Validation` whose field errors are keyed by a field path the caller
  names, worded by a formatter the caller may pass, with at most 100 messages by default plus one message that counts
  the rest.
- FR-025: A `TZID` MUST be normalized by removing surrounding quotes and spaces, a vendor prefix ending in an IANA id
  (`/mozilla.org/20070129_1/Europe/Berlin`, `/freeassociation.sourceforge.net/Europe/Berlin`), and a leading
  `(UTC±hh:mm) ` display prefix, in time linear in the id's length. A read MUST NOT resolve an id longer than the
  time-zone id limit of the read options, 128 characters by default; such an id is `calendar.unknown_time_zone`.
  A process-wide cache MUST hold only ids the system resolves, never an id that did not resolve.
- FR-026: A `TZID` MUST resolve through the system's time zones when the system knows the normalized id, as an IANA
  or a Windows id, and otherwise through the file's `VTIMEZONE` of that id, wherever in the file it stands.
- FR-027: A `TZID` that resolves neither way MUST be a `calendar.unknown_time_zone` error on every item that uses it.
- FR-028: A floating time MUST stay floating in the typed value and MUST be converted to UTC only in a zone the caller
  passes.
- FR-029: The model MUST tell a date, a floating date-time, a UTC date-time and a zoned date-time apart, and a zoned
  date-time MUST expose its `TimeZoneInfo` and the `TZID` as the file wrote it.
- FR-030: Expansion MUST take the items of one series or of a whole result, a window `[from, to)` and expand options,
  and MUST return the occurrences that overlap the window in start order with any errors.
- FR-031: Expansion MUST implement RFC 5545 recurrence for the Gregorian calendar: every `FREQ`, every `BY` part,
  `BYSETPOS`, `WKST`, `COUNT`, `UNTIL`, `INTERVAL`, `RDATE` values of every type, `EXDATE`, and overrides matched by
  `UID` and `RECURRENCE-ID`, and the RFC 2445 `EXRULE`, whose instances other than `DTSTART` are excluded. Repeated
  values of a `BY` list MUST count once, and a list with more items than the part has distinct values MUST NOT parse.
- FR-032: A recurrence MUST move a local time that does not exist forward by the length of the gap, and MUST place an
  ambiguous local time at its first offset.
- FR-033: Expansion MUST stop at the maximum number of occurrences of the expand options, 10,000 by default, with a
  `calendar.too_many_occurrences` error and the occurrences found before it.
- FR-034: Expansion MUST stop at the maximum number of periods examined without producing an occurrence, 100,000
  by default, and at the maximum number of candidates a rule builds, 1,000,000 by default, counted as they are built
  within a period, with a `calendar.recurrence_limit` error on the series. A rule without `COUNT` MUST start a few
  periods before the window, for every frequency. Expansion MUST take a cancellation token, checked per series and per
  period.
- FR-035: Each occurrence MUST carry its recurrence id, its start and end as the file states them and in UTC when they
  resolve, whether its end was stated, computed from a duration or derived, and the item it comes from.
- FR-036: Expansion MUST expand recurring to-dos, with `DUE` (or `DTSTART` plus `DURATION`) as the end.
- FR-037: An override with `RANGE=THISANDFUTURE` MUST be read as an override of its one instance, and a rule with an
  `RSCALE` other than `GREGORIAN` MUST be a `calendar.invalid_value` error.
- FR-038: An alarm MUST resolve its trigger times for an occurrence, relative to its start or end or absolute, with
  its `REPEAT` and `DURATION`, and MUST resolve none for an action other than `AUDIO`, `DISPLAY` and `EMAIL`.
- FR-039: The typed model MUST cover events, to-dos and alarms with the RFC 5545 properties of each, the RFC 7986
  properties of a calendar and an event, and the alarm `UID` and `ACKNOWLEDGED` of RFC 9074.
- FR-040: A typed item MUST keep every property and sub-component it does not model, and writing it MUST write them
  unchanged.
- FR-041: The component tree MUST hold every component, property and parameter of the file in order, with values as
  written and line numbers, including unknown and `X-` ones.
- FR-042: Components and properties MUST be constructible and copyable in code, and a typed item MUST convert to its
  component and back without loss.
- FR-043: Writing MUST accept a typed calendar and a component tree, and MUST write to a stream, an `ElarionFile` and an
  `ElarionStreamedFile` served as `text/calendar; charset=utf-8`, and item by item for content not held in memory,
  through the shape `Elarion.Spreadsheets` and `Elarion.Contacts` use: an instance writer created over a stream that
  items are added to, and static `WriteAsync`, `ToFileAsync(items, fileName, …)`, `ToStreamedFile(items, fileName, …)`
  and `ToStreamedFile(fileName, write, …)`.
- FR-044: Writing MUST produce UTF-8 without a byte-order mark, CRLF line ends, and lines of at most 75 octets folded
  with CRLF and a space without splitting a character.
- FR-045: Writing MUST escape text values, encode parameter values per RFC 6868, and quote parameter values that
  contain `:`, `;` or `,`.
- FR-046: Writing MUST write a typed item's properties in a fixed order and its sub-components after its properties,
  and a component tree in the order it holds.
- FR-047: Writing MUST write `VERSION:2.0` and a product id, by default one naming the package, and the calendar's name
  and refresh interval both as `NAME` and `X-WR-CALNAME`, and as `REFRESH-INTERVAL` and `X-PUBLISHED-TTL`.
- FR-048: Writing MUST write `DTSTAMP` from the item, else from its `LAST-MODIFIED`, else from the write options, and
  MUST fail with a `CalendarWriteException` when none is given.
- FR-049: Writing MUST fail with a `CalendarWriteException` for an event or to-do without `UID`, an end before its
  start, and a zoned time whose zone has no id; the exception MUST carry the item's index (`ItemIndex`), its `UID`
  when it has one, and the property (`Property`).
- FR-050: Writing MUST write a zoned time with the zone's IANA id where the system can name one, and otherwise with
  its id.
- FR-051: Writing MUST write one `VTIMEZONE` per zone used, before the first item that uses it, whose observances
  reproduce the zone's offsets from 1970 on, with runs that follow one rule written as one recurring observance and
  the last rule left open.
- FR-052: A write option MUST leave out the `VTIMEZONE` of zones written with an IANA id, and by default every zone
  MUST be included.
- FR-053: The same input with the same timestamp MUST produce byte-identical output with the same package version.
- FR-054: Reading MUST stop with one `calendar.too_large` error at the byte limit of the read options, 16 MiB by
  default, without reading further.
- FR-055: Reading MUST stop with one `calendar.too_many_components` error at the number of components of the read
  options (50,000), keeping the items read before it. The per-item limits MUST cost at most that item and reading MUST
  continue: an unfolded line longer than the line limit (4 MiB) MUST be dropped with `calendar.line_too_long`, and an
  item with more properties than the limit (1,000) or components nested deeper than the limit (8) MUST have no typed
  value and a `calendar.too_many_properties` or `calendar.nesting_too_deep` error, its component holding what was read
  within the limits.
- FR-056: A calendar written by the package MUST read back into items equal to the ones written.
- FR-057: Reading MUST stop with one `calendar.too_many_lines` error at the number of lines of the read options
  (1,000,000), and with one `calendar.too_many_items` error at the number of events and to-dos (50,000), keeping the
  items read before it, and MUST check the cancellation token per item while mapping.
- FR-058: Of several overrides with one `UID` and `RECURRENCE-ID`, expansion MUST by default keep only the one with the
  highest `SEQUENCE`, then the latest `DTSTAMP`, then the later one; an expand option MUST turn this off.
- FR-059: An expand option, off by default, MUST leave out items with `STATUS:CANCELLED`: a cancelled override removes
  its instance, a cancelled master its series, a cancelled single item itself.
- FR-060: A strict read option, off by default, MUST report a rule with both `COUNT` and `UNTIL` as
  `calendar.invalid_value` and skip a line whose value holds a control character other than tab with
  `calendar.control_character`; the error codes MUST say which of them are structural (about the file's form).

## Success criteria

- SC-001: The corpus of the research note (Google, Outlook, Apple, waste-collection and school-holiday files) reads
  with no error the files do not deserve, and every event lands on the date and time the producing program shows.
- SC-002: A calendar written by the package imports into Google Calendar, Outlook and Apple Calendar with every event
  at its intended date and time, including a recurring event across a daylight-saving change.
- SC-003: Expansion produces the occurrences of every example in RFC 5545 section 3.8.5.3, and of a published
  recurrence test suite, exactly.
- SC-004: An application published with NativeAOT that reads, expands and writes calendars gets zero trimming or AOT
  warnings from the package.
- SC-005: No file of at most the default size makes a read or an expansion run longer than a few seconds or allocate
  more than a small multiple of the limits.
- SC-006: An application can word every error of an import in its own language without parsing a message.
- SC-007: An application that hand-wrote a calendar import with encoding detection and a preview can replace its
  parsing code with the package and keep its behavior.

## Out of scope

- iTIP processing (accepting, declining, updating invitations) and iMIP mail; `METHOD` is read and written as a
  property only.
- Free-busy computation and `VFREEBUSY`, `VJOURNAL` and `VAVAILABILITY` as typed models; they are reachable as
  component trees.
- The semantics of `RANGE=THISANDFUTURE`, non-Gregorian calendars (RFC 7529), vCalendar 1.0, xCal and jCal.
- Downloading subscriptions over HTTP, gzip decoding included; the application downloads and passes the stream.
- Deciding what an import does with an item (create, update, skip, de-duplicate, match a re-import).
- Splitting one item into several (several waste types in one summary); the application does it with the typed value.
- CalDAV, WebDAV and the protocol rules of a calendar object resource; they belong to the CalDAV package.
- Generated typed records mapping an application's own type to an event.

## Decisions

- [ADR-0109](../docs/decisions/0109-icalendar-model-reader-and-writer.md): the package, the fixed model over a
  lossless tree, the own reader, writer and recurrence engine, the lenient rule, time zones, writing.
- [ADR-0108](../docs/decisions/0108-spreadsheet-records.md): the design language of file-format packages this package
  follows, and the result, error and `ToAppError` shapes it reuses.
- [ADR-0110](../docs/decisions/0110-vcard-contacts.md): the sibling format package whose entry, error, limit and writer
  shapes, decoding ladder and content-line layer this package shares.
- [ADR-0098](../docs/decisions/0098-streamed-files-for-http-only-endpoints.md) and
  [ADR-0105](../docs/decisions/0105-uploaded-files-are-handler-parameters.md): the streamed-file and uploaded-file tiers
  an export and an import use.
- [ADR-0104](../docs/decisions/0104-previewable-commands.md): an import with a preview is a previewable command.

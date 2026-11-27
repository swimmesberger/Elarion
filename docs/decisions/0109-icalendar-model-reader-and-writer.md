---
type: Architecture Decision Record
title: "ADR-0109: iCalendar — a fixed model over a lossless component tree, with Elarion's own reader, writer and recurrence engine"
description: "Elarion.Calendars reads .ics files leniently into a result that keeps every item, its component tree and coded errors, writes them deterministically, expands recurrences within a window under caps, and resolves time zones through TimeZoneInfo with the file's VTIMEZONE as fallback, without an outside library."
generated: { by: claude-code/claude-opus-5-5, at: 2026-11-26T17:00:00Z }
status: stable
---
# ADR-0109: iCalendar — a fixed model over a lossless component tree, with Elarion's own reader, writer and recurrence engine

- Status: Accepted
- Date: 2026-11-26
- Related: [ADR-0017](0017-dependency-light-core.md) (heavy defaults live in opt-in siblings),
  [ADR-0025](0025-distributed-scheduler-coordination.md) (the 1–10 node tier the defaults are sized for),
  [ADR-0039](0039-binary-file-responses.md) (the small-file tier), [ADR-0098](0098-streamed-files-for-http-only-endpoints.md)
  (streamed files), [ADR-0104](0104-previewable-commands.md) (preview, then commit),
  [ADR-0105](0105-uploaded-files-are-handler-parameters.md) (uploaded files), [ADR-0108](0108-spreadsheet-records.md)
  (the reference file-format package), [ADR-0110](0110-vcard-contacts.md) (contacts, whose design and content-line
  layer this shares), the research note
  [iCalendar in .NET](../research/2026-11-26-icalendar-libraries-and-producers.md), and
  [spec 0004](../../specs/0004-icalendar-files.md).

## Context

Applications import calendars and publish them. An application that imports a municipality's waste-collection
calendar by hand shows what an import needs: the user uploads an `.ics` file, the application analyzes it, shows a
preview of the dates it found with their problems, and commits what the person confirms; along the way it guesses the
text encoding, because the RFC's UTF-8 is not what every producer sends. Other applications publish feeds (a team's
dates, a booking calendar) that Google, Outlook and Apple subscribe to. A CalDAV package, designed in parallel, will
store, filter and expand calendar objects and needs the same parsing, writing and recurrence logic underneath.

The research note has the survey; three findings shape the decision:

- **No .NET iCalendar library passes the AOT bar.** Ical.Net is maintained and widely used, but declares neither AOT
  nor trimming compatibility, deserializes through reflection, and an open pull request shows that a fully trimmed
  build replaces a parsed `UID` with a random GUID without an error. It depends on NodaTime and is about to break its
  API in a major version whose recurrence engine still collects DST and counting fixes.
- **Real files break the RFC in a small, known set of ways.** Unfolded or character-folded lines, a byte-order mark,
  bare line feeds, Windows and quoted time-zone ids, ids without a `VTIMEZONE` ("Customized Time Zone"),
  `X-WR-TIMEZONE` as a default, dates where date-times belong, truncated values, and UIDs that change on every
  download. Tolerant readers agree on how to handle each.
- **Recurrence is unbounded by nature.** A three-line rule describes millions of occurrences, and rule combinations
  exist that iterate long without producing one. Every mature engine bounds both.

Unlike a spreadsheet, a calendar's schema is fixed by RFC 5545: an application does not choose the fields of an event,
it chooses what to do with them. And unlike a spreadsheet, a calendar is often edited by several programs in turn, each
adding properties of its own (`X-APPLE-*`, `X-MICROSOFT-CDO-*`, `X-MOZ-*`) that the others must keep.

## Decision

`Elarion.Calendars` is an opt-in package that depends on `Elarion.Abstractions` and `Microsoft.Extensions`
abstractions only. It does not depend on ASP.NET Core, EF Core, `Elarion.Blobs` or an outside library: it reads from
and writes to streams, and the file tiers carry them. It is `IsAotCompatible`; registration is
`AddElarionCalendars(IConfiguration?, Action<CalendarOptions>?)`, bound from the configuration section `Calendars`
through the configuration-binding generator, with limits and read defaults in one options bag and per-call options of
the same type, as `Elarion.Spreadsheets` and `Elarion.Contacts` do.

1. **A fixed model over a lossless component tree, no generator.** The design language of file-format packages asks
   for generated typed records; a calendar is where it does not fit, because the RFC, not the application, fixes the
   fields. The package has two public layers. The **component tree** (`CalendarComponent` with its properties,
   parameters and sub-components, and the line each starts on) keeps everything a file states: unknown and `X-`
   properties, parameter order, component order, values as written. The **typed model** (`CalendarEvent`,
   `CalendarTodo`, `CalendarAlarm`, attendees, the organizer, recurrence rules, date-times) is a view over a component
   that reads the properties it models and keeps the rest, so an item read and written again loses nothing another
   program put there. `VJOURNAL`, `VFREEBUSY`, `VAVAILABILITY` and unknown components are available as trees only.
   The tree is built as easily as it is read: components and properties have public constructors and can be copied
   with changes, and a typed item converts to its component and back, so a caller can assemble, prune or rewrite a
   calendar and hand it to the writer.
   An application maps a typed item to its own type in a few lines of handler code; a generator would save little and
   could not express a series, an override or a time zone better than the model. Consequently there is no
   `[ValidationRoot]`, no DataAnnotations run and no diagnostic prefix; the application validates its own type after
   mapping.
2. **Elarion owns the reader, the writer, the recurrence engine and the time-zone conversion.** iCalendar is text: a
   content-line grammar, some twenty value types, a recurrence rule and a time-zone component. The parts a library
   would contribute are exactly the parts the research found broken under trimming or still unsettled, and taking
   Ical.Net would bring NodaTime as a second dependency and its types toward the public API. Public types are .NET's
   own: `DateOnly`, `DateTime`, `DateTimeOffset`, `TimeSpan`, `TimeZoneInfo`. The content-line layer (line ends,
   folding, parameters, escaping, RFC 6868, the decoding ladder, the code-page cache, limits, line numbers) is the same
   grammar vCard uses. It is internal and source-shared: one folder, `src/Shared/ContentLines/`, compiled into this
   package and `Elarion.Contacts`, with no property names in it; what differs is a rule set each package passes in.
   For iCalendar, unfolding removes exactly one leading space or tab of a continuation line (vCard removes any
   indentation), and there are no groups, bare parameters, `CHARSET` or quoted-printable.
3. **The read result keeps every item.** Reading returns the calendar's own properties (name, product, method, the
   default time zone of `X-WR-TIMEZONE`) and every event and to-do in file order, each with its index, its component,
   the line it starts on, its typed view as `Value` when it has one, and its errors, so a preview can list every item,
   including those with errors. `HasValue` says whether the typed view is set, and says so to the compiler; it is the
   entry shape of `Elarion.Contacts`, and it is not `IsValid`, which in `Elarion.Spreadsheets` means a record and no
   errors, because here an error need not take the value away (decision 5). Items with the same `UID` form a series (one
   master, overrides by `RECURRENCE-ID`); the result groups them for expansion but does not merge them, and a missing
   `UID` is not an error, because files from municipalities omit or regenerate them. Re-import matching is the
   application's, on `UID` where the producer keeps it stable and on start, end and summary where it does not.
4. **A user's file is input, not infrastructure.** A file that is not a calendar, a limit exceeded, a value that does
   not convert, a time zone that does not resolve: each is an error in the result with a stable `calendar.*` code, an
   English default message and the location both line formats share (the item's index and the line it starts on, the
   line, the property and its value as written, the expected kind, the limit), and on top the component name, the
   `UID` and the time-zone id. Reading throws only on cancellation and misconfiguration. The application adds its own
   errors to an item (`AddError(code, message, property?, lineNumber?)`, with its own code prefix), and `ToAppError`
   converts the result to an `AppError.Validation` keyed by the field path the handler names, worded by
   a formatter and capped at 100 messages plus one that counts the rest, as `Elarion.Spreadsheets` does. An import is a
   previewable command (ADR-0104) like any other; the package has no preview of its own.
5. **Only what places an item in time makes it invalid.** An item has no typed view when its identity in time is broken:
   `DTSTART`, `DTEND`, `DURATION`, `DUE`, `RRULE`, `RDATE`, `EXDATE`, `RECURRENCE-ID`, or a time zone one of them names,
   or when it exceeds a per-item limit (decision 10). Any other property whose value does not convert (a `PRIORITY` of
   `high`, a malformed `GEO`) is left out of the typed view, kept in the tree, and reported as an error that does not
   invalidate the item. This is where the package departs from spreadsheets, whose every cell belongs to the
   application's record: a calendar file carries many properties the application never asked for, and an event should
   not disappear from a preview because a program wrote one of them badly.
6. **One lenient rule, the same for every application.** Text is decoded by the ladder both text formats use: a
   byte-order mark (UTF-8, UTF-16), or a file that starts with `BEGIN` in UTF-16 without one, decides the whole file;
   otherwise each logical line is decoded on its own, as strict UTF-8 when its bytes are valid UTF-8, else with the
   fallback encoding the read options name (`FallbackEncoding`, Windows-1252 by default, held by the package, never
   registered globally), and the result says when the fallback was used (`UsedFallbackEncoding`). iCalendar has no
   `CHARSET` parameter, so the ladder's `CHARSET` step does not apply. The reader accepts CRLF, LF and CR line ends,
   folding with a space or a tab, lines of any length up to the limit, property and parameter names in any case, spaces
   around `:`, `;` and `=`, quoted parameter values anywhere, and `VTIMEZONE`s after their first use. It repairs the
   known value faults: a missing seconds field, a value truncated after `T` (read as midnight), a duplicated time suffix
   (`…T000000ZT000000Z`), a `TZID` on a date value (ignored), a date-only `DTSTAMP`, a date `EXDATE` against date-time
   starts (excludes the day's instance). An all-day event is a date `DTSTART` with an exclusive `DTEND`; without `DTEND`
   or `DURATION` it lasts one day, and a date-time event without either lasts no time; midnight-to-midnight date-times
   with `X-MICROSOFT-CDO-ALLDAYEVENT:TRUE` read as all-day. Lines that are not content lines are skipped and reported. A
   repair is silent; what cannot be repaired is an error.
7. **Time zones come from the system, then from the file.** A `TZID` is normalized (quotes and surrounding space
   removed, a vendor prefix such as `/mozilla.org/20070129_1/Europe/Berlin` reduced to its IANA id, a
   `(UTC+01:00) …` display prefix removed) and resolved through `TimeZoneInfo`, which knows IANA ids and, with ICU,
   Windows ids. An id the system does not know resolves to the file's `VTIMEZONE` of that id, converted into a
   custom `TimeZoneInfo`; that covers "Customized Time Zone", Windows ids under invariant globalization and containers
   without tzdata. An id that neither resolves is `calendar.unknown_time_zone` on the items that use it. The system wins
   over the file for an id it knows, because producers' `VTIMEZONE`s are often truncated (Outlook's has one rule and no
   history); the file wins only when it is all there is. **Floating times stay floating**: the model never guesses a
   zone; expansion and conversion take the zone the caller passes, and the result offers `X-WR-TIMEZONE` as a
   suggestion, not a default.
8. **Recurrence expansion is in the package, always within a window and under two caps.** It implements RFC 5545
   recurrence completely for the Gregorian calendar: every frequency, every `BY` part, `BYSETPOS`, `WKST`, `COUNT` and
   `UNTIL`, `RDATE`s (dates, date-times and periods), `EXDATE`s, and overrides by `RECURRENCE-ID`. Local times that do
   not exist move forward by the gap (erratum 4271), and ambiguous ones take the first offset. Expansion always takes a
   `[from, to)` window, stops at an occurrence cap (10,000 by default) and at a cap on candidates examined without a
   match (100,000 by default, as libical and the Rust `rrule` crate use), and reports either as a coded error on the
   series together with the occurrences found so far. Each occurrence carries its recurrence id, its start and end as
   the file states them and in UTC when its zone resolves, whether its end was stated (`DTEND`, `DUE`), computed from a
   `DURATION`, or derived by the RFC's default (one day for a date, none for a date-time), and the item it comes
   from, master or override. Recurring to-dos expand like events, with `DUE` as the end. An alarm resolves its trigger
   times for an occurrence (relative to its start or end, or absolute, with its repetitions). An override
   with `RANGE=THISANDFUTURE` is read as an override of its one instance; non-Gregorian `RSCALE` (RFC 7529) is an
   error.
9. **Writing is deterministic and interoperable.** The writer takes a typed calendar or a component tree and writes
   UTF-8 without a byte-order mark, CRLF, lines folded at 75 octets without splitting a character, text escaped and
   parameter values encoded per RFC 6868 and quoted when they need it. Typed items are written in a fixed property order
   with sub-components after the properties; trees in the order they hold. Every event needs a `UID`. `DTSTAMP` is the
   item's own, else its `LAST-MODIFIED`, else the write options' timestamp; with none of them the write fails, because a
   stamp taken from the clock would change the bytes, and with them a CalDAV ETag, on every write. A zoned time is
   written with its IANA id (a Windows id is converted where the system can), and before the first item that uses a zone
   the writer emits its `VTIMEZONE`, built from the zone's actual transitions since 1970, runs of transitions that
   follow one rule collapsed into a recurring observance and the rule in force last left open; building from transitions
   rather than from `TimeZoneInfo.GetAdjustmentRules` avoids the per-platform rule shapes and the southern-hemisphere
   trap the research found. A write option leaves the `VTIMEZONE`s out for IANA ids, for CalDAV clients that ask for
   time zones by reference (RFC 7809); the default includes them, which Outlook, iOS and Thunderbird need. A calendar's
   name and refresh interval are written in both forms (`NAME` and `X-WR-CALNAME`, `REFRESH-INTERVAL` and
   `X-PUBLISHED-TTL`). The same input with the same timestamp produces the same bytes for one package version; a release
   that changes the bytes says so in the changelog, because every published ETag changes with them. A value the format
   cannot hold (an event without `UID`, an end before its start, a `TimeZoneInfo` without an id) fails the write with a
   `CalendarWriteException` carrying the item's index, its `UID` and the property. The writer has the shape of
   `SpreadsheetWorkbook` and `ContactWriter`: an instance created over a stream that items are added to one at a time,
   for feeds too large to build in memory, and static `WriteAsync`, `ToFileAsync(items, fileName, …)`,
   `ToStreamedFile(items, fileName, …)` and `ToStreamedFile(fileName, write, …)`. The content-type constant is
   `text/calendar`, and files are served as `text/calendar; charset=utf-8`.
10. **Reading is bounded.** Size in bytes (16 MiB by default), components (50,000), unfolded line length (4 MiB, for
    embedded HTML and attachments), properties per component (1,000) and nesting depth (8), as read options. As for
    contacts, a per-item limit costs at most that item and the read continues: a line over the limit costs its
    property (and so the typed view, when the property places the item in time), too many properties or too deep a
    nesting cost the item's typed view, and the reader skips to the item's end. A file limit (the bytes, the number of
    components) stops the read with one error and keeps what it read before it. The defaults cover every observed file
    by an order of magnitude and are sized for the 1–10 node tier.
11. **The model is the one CalDAV builds on.** The tree is what CalDAV's filters and partial retrieval walk, the
    reader is what validates a stored calendar object, the writer is what returns one, expansion is what its
    time-range queries and `expand` use, and the `VTIMEZONE` conversions in both directions are public for its
    time-zone properties. Rules that belong to the protocol (one `UID` per resource, the time-range table of RFC 4791,
    free-busy aggregation, scheduling) stay in the CalDAV package.

Out of scope: iTIP processing and iMIP mail (`METHOD` is read and written as a property), free-busy computation,
`RANGE=THISANDFUTURE` semantics, non-Gregorian calendars, vCalendar 1.0, xCal and jCal, fetching subscriptions over
HTTP (an application downloads with its own `HttpClient` and reads the stream), and deciding what an import does with an
item (create, update, skip, de-duplicate).

## Consequences

- An application adds the package and reads an upload's stream into a result with every item, its errors and its
  occurrences in a window, from which it builds a preview and then commits; it publishes a feed by writing events to an
  `ElarionFile` or a streamed file. No reflection, no third-party types in the public API, nothing to configure.
- Elarion owns a recurrence engine. It is the bulk of the work and the main risk; the tests take their oracle from
  the RFC's own examples, the published test suites of other engines where their licenses allow, and comparisons
  against fixtures generated by mature engines, and they pin every DST edge the research lists.
- Results depend on the system's time-zone data. A NativeAOT application in a default slim or chiseled container has
  no tzdata, so a named zone resolves only through the file's `VTIMEZONE`; such an application installs tzdata (the
  `-extra` images) when it needs zones the files do not define. The capability page says so.
- Windows ids resolve through ICU, which invariant globalization switches off; files from Outlook define their zones,
  so that fallback covers them, and a zoned time written from a Windows `TimeZoneInfo` keeps its Windows id when it
  cannot be converted.
- The typed model leaves out a broken optional property and keeps it in the tree, so an application that cares about a
  property the model treats as optional finds the error in the result, not a missing item.
- An export states a timestamp: every event carries `DTSTAMP` or `LAST-MODIFIED`, or the write options name one. That
  is one line in a handler and what keeps feeds and CalDAV ETags stable.
- `Elarion.Calendars` and `Elarion.Contacts` share the content-line layer as source: a fix to folding or decoding
  lands in both packages in one change, and neither carries a public API for it.
- The package's tests include a NativeAOT publish that reads, expands and writes calendars and must produce no
  trimming or AOT warning.

## Alternatives considered

- **Ical.Net behind an internal interface.** The obvious choice and the one the design language allows. Rejected: not
  AOT- or trim-compatible, with silent data loss in a trimmed build; brings NodaTime; a breaking major version is
  imminent. Revisable if it ships declared AOT compatibility and a settled engine, though the public API would not
  change either way.
- **NodaTime as the one outside library, for its embedded time-zone database.** Would make results independent of the
  system's tzdata and of ICU. Rejected with the user: a second date-time vocabulary next to the BCL's, 130 KB of data,
  and a database that can lag the system's; the file's `VTIMEZONE` covers the files that matter, and tzdata is one
  package in a container.
- **Generated typed records (`[CalendarRecord]`).** The design language's default. Rejected: the RFC fixes the fields,
  and a mapping from an application type to an event would neither express series, overrides and zones nor save code.
- **A typed model with a bag of unknown properties, no public tree.** Simpler. Rejected: CalDAV filters and partial
  retrieval address arbitrary properties and parameters, and would need a second tree.
- **The file's `VTIMEZONE` first, as the RFC literally reads.** Rejected: producers' definitions are often truncated
  (one rule, no history), which shifts past and future dates; the system's database is more complete for an id it
  knows.
- **Floating times resolved to a default zone (`X-WR-TIMEZONE` or the server's).** What some readers do silently.
  Rejected: a guessed zone shifts every time by hours without a trace; the caller passes the zone.
- **An unknown `TZID` read as floating, without an error** (python icalendar). Rejected: the time looks right and is
  wrong; the item carries `calendar.unknown_time_zone` and the application decides.
- **Every broken value invalidates its item**, as a spreadsheet cell does. Rejected with the user: a calendar carries
  many properties the application never asked for; one badly written one should not drop an event.
- **Unbounded or count-only expansion** (Ical.Net's lazy sequence, rrule.js's `all`). Rejected: a window and two caps
  are what every engine that survived hostile input converged on.
- **Excluding sub-hourly frequencies.** Would shrink the test matrix. Rejected with the user: the iteration cap bounds
  them, and CalDAV must expand what clients store.
- **A `VTIMEZONE` written from `GetAdjustmentRules`, or none at all (RFC 7809 by reference).** The rules differ by
  platform and invert in the southern hemisphere; omitting the component makes Outlook place times an hour off.
  Rejected for one built from transitions.
- **`DTSTAMP` from the clock when an item has none.** Most writers do it. Rejected: the bytes change on every write,
  so a feed or a CalDAV collection looks changed to every subscriber on every poll.
- **Exceptions for broken files.** Rejected for the same reasons as in ADR-0108: the user needs every error at once.
- **The content-line layer private to the first package that ships, moved when the second ships.** What this ADR first
  said. Rejected: the two packages are designed together, two copies or a later move leave the second package to
  discover the differences, and the folding and decoding rules would drift.
- **A shared content-line package.** A small internal package instead of a source-shared folder. Rejected: it would
  be a published package with no public API; linking a folder is how the repository already shares generator code.
- **One encoding per file** (UTF-8 for the whole file when it all validates, else the fallback for all of it).
  Rejected for the per-line ladder vCard needs too: one stray byte would push every UTF-8 line of the file through
  Windows-1252, and two ladders would split the design language.
- **Every limit stops the read.** What this ADR first said. Rejected for the rule both line formats follow: one
  oversized description should cost its event, not every event after it; only a file limit stops.

## Amendment (2026-11-27): hostile input, override versions, `EXRULE` and strict reads

The first application to import uploaded files with the package measured two hostile-input faults and asked for
controls it had built on its own side. Each is decided here; the published page says what holds.

- **Time-zone ids are bounded and resolved in linear time.** Decision 7's vendor-prefix removal built every suffix of
  the id by joining its segments, which is quadratic (a 64 KB id of `/a` segments took 3.1 s, and a 4 MiB line hours).
  The suffixes are now sliced while scanning backwards, and only the last four segments are tried, because an IANA id
  has at most three. A read refuses an id longer than `CalendarReadOptions.MaxTimeZoneIdLength` (128 characters by
  default, far above vendor prefixes and Outlook's display names) as `calendar.unknown_time_zone`, without a lookup;
  a `VTIMEZONE` or `X-WR-TIMEZONE` with such an id is not used.
- **No process-wide cache keeps untrusted text.** The system-zone cache was keyed by the id as written and cached misses,
  so every distinct id of every upload stayed for the life of the process (200 reads of a distinct 100 KB id kept
  39 MB). It now holds only zones the system resolves, keyed by the candidate that resolved, case-insensitively and
  capped at 1,024 entries; the shared resolver of `CalendarEvent.TryFromComponent` caches nothing, and a read's own
  resolver caches for that read only. The writer's `VTIMEZONE` cache holds only zones the system knows, because a zone
  built from an uploaded `VTIMEZONE` carries an id the file chose. Rejected: a bounded LRU of misses (it saves a failed
  lookup per id and per read, which is cheap, at the price of a policy to tune).
- **Expansion bounds its work, not only its output (decision 8).** The two caps bounded the occurrences and the periods
  without a match, plus a hidden cap of a million enumerated instances; none bounded the work inside one period, where a
  yearly rule with every hour, minute and second builds 31.5 million candidates (2 GB) before filtering.
  `CalendarExpandOptions.MaxCandidates` (1,000,000) replaces the hidden cap and is counted as candidates are built, so a
  period stops as soon as it passes the cap; a period cut short is not used, since its `BYSETPOS` positions would be
  wrong, and the series gets `calendar.recurrence_limit`. A rule without `COUNT` now starts a few periods before the
  window for monthly and yearly frequencies too, not only for weekly and shorter ones; a rule with `COUNT` is still
  counted from `DTSTART`, because skipping would need the number of instances of every skipped period, which `BY`
  parts and `BYSETPOS` make irregular, and `MaxCandidates` bounds it. `Expand` takes a cancellation token, checked per
  series and per period. Repeated `BY` values count once, and a `BY` list longer than its part has distinct values
  does not parse. Rejected: a per-period cap next to a per-series one (one total bound covers both and is one setting),
  and a streaming `IEnumerable` per series (the window and the caps already bound the result; a caller that caps by days
  passes a smaller window).
- **Reading has a line and an item limit, and checks its token while mapping.** `MaxLines` (1,000,000) and `MaxItems`
  (50,000 events and to-dos; alarms and time zones do not count, unlike `MaxComponents`) are file limits like
  decision 10's: one error, the items before kept. The defaults lie above any file the byte limit lets through in
  practice; they exist so an application can choose a lower bound declaratively.
- **Of several overrides of one instance, the latest counts.** A producer that sends an update again leaves two
  overrides with one `UID` and `RECURRENCE-ID`; the reader reports `calendar.duplicate_uid`, and expansion used to give an
  occurrence for each. Calendar clients keep the latest, so expansion now does by default
  (`ResolveOverrideVersions`): the highest `SEQUENCE`, then the latest `DTSTAMP`, then the later in the input. The
  option turns it off for a caller that wants every component.
- **Cancelled items are skipped on request, not by default.** `SkipCancelled` leaves out items with `STATUS:CANCELLED`:
  a cancelled override removes its instance, a cancelled master its series, a cancelled single item itself. It is off
  by default, against the request to make it the default, because expansion reports what a file states and the
  CalDAV package's time-range queries and `expand` must return cancelled instances (RFC 4791 matches components, not
  their status); an application that shows a calendar turns it on, as clients do.
- **`EXRULE` is applied.** RFC 5545 deprecates it, but RFC 2445 producers and older Outlook exports still write it, and
  the reader used to keep it as an unmapped property that expansion ignored. It is now `CalendarItem.ExceptionRules`,
  a property that places the item in time (decision 5: one that does not parse clears the typed value), written back
  in the fixed property order after `EXDATE`, and applied with RFC 2445 semantics: its instances, computed from
  `DTSTART` like a rule's, are excluded. `DTSTART` itself is never excluded, as every rule yields it first; this keeps
  what importers that implemented `EXRULE` on their own side did.
- **Leniency stays the default; strictness is declarative.** Decision 6 holds. `CalendarReadOptions.Strict` reports the
  two things the lenient reader accepts silently: a rule with both `COUNT` and `UNTIL` (RFC 5545 forbids it;
  `calendar.invalid_value`) and a value with a control character other than tab (`calendar.control_character`, the line
  skipped). `CalendarErrorCodes.IsStructural(code)` names the errors about a file's form, so an application that
  refuses broken files writes that policy as one predicate. Rejected: a strict mode that turns every error into a file
  error (it would only move the predicate into the package).
- **Error wording stays with the application.** The request for translated messages is answered by the formatter
  `ToAppError` already takes, as in `Elarion.Contacts` and `Elarion.Spreadsheets`. The framework ships no translated
  messages anywhere (request validation localizes through the application's own resources), and a formatter in one
  language would be one application's choice made for all. The page shows a formatter by code.
- **Override matching stays exact.** An override whose `RECURRENCE-ID` names the right day at another time is an extra
  occurrence, as RFC 5545 says and the page states; matching by day, as one library did, would merge distinct
  instances of an hourly series.

---
type: Research note
title: "iCalendar in .NET: libraries under NativeAOT, and what real producers publish"
description: "A survey of .NET iCalendar libraries for NativeAOT, recurrence and time-zone handling, of the time-zone data .NET itself offers, and of the .ics files Google, Outlook, Apple and municipal calendars publish; input for ADR-0109."
generated: { by: claude-code/claude-opus-5-5, at: 2026-11-26T16:00:00Z }
status: draft
sources:
  - https://www.rfc-editor.org/rfc/rfc5545
  - https://errata.rfc-editor.org/rfc5545
  - https://www.rfc-editor.org/rfc/rfc6868
  - https://www.rfc-editor.org/rfc/rfc7986
  - https://www.rfc-editor.org/rfc/rfc9074
  - https://github.com/ical-org/ical.net
  - https://github.com/ical-org/ical.net/pull/977
  - https://github.com/ical-org/ical.net/issues/749
  - https://github.com/nodatime/nodatime
  - https://github.com/mattjohnsonpint/TimeZoneConverter
  - https://github.com/EWSoftware/PDI
  - https://devblogs.microsoft.com/dotnet/date-time-and-time-zone-enhancements-in-net-6/
  - https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/TimeZoneInfo.FullGlobalizationData.cs
  - https://github.com/dotnet/dotnet-docker/blob/main/src/runtime-deps/10.0/noble-chiseled/amd64/Dockerfile
  - https://github.com/libical/libical/blob/master/src/libical/icallimits.c
  - https://github.com/ical4j/ical4j
  - https://github.com/fmeringdal/rust-rrule
  - https://github.com/collective/icalendar
  - https://github.com/sabre-io/vobject
  - https://learn.microsoft.com/en-us/openspecs/exchange_server_protocols/ms-oxcical/65e61fa3-de85-4030-8245-1054e27208f7
  - https://learn.microsoft.com/en-us/openspecs/exchange_standards/ms-stanoical/c5b49d8e-6122-4bd7-a2f2-25c222fe5bfc
  - https://support.google.com/calendar/answer/45654
  - https://github.com/mampfes/hacs_waste_collection_schedule
---
# iCalendar in .NET: libraries under NativeAOT, and what real producers publish

- Date: 2026-11-26
- Status: Research input for [ADR-0109](../decisions/0109-icalendar-model-reader-and-writer.md). This is not a
  decision.
- Question: is there a .NET iCalendar library that a NativeAOT framework package can hide behind an internal
  interface, and if not, which parts would the package own? What do the files a reader meets in practice look like,
  and what must a writer emit so that Google, Outlook and Apple take it?
- Method: the repositories of the libraries cloned and searched for reflection (`Activator`, `MakeGenericType`,
  `GetMethods`) and for `IsAotCompatible`/`IsTrimmable`; releases and dependencies from the NuGet API; activity from the
  GitHub API; the .NET runtime and container image sources for time-zone behavior. Eleven real calendars were
  downloaded on the day and inspected byte for byte: three Google feeds, the iCloud holiday feed, two Apple and two
  Outlook 16 exports, two German waste-collection feeds and one school-holiday feed. **Inference** marks my reading.

## Summary

1. **No .NET iCalendar library passes the AOT bar.** Ical.Net, the only maintained and widely used one, declares
   neither `IsAotCompatible` nor `IsTrimmable` and deserializes through reflection; an open pull request shows that a
   fully trimmed build silently replaces a parsed `UID` with a random GUID, so overrides stop matching their series.
   Its next major version is a breaking rewrite, and it depends on NodaTime.
2. **Recurrence is the hard part, and it is still moving in the leading library.** Ical.Net fixed a hang on an hourly
   rule across a DST change, lost times of day in a DST gap and rules that ran past `UNTIL` in the past year; several
   counting bugs are still open. Every mature engine (libical, ical4j, the Rust `rrule` crate) bounds expansion twice:
   a caller's window or count, and a cap on iterations that produce nothing.
3. **.NET's own time zones cover IANA ids, with two gaps.** `TimeZoneInfo` accepts IANA and Windows ids since .NET 6,
   but converts Windows ids only through ICU, which invariant globalization switches off; and Linux reads zones from
   the system's tzdata, which the default slim and chiseled .NET container images do not ship.
4. **Real files break the RFC in a small, known set of ways**: no folding or folding by characters, a byte-order mark,
   bare line feeds, quoted and Windows time-zone ids, ids without a `VTIMEZONE`, `X-WR-TIMEZONE` as a default, dates
   where a date-time belongs, truncated values. Tolerant readers agree on how to handle each.
5. **A writer that interoperates is short to describe**: CRLF, folding at 75 octets on character boundaries, a stable
   `UID`, `DTSTAMP` in UTC, all-day events as dates with an exclusive end, IANA ids each with a full `VTIMEZONE`, and
   properties before sub-components.

## Libraries

| Library | Facts | Fit |
| --- | --- | --- |
| [Ical.Net](https://github.com/ical-org/ical.net) | MIT; 5.2.4 (2026-10-31, about 41 M downloads), 6.0.0-pre.2 the same day; two active maintainers, last push 2026-11-19. Targets net8.0, net6.0 and netstandard2.x (v6: net10.0, net8.0, netstandard2.0). Hard dependency on NodaTime 3.2; in v6 occurrences are NodaTime `ZonedDateTime`s. No `IsAotCompatible`/`IsTrimmable`; `Activator.CreateInstance` in five places, `MakeGenericType` with `GetMethod("Add")`, and a `GetMethods()` scan for `[OnDeserialized]` callbacks. [PR #977](https://github.com/ical-org/ical.net/pull/977) ("Aot remove reflection", open since 2026-08-31, unreviewed) documents the trimmed-build data loss. `GetOccurrences` is lazy and unbounded; the only guard, `MaxUnmatchedIncrementsLimit`, is off by default. Unknown `TZID` throws; a `TZID` defined only by the file's `VTIMEZONE` is not evaluated ([#749](https://github.com/ical-org/ical.net/issues/749), "Customized Time Zone"). No `RANGE=THISANDFUTURE`, RFC 6868 an open PR, no typed RFC 7986 properties. About 19,500 lines. | Not AOT-safe; would also spend the one-library budget on NodaTime |
| [NodaTime](https://github.com/nodatime/nodatime) | Apache-2.0, 3.3.7; declares `IsAotCompatible` since 3.2 and tests it with a published app; embeds the IANA database (`Tzdb.nzd`, 132,773 bytes), so it needs no system tzdata. | AOT-safe, but a time library, not an iCalendar one; its types would have to stay out of the public API |
| [TimeZoneConverter](https://github.com/mattjohnsonpint/TimeZoneConverter) | MIT, 8.0.1 (2026-11); embeds the CLDR Windows-to-IANA table as a resource, no reflection, no AOT flag. | Only fills the Windows-id gap under invariant globalization |
| [EWSoftware.PDI](https://github.com/EWSoftware/PDI) | MS-PL, last release 2025-01; RFC 2445 (the predecessor of RFC 5545), netstandard2.0 and net48. | Old standard, little activity |
| DDay.iCal; recurrence-only ports | DDay.iCal is Ical.Net's dead ancestor; the .NET RRULE-only libraries found have 13 stars or fewer. | No |

## Time zones in .NET itself

- `TimeZoneInfo.FindSystemTimeZoneById` accepts IANA and Windows ids on every platform since .NET 6 and converts
  between them with ICU's CLDR data ([.NET 6 announcement](https://devblogs.microsoft.com/dotnet/date-time-and-time-zone-enhancements-in-net-6/)).
- With `InvariantGlobalization=true`, `TryConvertWindowsIdToIanaId` and `TryConvertIanaIdToWindowsId` return `false`
  at once ([runtime source](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/TimeZoneInfo.FullGlobalizationData.cs)).
  IANA ids still resolve, because Linux reads TZif files directly; Windows ids then do not resolve on Linux.
- Linux and macOS have no embedded database: zones come from `/usr/share/zoneinfo` (`TZDIR` overrides it). A missing
  zone throws `TimeZoneNotFoundException`. The .NET 10 `alpine` and `noble-chiseled` runtime images ship neither tzdata
  nor ICU and set invariant globalization; only the `-extra` variants add both
  ([image source](https://github.com/dotnet/dotnet-docker/blob/main/src/runtime-deps/10.0/noble-chiseled/amd64/Dockerfile)).
  **Inference:** a NativeAOT application in a default slim image resolves no named zone at all unless it adds tzdata.
- `TimeZoneInfo.GetAdjustmentRules` can drive a `VTIMEZONE` writer, but its rules differ in shape between Windows
  (registry rules, short history) and Unix (rules synthesized from TZif transitions), `BaseUtcOffsetDelta` must be
  applied, and southern-hemisphere zones invert the months: Ical.Net wrote wrong offsets for them until October 2026
  ([#1006](https://github.com/ical-org/ical.net/issues/1006)). **Inference:** generating observances from the actual
  transitions (`TimeZoneInfo` conversions at year boundaries) is safer than translating adjustment rules.

## How other engines bound recurrence and treat unknown time zones

| Engine | Bound | Unknown `TZID` |
| --- | --- | --- |
| [libical](https://github.com/libical/libical/blob/master/src/libical/icallimits.c) | 100,000 searches for the next occurrence; parser caps of 10,000 properties, 10 MiB per value, nesting 100 | Built-in zone table |
| [ical4j](https://github.com/ical4j/ical4j) | Expansion always takes a period; 1,000 increments without a candidate | Throws; ignored in relaxed mode; an in-file `VTIMEZONE` of the same name wins |
| [Rust rrule](https://github.com/fmeringdal/rust-rrule) | Error after 100,000 iterations without a match; `all(limit)` hard cap up to 65,535 | n/a |
| rrule.js | `between`, or a callback that stops; otherwise runs to the year 9999 | n/a |
| [python icalendar](https://github.com/collective/icalendar) with recurring-ical-events | `between`/`at`/`count`; `skip_bad_series` skips a broken series instead of failing | Cleaned id, Windows table, vendor prefix stripped (`/mozilla.org/…/Europe/Berlin`); else the time stays floating, without an error |
| [sabre/vobject](https://github.com/sabre-io/vobject) | n/a | Name lookup, then guesses from the `VTIMEZONE` (`X-LIC-LOCATION`, Microsoft ids, `(UTC+01:00) …` display names), else a default zone |

## What producers publish

**Google** (three feeds). Always `PRODID:-//Google Inc//Google Calendar 70.9054//EN`, `CALSCALE`, `METHOD:PUBLISH`,
`X-WR-CALNAME`, `X-WR-TIMEZONE`; no RFC 7986 properties. Single timed events in UTC, recurring events and their
overrides with IANA `TZID`s and a `VTIMEZONE` carrying `X-LIC-LOCATION`. All-day events as `VALUE=DATE` with an
exclusive `DTEND`; holiday feeds have one event per date and no `RRULE`. Lines folded at 75 characters, not octets (228
lines of 76–80 octets in one feed), but never inside a UTF-8 sequence. HTML in `DESCRIPTION`. Other clients'
properties re-emitted as they came (`X-APPLE-*`, `X-MOZ-*`, `ACKNOWLEDGED`). Import takes files up to 1 MB
([Google help](https://support.google.com/calendar/answer/45654)). UIDs were stable across two downloads.

**Outlook and Exchange** ([MS-OXCICAL](https://learn.microsoft.com/en-us/openspecs/exchange_server_protocols/ms-oxcical/65e61fa3-de85-4030-8245-1054e27208f7),
[MS-STANOICAL](https://learn.microsoft.com/en-us/openspecs/exchange_standards/ms-stanoical/c5b49d8e-6122-4bd7-a2f2-25c222fe5bfc),
two exports). Windows zone names as `TZID`, quoted where used (`DTSTART;TZID="FLE Standard Time":…`), with a
`VTIMEZONE` of one `STANDARD` and one `DAYLIGHT` starting in 1601 and no history. Folding with CRLF and a tab. Hex
`UID`s of about 112 characters; `X-ALT-DESC;FMTTYPE=text/html` with about 40 KB of HTML per event;
`X-MICROSOFT-CDO-*`, `X-MS-OLK-*`; `METHOD:REQUEST` in files saved from an invitation. Outlook accepts CR, LF or CRLF,
any line length, and ignores a BOM; it compares `TZID`s case-sensitively in versions 2007–2019 and resolves a
`VTIMEZONE` that follows its first use. All-day events are exported as dates; on import, Outlook honors
`X-MICROSOFT-CDO-ALLDAYEVENT` with midnight start and end, which older Exchange writes. Field reports: events whose
`TZID` is "Customized Time Zone", and files that use a `TZID` they never define
([ical.net #749](https://github.com/ical-org/ical.net/issues/749)); an IANA `TZID` without a `VTIMEZONE` imported one
hour off.

**Apple** (two macOS 14 exports, the iCloud holiday feed). IANA `TZID`s with a `VTIMEZONE`, no `X-LIC-LOCATION`.
`X-APPLE-STRUCTURED-LOCATION` with a quoted `X-TITLE` parameter containing commas and a `geo:` value. Alarms carry
`UID` and `X-WR-ALARMUID` with the same value and `ACKNOWLEDGED` ([RFC 9074](https://www.rfc-editor.org/rfc/rfc9074));
a disabled alarm is `ACTION:NONE` with the trigger `19760401T005545Z`. The iCloud holiday feed has an invalid
`DTSTAMP;VALUE=DATE`, all-day events without `DTEND` (one day, per RFC 5545), `RRULE:FREQ=YEARLY;COUNT=6`, and is served
gzip-compressed.

**Waste-collection and school calendars.** A German city's waste feed: UTF-8 with a BOM, no folding at all (lines up
to 267 octets), one all-day event per collection date, the waste type in `SUMMARY`, a `VTIMEZONE` defined but unused,
no alarms, no `CATEGORIES`. Another district's feed: timed 06:00–07:00 events with a one-hour `VALARM`, a URL as
`PRODID`, `X-WR-TIMEZONE`. A school-holiday feed uses RFC 7986 `NAME` and generates **new random UIDs on every
download**, so a re-import cannot match events by `UID` alone. The Home Assistant waste-collection project
([source](https://github.com/mampfes/hacs_waste_collection_schedule)), which reads hundreds of German and Austrian
providers, repairs `…T000000ZT000000Z` timestamps, values truncated after `T`, `TZID` on date values, date `EXDATE`s
against date-time starts, overrides without `SUMMARY`, the BOM, and several waste types in one `SUMMARY` (its
`split_at` option). Some provider URLs return an HTML page instead of a calendar. Not seen in any downloaded file,
though plausible: Windows-1252 bodies, missing `VERSION`, `UID` or `DTSTAMP`, duplicate UIDs, `CATEGORIES` as the waste
type; sabre/vobject and Thunderbird both fall back from UTF-8 to ISO-8859-1 or Windows-1252.

## Deviations tolerant readers accept

| Deviation | Seen from | Tolerated by |
| --- | --- | --- |
| Bare LF or CR line ends, folding with LF | Older Mozilla and KDE clients, scripts | Outlook, ical4j relaxed, python icalendar |
| No folding, or lines over 75 octets | A waste feed; Google (character folding); Outlook (tab plus 75) | All |
| UTF-8 byte-order mark | A waste feed | Outlook, sabre/vobject, python icalendar |
| Lower-case or odd property names, spaces around `:`, `;`, `=` | Hand-written feeds | ical4j relaxed, sabre forgiving mode, python icalendar non-strict |
| Unparseable lines | Various | Outlook and sabre ignore them; python icalendar keeps them and lists them as errors; libical inserts `X-LIC-ERROR` |
| Unescaped `,`, `;`, `:` in text | Many | python icalendar |
| Quoted `TZID` values, with commas | Outlook | Most; strictly invalid, erratum 5505 to allow them is "held" ([errata](https://errata.rfc-editor.org/rfc5545)) |
| Windows ids and `(UTC+01:00) …` display names | Outlook, Exchange | sabre/vobject, python icalendar, Ical.Net (through NodaTime) |
| Vendor prefixes (`/mozilla.org/20070129_1/America/New_York`) | Old Lightning, libical | python icalendar, Thunderbird |
| `TZID` without a `VTIMEZONE`, or defined after its use | Outlook, generators | sabre (default zone), python icalendar (floating); Ical.Net throws |
| `X-WR-TIMEZONE` as the default for floating times | Google, a waste feed | python `x-wr-timezone`; strict readers ignore it |
| Date where a date-time belongs, no seconds, truncated `…T` | iCloud, waste feeds | Repairs in the waste-collection project |

Standards a reader should know beyond RFC 5545: [RFC 7986](https://www.rfc-editor.org/rfc/rfc7986) (calendar `NAME`,
`COLOR`, `REFRESH-INTERVAL`, `IMAGE`, `CONFERENCE`), [RFC 6868](https://www.rfc-editor.org/rfc/rfc6868) (`^n`, `^'`,
`^^` in parameter values), RFC 9074 (alarm `UID`, `ACKNOWLEDGED`). Verified errata that change behavior: 2039 (a
date-time `TRIGGER` needs `VALUE=DATE-TIME`) and 4271 (a nonexistent local time in a recurrence is moved, not
skipped).

## What a writer must emit

From RFC 5545 and the interop reports: `VERSION:2.0` and one `PRODID`; `UID` and a UTC `DTSTAMP` on every event;
a `VTIMEZONE` for every `TZID` used, with the id unquoted and in the same case (Outlook placed an IANA id without one
an hour off); all-day events as `VALUE=DATE` with an exclusive `DTEND`; recurring timed events in local time with a
`TZID` (in UTC, a DST change shifts the series); properties before `VALARM`s (new Outlook reportedly drops
`LOCATION` otherwise); CRLF, folding at 75 octets without splitting a UTF-8 sequence, no BOM; text escaped (`\\`,
`\;`, `\,`, `\n`), parameter values per RFC 6868 and quoted when they contain `:`, `;` or `,`; media type
`text/calendar; charset=utf-8`. For feeds, both `NAME` and `X-WR-CALNAME`, both `REFRESH-INTERVAL` and
`X-PUBLISHED-TTL` (what python icalendar now writes).

## Sizes

Observed: a waste feed for one address and year 80–140 events, 33–46 KB; a school-holiday feed 7 events, 2 KB;
national holiday feeds 130–300 events, 38–112 KB; long-lived community calendars 800–900 events, about 400 KB; one
Outlook meeting 40 KB because of its HTML description. Google imports at most 1 MB. **Inference:** limits of a few
megabytes and tens of thousands of components cover every observed case by an order of magnitude; recurrence
expansion needs its own bound, because a three-line rule can describe millions of occurrences.

## Not verified

- Whether Google's import honors `VALARM` and `METHOD`, and how often subscriptions refresh.
- Whether current Outlook still writes `X-MICROSOFT-CDO-ALLDAYEVENT`.
- Waste feeds in Windows-1252, without `VERSION`, `UID` or `DTSTAMP`, with duplicate UIDs or day-before alarms: none
  in the downloaded sample; two large providers could not be fetched without per-address keys.
- Official documentation of the `X-APPLE-*` properties: none found.

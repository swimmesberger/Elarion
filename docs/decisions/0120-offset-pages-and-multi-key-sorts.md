---
type: Architecture Decision Record
title: "ADR-0120: Offset pages have their own envelope, and a sort may combine several keys"
description: "ToOffsetPageAsync returns OffsetPage<T> with a required total and no cursors instead of the shared Page<T>; a SortMap sort is a comma-separated key list, capped per map, refused like an unknown key when it repeats a key or exceeds the cap, with a message hook on Validate."
status: stable
---
# ADR-0120: Offset pages have their own envelope, and a sort may combine several keys

- Status: Accepted
- Date: 2026-11-27
- Related: [ADR-0100](0100-the-schema-exports-constants-presets-and-limits.md) (the schema exports a handler's sort
  keys), [ADR-0082](0082-requiredness-comes-from-nullability-in-both-directions.md) (requiredness comes from
  nullability, which is what makes a non-nullable `Total` required on the wire)

## Context

Paging shipped with one envelope, `Page<T>`, for keyset and offset lists alike: `Items`, `HasNext`, `HasPrevious`,
the keyset cursors and an optional `Total`. The pagination page argued that one shape lets one list component cover
both strategies. In use, offset lists were the common case and paid for the union: the generated TypeScript typed
`total` as `number | null | undefined` although `ToOffsetPageAsync` always counts, typed two cursors that are always
`null`, and `Page<T>.Empty` had no total, so an empty offset page had to be written out by hand. A documented recipe
("ignore the cursors, `total ?? 0`") does not remove the null checks from every consumer.

Offset lists were also sorted by one key per request. `SortMap.Apply` resolved `sort` to a single key plus that key's
declared tiebreakers, so a table that sorts by several columns the user chose (city, then name) could only do so in
the browser, over rows that were all loaded, or by declaring every combination as a key of its own. `Validate`
answered a refused key with a fixed English message.

## Decision

**`OffsetPage<T>`.** `Elarion.Abstractions.Paging.OffsetPage<T>` carries `Items`, a required `int Total`,
`HasNext` and `HasPrevious`, with `Map`, `MapAsync`, `Empty` (total `0`) and `ToPage()`, which converts it into the
shared envelope for an endpoint whose contract stays `Page<T>`. `ToOffsetPageAsync` returns it. `Page<T>` stays the
keyset envelope; its `Total` remains optional for a handler that counts. Both share `items`, `hasNext` and
`hasPrevious`, so a list component that reads only those renders either.

**Multi-key sorts.** A `sort` value is one key or a comma-separated list of keys, each `key`, `+key` or `-key`.
`Apply` orders by the keys from left to right (`OrderBy`, then `ThenBy`), each in its requested or declared
direction, and appends the declared tiebreakers of the last key only, which keeps the order stable without repeating
columns. A single key keeps its meaning. A sort combines at most `MaxLevels` keys, `3` by default and set per map with
the builder's `MaxLevels(n)`; the level count is checked before any key is looked up, so a hostile sort costs one pass
over the text. An unknown, empty or repeated key, or a sort above the cap, is refused: `Apply` falls back to the
default key and `Validate` answers `paging.sort_key_unknown`, the code handlers already declare.

**A message hook.** `Validate(sort, field, message)` takes an optional `Func<SortRefusal, string>`; the refusal
carries the reason (`UnknownKey`, `RepeatedKey`, `TooManyKeys`), the key at fault, the allowed keys and the cap.
Without it the message stays English.

## Consequences

- Breaking (pre-1.0): a handler that returned `Result<Page<T>>` from `ToOffsetPageAsync` changes its declared result
  to `OffsetPage<T>`, or calls `.ToPage()` to keep its wire contract. The generated TypeScript type of an offset list
  becomes `{ items, total: number, hasNext, hasPrevious }`.
- A sort value with a comma used to be one unknown key; it is now a list. A key containing a comma could never be
  declared usefully before, so no map changes meaning.
- The schema exports the allowed keys (ADR-0100), not the level cap; a client that combines more keys than the
  handler allows gets the validation error.

## Alternatives considered

- **Keep one envelope and document the offset recipe.** Rejected: the recipe was already published and the
  null checks stayed in every consumer; the type is the documentation a client reads.
- **A `KeysetPage<T>` as well, retiring `Page<T>`.** Rejected for now: keyset consumers were not affected, and a
  rename would break every keyset handler for no gain.
- **Separate error codes for a repeated key or too many keys.** Rejected: each would have to be declared on every
  handler that validates its sort; the field message says what was wrong, and the hook receives the reason.
- **Applying every key's tiebreakers.** Rejected: the earlier keys' tiebreakers (usually the id) would decide the
  order before the later keys the client asked for.
- **Exporting the level cap in the schema.** Rejected for now (revisable): the keys are what a client builds sort parameters
  from, and the default cap covers the multi-column grids seen so far.

## Amendment (2026-11-27): the schema exports the level cap

The rejected alternative "exporting the level cap in the schema" is reversed. The first adoption showed that a
handler's own description of its sort parameter, written for one key, became incomplete the day every `SortMap`
started to accept three keys, and a client had to copy the cap to know when to stop adding sort levels. The cap is one
integer the server already holds, so the schema now carries it as `methods.{method}.maxSortLevels` next to
`sortKeys` (only where `sortKeys` is exported, from the same map), and the generated client exports
`rpcMaxSortLevels['{method}']`. It is a separate export rather than a field inside `rpcSortKeys`, so existing code
that reads `rpcSortKeys['{method}']` as an array keeps compiling. The default cap of three stays: a list written for
one key gains multi-key sorting without a change, and `.MaxLevels(1)` restores the single-key behaviour where a
handler wants it. The release notes and the pagination page say so in those words.

---
type: Format
title: "ADR format"
description: "When to write an architecture decision record, its format and its statuses."
generated: { by: claude-code/claude-fable-5-2, at: 2026-11-21T11:30:00Z }
status: stable
---
# Architecture Decision Records

This directory holds Architecture Decision Records (ADRs) for Elarion, in the
classic [Nygard format](https://cognitect.com/blog/2011/11/15/documenting-architecture-decisions).
The list of records is [`index.md`](index.md).

An ADR captures a single architecturally significant decision: the context that
forced it, the decision itself, and the consequences we accept. ADRs are
append-only history — once an ADR is `Accepted`, prefer writing a new ADR that
supersedes it over editing the original.

These records are intentionally **not** published on the documentation site. The
site documents shipped behavior; ADRs document *why* we chose a design, including
designs that are not yet implemented. A published page that cites an ADR links it
by its absolute GitHub URL
(`https://github.com/swimmesberger/Elarion/blob/main/docs/decisions/NNNN-….md`).

## When to write one

All three must hold:

1. **Hard to reverse.** Changing your mind later costs real effort.
2. **Surprising without context.** A future reader would look at the code and ask why.
3. **A real trade-off.** There were genuine alternatives and one was chosen for reasons.

When a feature is in scope and several reasonable designs exist, the ADR records the one that was chosen and
the alternatives that were rejected, as revisable (`AGENTS.md`, "Decide, do not defer"). An ADR may be
`Proposed` for a design that is not built yet.

## Conventions

- File name: `NNNN-kebab-case-title.md`, a zero-padded sequence number. Take the next free number and never
  renumber. A number is taken by the first branch that uses it, so look at open pull requests as well as at
  this folder.
- First line after the frontmatter: `# ADR-NNNN: Title`, with the number of the file name.
- Header bullets: `- Status:`, `- Date:` and, when they add value, `- Related:`, `- Amends:`, `- Supersedes:`.
- Status: `Proposed` | `Accepted` | `Superseded by ADR-NNNN` | `Deprecated`. A qualifier may follow in
  parentheses (`Accepted (implemented 2026-07-21)`).
- Sections: Context, Decision, Consequences. Add others (Options, Rejected alternatives, Recommended
  pattern, References) when they add value.
- Write in the terms of [`GLOSSARY.md`](../../GLOSSARY.md). If a decision introduces or changes a term,
  update the glossary in the same change.

## Frontmatter

Every ADR starts with the Open Knowledge Format envelope that every `.md` page under `docs/` carries
(see [`../guide.md`](../guide.md)):

```markdown
---
type: Architecture Decision Record
title: "ADR-NNNN: Title"
description: "One sentence stating the decision."
status: draft
---
# ADR-NNNN: Title

- Status: Proposed
- Date: YYYY-MM-DD
```

The frontmatter `status` is the lifecycle word tools read; the `- Status:` bullet is the record's own
wording. They must agree, and `tools/check_repo.py` fails when they do not:

| `- Status:` starts with | Frontmatter |
| --- | --- |
| `Proposed` | `status: draft` |
| `Accepted` | `status: stable` |
| `Superseded by ADR-NNNN` | `status: deprecated` and `superseded_by: NNNN` |
| `Deprecated` | `status: deprecated` |

## Rules

- A new or changed ADR gets its entry in [`index.md`](index.md)
  (`* [ADR-NNNN](NNNN-….md) - Title`, with `(proposed)` or `(superseded by ADR-NNNN)` after the title while
  that is its state) and an entry in [`../log.md`](../log.md), in the same change. `make check` fails on a
  missing index entry.
- Superseding: the new ADR says `- Supersedes:`; the old one gets `- Status: Superseded by [ADR-NNNN](…)`,
  `status: deprecated`, `superseded_by: NNNN`, and a note at the top. Nothing else in it changes.
- Agents write ADRs through the `adr` skill, which follows this file. The `domain-modeling` skill would
  write to `docs/adr/` in its own short format; in this repository its ADRs go here, in this format.

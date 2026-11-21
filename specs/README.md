---
type: Format
title: "Specification format"
description: "How specs and plans are written in this repository."
generated: { by: claude-code/claude-fable-5-2, at: 2026-11-21T11:30:00Z }
status: stable
---
# Specifications

A spec says what a change must do before anyone, human or agent, writes code for it. The code is derived from the spec; the spec is the source and changes with the code ([ADR-0097](../docs/decisions/0097-adopt-the-agentic-repository-layout.md)).

## Files

- `NNNN-slug.md`: the spec, four digits, sequential, never renumbered. One per feature or bounded change.
- `NNNN-slug.plan.md`: the plan, how the spec is built. Optional for small specs.

Both carry Open Knowledge Format frontmatter (`type: Specification` and `type: Plan`), like every `.md` page under `docs/`, so one checker and one reader handle all of them.

Specs 0001 and 0002 describe the repository's own check tools and are the worked examples of the format. Specs of framework features are numbered from 0003.

A spec's `status` is its approval state, and every spec has one: `draft` while it is written and discussed, `stable` once a person has approved it, `deprecated` when the feature is gone or a later spec replaces it. Approval is the checkpoint before any code is written.

## Spec format

Sections in this order, headings as written:

- `## Intent`: one paragraph, in the terms of `../GLOSSARY.md`, on what the user gets and why.
- `## User scenarios`: one `###` subsection per user story, highest priority first, marked `(P1)`, `(P2)`, each with acceptance scenarios in Given/When/Then form. Every story is testable on its own.
- `## Requirements`: numbered `FR-001`, `FR-002`, one testable sentence each, with the RFC 2119 keywords in capitals: MUST and MUST NOT for hard requirements, SHOULD and SHOULD NOT for strong defaults that need a reason to deviate, MAY for options. Nothing else in the spec uses these words in capitals. Write `[NEEDS CLARIFICATION: ...]` instead of guessing.
- `## Success criteria`: numbered `SC-001`, measurable outcomes that do not mention technology.
- `## Out of scope`: what this spec deliberately does not cover, so nobody builds it.
- Optional `## Decisions`: links to ADRs the spec depends on or creates.

A MUST is a test that has to pass before the spec is done, a SHOULD is a review question, a MAY is the implementer's choice. Acceptance scenarios may use EARS (`WHEN <event> THE SYSTEM SHALL <behaviour>`) instead of Given/When/Then when a story has many conditions; keep one form per spec.

A spec contains no implementation detail: no technology choices, no code references, no architecture. In a framework the user is a developer, so the public surface is what the user gets: a spec names the attribute, the package, the diagnostic id or the wire shape a developer will see, and says nothing about how the framework produces it (generator pipelines, internal types, storage layout). Those go into the plan.

## Plan format

Free-form, but it answers: which modules change, which interfaces appear, what data moves, at which seams the tests sit, and which ADRs it relies on or creates. Tasks for the tracker are cut from the plan, each naming the FR numbers it covers.

## Specs, published pages and ADRs

Three documents describe a feature, and each answers one question:

| Document | Question | Written | Reader |
|---|---|---|---|
| Spec, `specs/NNNN-slug.md` | What must this change do? | Before the code, approved by a person | Whoever builds and reviews the change |
| Published page, `docs/concepts/` or `docs/capabilities/` (`.mdx`) | How does the shipped framework behave, and how is it used? | With the code, in the same pull request | Users of the framework |
| ADR, `docs/decisions/` | Why this design, and what was rejected? | When the decision is made | Whoever asks why later |

- **Features that shipped before `specs/` existed have no spec, and none is written after the fact.** Their
  description of record is the published page and the ADRs it cites.
- **A change to such a feature gets a spec for the change**, not for the whole feature. Its `## Intent` links
  the published page as the baseline and states what becomes different.
- **A new feature gets a spec first.** When it ships, the published page is written from the spec's scenarios
  and becomes the description users read; the spec stays `stable` as the requirement set its tests are named
  after.
- **Spec, published page and code change together.** A pull request that changes the behavior of a specified
  feature updates all three, or says why one of them is unaffected.
- A spec never replaces the `CHANGELOG.md` entry, and a `MUST` in a spec never overrides a rule in
  `AGENTS.md` or the pattern rules: a spec that needs to break a framework invariant needs an ADR first.

## Done

A spec is done when every acceptance scenario has a passing test named after it, every open `[NEEDS CLARIFICATION]` is resolved, the published page describes the behavior, and the pull request links the spec. `tools/check_repo.py` checks that every spec has a status, and the sections, the numbering and the open clarifications of every approved (`stable`) spec. Headings must match exactly, including case.

## Why this format

Three families of documents exist. An RFC or design doc is a proposal under review for a change that affects several teams; its outcome is recorded here as an ADR. A feature spec, user stories with acceptance criteria, says what the user gets; that is what `specs/` holds. Generated specs (a skill such as Pocock's `to-spec`, or a framework such as spec-kit, Kiro or OpenSpec) carry the same content in a tool's layout. The format above uses parts older than any of these tools (user stories with Given/When/Then, numbered testable requirements, RFC 2119 keywords); the FR and SC numbering and the clarification marker come from spec-kit's public template. Because it keeps the what apart from the how, a skill can write it and any framework can take it over.

## Sources

- [RFC 2119](https://www.rfc-editor.org/rfc/rfc2119) and [RFC 8174](https://www.rfc-editor.org/rfc/rfc8174), requirement key words
- [spec-kit specification template](https://github.com/github/spec-kit/blob/main/templates/spec-template.md)
- [Kiro specs](https://kiro.dev/docs/specs/), EARS notation
- [Gergely Orosz, RFCs and design docs](https://blog.pragmaticengineer.com/rfcs-and-design-docs/)

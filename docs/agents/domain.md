---
type: Agent configuration
title: "Domain docs"
description: "How the engineering skills read the glossary, the decisions and the specs; seeded from mattpocock/skills, single-context layout, with the ADRs in docs/decisions."
generated: { by: claude-code/claude-fable-5-2, at: 2026-11-21T11:30:00Z }
status: stable
sources:
  - https://github.com/mattpocock/skills/blob/5863806dd4e15c63174bee4e7515a4256792ecfa/skills/engineering/setup-matt-pocock-skills/domain.md
---
# Domain Docs

How the engineering skills should consume this repo's domain documentation when exploring the codebase.

## Before exploring, read these

- **`GLOSSARY.md`** at the repo root, or
- **`GLOSSARY-MAP.md`** at the repo root if it exists: it points at one `GLOSSARY.md` per context. Read each one relevant to the topic.
- **`docs/decisions/`**: the ADRs of this repository. The skills name `docs/adr/`; that folder does not exist here and must not be created. Start at `docs/decisions/index.md` and read the ADRs that touch the area you're about to work in. Their format, which is fuller than the skills' own, is in `docs/decisions/README.md`.
- **The published documentation** under `docs/concepts/`, `docs/capabilities/` and `docs/reference/` (`.mdx`): current behavior and public API. An ADR is rationale and may describe a design that has since changed; the published page is what holds today.
- **`specs/`**: the spec of the feature you are working on (`NNNN-slug.md`) and its plan (`NNNN-slug.plan.md`). The spec is the source for the Spec axis of a review.

The glossary and the decisions exist; a feature that shipped before `specs/` existed has no spec, and that is not a gap to report. The `/domain-modeling` skill (reached via `/grill-with-docs` and `/improve-codebase-architecture`) adds terms to `GLOSSARY.md` and writes new ADRs into `docs/decisions/` when terms or decisions actually get resolved.

## File structure

Elarion is a single-context repository:

```
/
├── GLOSSARY.md
├── docs/decisions/
│   ├── index.md
│   ├── 0001-event-transaction-phase.md
│   └── 0002-cross-module-communication.md
├── specs/
└── src/
```

There is no `GLOSSARY-MAP.md` and no per-context `docs/adr/` under `src/`.

## Use the glossary's vocabulary

When your output names a domain concept (in an issue title, a refactor proposal, a hypothesis, a test name), use the term as defined in `GLOSSARY.md`. Don't drift to synonyms the glossary explicitly avoids.

If the concept you need isn't in the glossary yet, that's a signal: either you're inventing language the project doesn't use (reconsider) or there's a real gap (note it for `/domain-modeling`).

## Flag ADR conflicts

If your output contradicts an existing ADR, surface it explicitly rather than silently overriding:

> _Contradicts ADR-0010 (the event bus is pub/sub only), but worth reopening because…_

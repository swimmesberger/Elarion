---
type: Architecture Decision Record
title: "ADR-0096: Harness memory is off; the repository is the memory"
description: "Harness memory is off; the repository is the memory."
status: stable
---
# ADR-0096: Harness memory is off; the repository is the memory

- Status: Accepted
- Date: 2026-11-21
- References: the [agentic repository template](https://github.com/swimmesberger/agentic-repo-template)
  (its ADR 0006, "Harness memory is off; the repository is the memory"), whose routing this adopts.

## Context

Coding harnesses — Claude Code, Copilot, Codex, Cursor — each offer a memory that stores notes on one machine or
account and reloads them when a session starts. An agent working on Elarion used it: it saved where an SDK is
installed and a working rule the maintainer had just stated. Both were useful, and both were in the wrong place:

- **They do not travel.** The notes are unreviewed, unversioned and invisible to the next contributor, the next
  machine and the next harness. A rule that changes how the framework is designed belongs where every agent and
  person reads it.
- **They drift.** A fact saved in March is loaded in June with the same confidence, and nothing in the repository
  shows that it was ever written, so nothing prompts anyone to correct it.
- **They split the source of truth.** `AGENTS.md` is the canonical guidance and says so; a second, private store of
  guidance quietly overrides that.

## Decision

Harness memory is switched off in settings committed to the repository, and what it would capture is routed into
tracked files.

- `.claude/settings.json` sets `autoMemoryEnabled: false` (Claude Code then neither reads nor writes its auto-memory
  directory for this project); `.vscode/settings.json` sets `chat.tools.memory.enabled: false`.
- `AGENTS.md` states the rule and the routing ("Repository memory"): a working rule goes into `AGENTS.md`, a design
  decision into an ADR here, behavior into the docs, build and machine setup into `CONTRIBUTING.md`, consumer-visible
  change into the `CHANGELOG`.
- Working notes and session hand-overs go into `.scratch/`, which git ignores, or the OS temp directory.
- Personal preferences and facts about one machine stay in that person's user-level instruction file. They are not
  repository knowledge, and the repository does not try to hold them.

The template's routing is adopted, not its layout. Elarion keeps `docs/decisions/` for ADRs in the format its
`README.md` describes, and `docs/` stays the content source of the documentation website; the template's
`GLOSSARY.md`, `specs/`, `docs/log.md` and document frontmatter are not introduced by this decision.

## Consequences

- What an agent learns is reviewed like any other change, and is there for the next session whatever harness runs it.
- Writing a rule down costs a commit. That is the point: a rule not worth a line in `AGENTS.md` is not worth
  loading into every future session either.
- A memory that lives on a server rather than in a file is not reached by a committed setting; a repository
  administrator switches it off in the hosting service's own settings where it exists.
- The setting applies to sessions started after it is in the checkout. Notes a harness stored earlier are deleted by
  whoever owns them; the two that prompted this decision became the "Decide, do not defer" rule in `AGENTS.md` and
  the machine-failure notes in `CONTRIBUTING.md`.
- History has no dedicated log: `CHANGELOG.md` and git carry it. If that proves too thin for agents, a log is the
  next piece of the template to take.

## Rejected alternatives

- **Keep memory on and ask agents to use it sparingly.** The failure is structural — private, unreviewed, loaded
  whole — and does not depend on how much is stored.
- **A `.claude/settings.local.json` per contributor.** It is ignored by git, so each machine would have to opt out by
  itself and a fresh checkout would not.
- **Adopting the whole template in one step** (`GLOSSARY.md`, `specs/`, an Open Knowledge Format `docs/` bundle, the
  consistency tooling). `docs/` is published as the website, so restructuring it is a separate decision with its own
  costs; the memory rule does not depend on it.

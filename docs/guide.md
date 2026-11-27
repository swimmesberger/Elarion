---
type: Guide
title: "How this knowledge folder is kept"
description: "What lives where, the two kinds of pages under docs/, and the rules for sources, compiled pages, the index and the log."
generated: { by: claude-code/claude-fable-5-2, at: 2026-11-21T11:30:00Z }
status: stable
---
# How this knowledge folder is kept

This folder is what an agent or a person knows about Elarion beyond the code. It is an Open Knowledge Format
bundle (OKF 0.2), kept the way Karpathy's LLM wiki is kept: raw material stays as received, compiled pages
synthesise it, an index lists every page, and every change is logged. Agents read the index and follow
pointers; they do not load the folder.

`index.md` holds only headings and entries, because OKF 0.2 section 8 defines an index that way; everything
that explains the folder is on this page.

## Two kinds of pages

`docs/` is also the content source of the documentation website (`website/`). The file extension says which
kind a page is, and each kind has its own format and its own index:

| | Repository knowledge | Published documentation |
|---|---|---|
| Extension | `.md` | `.mdx` |
| Read by | agents and contributors, in the repository | users of the framework, on the website |
| Says | why and how we work: decisions, research, architecture, the workflow | how the shipped framework behaves and is used |
| Frontmatter | OKF: `type` required | Fumadocs: `title` and `description` required |
| Listed in | the nearest `index.md` | the `meta.json` of its folder |
| Built into the website | no (`website/source.config.ts` reads `**/*.mdx` only) | yes |

OKF defines a bundle as a tree of `.md` files, so the `.md` pages are the bundle in the strict sense; the
`.mdx` pages are reached from it through the "Published documentation" section of [index.md](index.md), which
links every top-level section of the website. `public/` holds the website's static assets and no pages.
`tools/check_repo.py` checks both kinds against their own rules ([ADR-0097](decisions/0097-adopt-the-agentic-repository-layout.md)).

## What lives where

| Kind of knowledge | Lives in | Kind | Loaded |
|---|---|---|---|
| Terms and their meaning, and only that | `../GLOSSARY.md` | primary | on demand, pointed to from `AGENTS.md` |
| Decisions, their trade-offs and rejected alternatives | `decisions/` | primary | on demand |
| What a new feature must do, and how it is built | `../specs/` | primary | per feature |
| How the shipped framework behaves and is used | `concepts/`, `capabilities/`, `reference/`, `tutorial/`, `getting-started/` (`.mdx`) | primary for current behavior and public API | on demand |
| How the framework is shaped and why | `architecture.md` | secondary, compiled from code, ADRs and the package reference | on demand |
| A proposed direction that spans several decisions, before they are ADRs and specs | a `type: Concept` page at the top of `docs/` (`packaged-modules.md`) | primary until its decisions are ADRs, then secondary | on demand |
| How the engineering skills find the tracker and the domain docs | `agents/` | primary, seeded from the skills' setup | by the skills |
| How we work: steps, practices, skills | `workflow.md` | secondary | on demand, pointed to from `AGENTS.md` |
| Research notes: a synthesis of outside material, with its URLs | `research/` | secondary, `type: Research note` | on demand |
| Raw material as received: notes, mails, requirements from outside | `sources/` | primary, never edited | when a compiled page needs checking |
| What changed in this folder and why | `log.md` | newest first, entries never edited | on demand |
| What changed for consumers of the packages | `../CHANGELOG.md` | primary | on demand |
| How to build, test and set up a machine; how to release | `../CONTRIBUTING.md`, `../RELEASING.md` | primary | on demand |
| Files that cannot carry frontmatter (PDF, HTML, office files) | `../raw/`, outside the bundle; create it when needed | primary, never edited | when a source page points there |
| Working notes for one task, handoffs between sessions | `../.scratch/`, ignored by git | ephemeral | per task |

## Frontmatter of a `.md` page

Every `.md` page except `index.md` and `log.md` starts with OKF frontmatter:

```yaml
---
type: Architecture page          # required, free text: Guide, Source, Research note, Architecture Decision Record, ...
title: "Architecture"
description: "One sentence on what the page is for."
generated: { by: claude-code/<model>, at: 2026-11-21T12:00:00Z }   # or human:<id>
verified: [{ by: human:<id>, at: 2026-11-22 }]                      # only once a person has read it
status: draft                    # draft | stable | deprecated; the checker rejects other values
stale_after: 2027-06-30          # optional: date after which the page must be re-checked
sources:                         # optional: where the content comes from
  - https://example.com/origin
superseded_by: 0090              # ADRs only, with status: deprecated
---
```

A page without `verified` counts as unverified, which is the honest default for what an agent wrote. A page
with only machine actors under `verified` is machine-confirmed; one with any `human:` actor is human-reviewed.
The ADRs that existed before the bundle carry no `generated` field: who wrote each of them was not recorded,
and the envelope does not invent it.

## Frontmatter of a `.mdx` page

`title` and `description`, and `icon` (a Lucide icon name) where the navigation shows one. The page is listed
in the `pages` array of the `meta.json` next to it, by its file name without the extension. The rules for
writing published pages are in `.github/instructions/docs.instructions.md`.

## Rules

- **A source enters with an envelope.** A file under `sources/` gets frontmatter (`type: Source`, where it
  came from, when) and its body stays exactly as it arrived. Name it by date and topic:
  `sources/2026-11-21-topic.md`.
- **Sources are never edited.** When a compiled page and a source disagree, the source wins and the page gets
  fixed.
- **Compiled pages cite their sources** under a `Sources` heading at the bottom.
- **Update in the same change.** A pull request that changes behavior updates the published page that
  describes it. A page nobody updates is deleted: stale knowledge is loaded with the same confidence as fresh
  knowledge.
- **Every page is in its index.** A page not listed does not exist for the agent, and a published page not
  in its `meta.json` is missing from the navigation. Adding, renaming or removing a page means editing the
  index in the same change.
- **One log entry per change to the `.md` pages**, added with `tools/log_add.py` (the checker verifies the
  log's form, not that your change has an entry; review does):
  `* **Operation**: what changed and why, with a [link](page.md)` under a `## YYYY-MM-DD` heading, newest day
  and newest entry first. Link the one to three pages that carry the change. Changes to published pages are
  recorded where consumers read them, in `../CHANGELOG.md`, not here.
- **No harness memory.** What an agent would have remembered goes into one of the files above
  ([ADR-0096](decisions/0096-the-repository-is-the-memory.md)).

## Sources

- [Open Knowledge Format, specification 0.2](https://github.com/GoogleCloudPlatform/knowledge-catalog/blob/main/okf/SPEC.md)
- [Andrej Karpathy, LLM wiki](https://gist.github.com/karpathy/442a6bf555914893e9891c11519de94f)
- [Agentic repository template](https://github.com/swimmesberger/agentic-repo-template), whose `docs/guide.md` this page adapts

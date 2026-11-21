---
type: Specification
title: "Spec 0001: repository consistency check"
description: "What the repository checker must detect, so that the layout cannot drift unnoticed."
generated: { by: claude-code/claude-fable-5-2, at: 2026-11-21T11:30:00Z }
status: draft
---
# Spec 0001: repository consistency check

This spec describes `tools/check_repo.py`. It comes from the [agentic repository template](https://github.com/swimmesberger/agentic-repo-template) and is extended here for what Elarion adds to the layout: published `.mdx` pages next to the bundle's `.md` pages, and ADRs in `docs/decisions/`. It is also the worked example of the spec format. The checker's tests build their own fixture repository and do not depend on this file.

## Intent

Every convention of this repository (routers, mirrors, skills, the bundle, the published pages, the decision records, specs) is only worth something while it holds. A maintainer runs one command and learns every place where the layout has drifted, so drift is caught in the pull request that causes it and not by an agent that silently reads the wrong instructions.

## User scenarios

### A consistent repository passes (P1)

- Given a repository in which every convention holds, when the check runs, then it reports that the repository is consistent and exits with success.
- Given a repository with several kinds of drift, when the check runs, then it reports all of them in one run.
- Given a Markdown file without frontmatter in a raw or scratch folder, when the check runs, then it is not reported.
- Given a file in a build-output, dependency or worktree folder, when the check runs, then it is not reported.

### Instruction drift is reported (P1)

- Given a `CLAUDE.md` with content beyond `@AGENTS.md`, when the check runs, then it names that router and fails.
- Given an `AGENTS.md` without a `CLAUDE.md` next to it, when the check runs, then it names the missing router and fails.
- Given a pattern rule whose mirror lists different globs, when the check runs, then it names the mirror and fails.
- Given a mirror whose body repeats the rule instead of pointing at it, when the check runs, then it names the mirror and fails.
- Given a mirror without a pattern rule as its source, when the check runs, then it names the mirror and fails.

### Skill and tool drift is reported (P1)

- Given a skill whose `name` differs from its folder, when the check runs, then it names the skill and fails.
- Given a missing link from a harness-specific path to the shared file, when the check runs, then it names the path and fails.
- Given an MCP server without a URL or command, when the check runs, then it names the server and fails.

### Bundle drift is reported (P1)

- Given a page under `docs/` that its index does not list, when the check runs, then it names the page and fails.
- Given a page without a frontmatter `type`, when the check runs, then it names the page and fails.
- Given a page whose `status` is not an OKF value, when the check runs, then it names the page and the value and fails.
- Given an index with a line that is neither a heading nor an entry, when the check runs, then it names the index and fails.
- Given a log whose dates are not newest first, when the check runs, then it names the log and fails.

### Published-page drift is reported (P1)

- Given a published page without a title or a description, when the check runs, then it names the page and the missing field and fails.
- Given a published page that the navigation file of its folder does not list, when the check runs, then it names the page and fails.
- Given a published page that the navigation file lists as deliberately kept out of the navigation, when the check runs, then it does not fail on it.
- Given a folder of published pages that the navigation file of its parent does not list, when the check runs, then it names the folder and fails.
- Given a navigation entry that names neither a page nor a folder, when the check runs, then it names the navigation file and the entry and fails.
- Given a published page with no bundle frontmatter and no index entry, when the check runs, then it does not fail on it.

### Decision-record drift is reported (P1)

- Given an ADR outside `docs/decisions/`, in the folder public skills would use, when the check runs, then it names that folder and fails.
- Given an ADR whose status line disagrees with its frontmatter status, when the check runs, then it names the ADR and the expected status and fails.
- Given a superseded ADR that does not name its successor, when the check runs, then it names the ADR and fails.
- Given an ADR whose heading number differs from the number in its file name, when the check runs, then it names the ADR and fails.
- Given two ADRs with the same number, when the check runs, then it names the second and the first and fails.

### Spec drift is reported (P2)

- Given a spec without a status, when the check runs, then it names the spec and fails.
- Given an approved spec without a required section, when the check runs, then it names the spec and the section and fails.
- Given an approved spec with a duplicated requirement number, when the check runs, then it names the spec and the number and fails.
- Given a draft spec with an open clarification, when the check runs, then it does not fail on it.

## Requirements

The key words MUST, SHOULD and MAY are to be interpreted as described in RFC 2119 and RFC 8174.

- FR-001: The check MUST report every finding of a run, not only the first.
- FR-002: The check MUST fail when any finding exists and succeed when none does.
- FR-003: Each finding MUST name the file it concerns and SHOULD say what to do about it.
- FR-004: The check MUST verify that every `AGENTS.md` has a `CLAUDE.md` next to it whose only content is `@AGENTS.md`.
- FR-005: The check MUST verify that every pattern rule has a mirror with identical globs and a body of at most 60 words that names the rule's file, and that every mirror has a rule.
- FR-006: The check MUST verify that the shared skills folder and the shared MCP file are linked, or identically copied, for the harnesses that need their own path.
- FR-007: The check MUST verify that every skill has a `SKILL.md` with a `description` and a `name` equal to its folder, in lowercase letters, digits and single hyphens.
- FR-008: The check MUST verify that the MCP file uses the `mcpServers` key and that every server has a known type and the URL or command that type needs.
- FR-009: The check MUST verify that every page of the bundle, the glossary and every spec carry frontmatter with a `type` and, when set, a `status` of draft, stable or deprecated; that every page is listed in its nearest index; that indexes hold only headings and entries whose local links resolve; and that the log is newest first with a linked page in every entry.
- FR-010: The check MUST verify that every spec has a status, and that every approved spec has the required sections in order, unique requirement and success-criterion numbers, and no open clarification.
- FR-011: The check MUST skip dependency, cache, build-output, worktree, scratch and raw folders, without entering them.
- FR-012: The check MUST NOT need anything beyond the language runtime to run.
- FR-013: The check MUST apply the bundle rules of FR-009 to `.md` pages only, and MUST verify that every published `.mdx` page has a title and a description and is listed in the navigation file of its folder, by name or as deliberately excluded; a folder's landing page needs no entry.
- FR-014: The check MUST verify that every folder of published pages is listed in the navigation file of its parent, and that every navigation entry that is not navigation syntax names a page or a folder.
- FR-015: The check MUST fail when a decision record exists in `docs/adr/`.
- FR-016: The check MUST verify, for every ADR in `docs/decisions/`, that its number is unique, that its heading carries the number of its file name, that its status line starts with a known status word which agrees with its frontmatter status, and that a superseded ADR names its successor.

## Success criteria

- SC-001: Every acceptance scenario above has a passing test named after it.
- SC-002: The check finishes in under two seconds on this repository with the website's dependencies installed and the solution built.

## Out of scope

Fixing findings automatically; checking the content or quality of pages; validating links to external URLs and links inside pages (the website's own link check covers the published pages); checking that an `icon` names an existing icon; checking that a change has a log entry, which needs the diff and is left to review.

## Decisions

- [ADR-0097](../docs/decisions/0097-adopt-the-agentic-repository-layout.md): the layout this check keeps, and how the template's layout was adapted.
- [ADR-0096](../docs/decisions/0096-the-repository-is-the-memory.md): the repository is the memory.
- The template's ADRs 0001, 0002, 0003, 0005 and 0007 hold the reasoning for the conventions taken over unchanged: <https://github.com/swimmesberger/agentic-repo-template/tree/main/docs/adr>.

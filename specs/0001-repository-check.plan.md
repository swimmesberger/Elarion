---
type: Plan
title: "Plan 0001: repository consistency check"
description: "How the checker of spec 0001 is built and tested."
generated: { by: claude-code/claude-fable-5-2, at: 2026-11-21T11:30:00Z }
status: draft
---
# Plan 0001: repository consistency check

## Shape

- One script, `tools/check_repo.py`, Python 3.12 standard library only (FR-012). A `check(root)` function returns the list of findings; `main()` prints them and sets the exit code (FR-001, FR-002).
- One function per convention: routers, pattern mirrors, links, skills, MCP file, bundle, published pages, decision records, specs. Each appends findings that start with the file path (FR-003).
- Files are found with a walk that prunes the skipped folder names before entering them (FR-011): a recursive glob would read `website/node_modules` and every `bin/` and `obj/` first.
- The navigation file of a folder of published pages is the website's `meta.json`; its `pages` array is read as JSON. Entries that are navigation syntax (`...`, `---Separator---`, `[Text](url)`) name no file, and `!name` is the website's own syntax for a page kept out of the navigation (FR-013, FR-014).
- The template's code is kept as it is where Elarion does not differ, so a later version of the template's checker can be compared file to file; what Elarion adds is in `site` and `decisions`.
- Frontmatter is parsed with a minimal reader for the YAML subset the repository uses (scalars, flow maps kept as strings, dash lists), so no YAML library is needed.

## Why Python

- The check is the template's, tested, and needs only the standard library; the product build (`dotnet`, `npm`) does not depend on it. [ADR-0097](../docs/decisions/0097-adopt-the-agentic-repository-layout.md) records the alternatives.

## Test seams

- Tests in `tools/tests/test_check_repo.py` build a minimal conforming repository in a temporary folder, independent of this repository's pages, change one thing, and call `check(root)`. They live under `tools/` so they do not mix with a project's own test suite. The public seam is `check(root)`; no test reaches into the per-convention functions.
- One test per acceptance scenario, named after it.

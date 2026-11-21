---
type: Guide
title: "Sources"
description: "Raw material stored as received, with a frontmatter envelope."
generated: { by: claude-code/claude-fable-5-2, at: 2026-11-21T11:30:00Z }
status: stable
---
# Sources

Raw material as received: meeting notes, mails, requirements from outside. Research notes that synthesise
outside material go to `../research/` instead. One file per source, named `YYYY-MM-DD-topic.md`, with
frontmatter (`type: Source`, `title`, `description` saying where it came from) and the body exactly as it
arrived. Never edited. Files that cannot carry frontmatter (PDF, HTML, office files) go to `../../raw/` with a
source page here that points to them. Rules in [../guide.md](../guide.md).

Do not store material that names a consuming application: Elarion stays independent of every downstream
application (`AGENTS.md`), and that holds for what is kept about it.

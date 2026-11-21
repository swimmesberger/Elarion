---
name: adr
description: Write or supersede an architecture decision record in docs/decisions. Use when a decision is hard to reverse, would surprise a future reader, or resolved a real trade-off, and when the user says "record this decision" or "write an ADR".
---
# ADR

The format, the three-part test for whether a decision deserves an ADR, the status words and the rules for
superseding live in `docs/decisions/README.md`. Read it first; this skill is the procedure, not the format.
ADRs live in `docs/decisions/`, never in `docs/adr/`.

1. Apply the three-part test from the README. If it fails, tell the user why and stop.
2. Take the next free number: the highest number in `docs/decisions/` plus one, after checking that no open
   pull request already uses it (`gh pr list --state open --json number,title,files`). Never renumber and
   never reuse a number.
3. Create `NNNN-kebab-case-title.md` in the README's format: the frontmatter envelope
   (`type: Architecture Decision Record`, `title`, `description`, `status`), the `# ADR-NNNN: Title` heading,
   the `- Status:` and `- Date:` bullets, then Context, Decision, Consequences, and the rejected alternatives.
   `- Status: Proposed` with `status: draft` until the user accepts it, then `Accepted` with `stable`.
4. Add one entry to `docs/decisions/index.md` (`make check` fails without it) and one to `docs/log.md` with
   `tools/log_add.py`.
5. Never rewrite an accepted ADR. To change a decision, write a new one and mark the old one as the README's
   "Rules" describe (`Superseded by`, `status: deprecated`, `superseded_by: NNNN`).
6. If the decision introduces or changes a term, update `GLOSSARY.md` in the same change. If it changes a
   rule agents must follow, update `AGENTS.md` or the pattern rule in `.github/instructions/` as well: an ADR
   is rationale, the rule files are what is loaded.
7. A published page (`.mdx`) that cites the ADR links it by its absolute GitHub URL. Link the ADR from the
   pull request description.

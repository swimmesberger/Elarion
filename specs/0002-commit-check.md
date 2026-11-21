---
type: Specification
title: "Spec 0002: commit message check"
description: "What the commit check must accept and reject, so history stays in Conventional Commits form."
generated: { by: claude-code/claude-fable-5-2, at: 2026-11-21T11:30:00Z }
status: draft
---
# Spec 0002: commit message check

## Intent

Commit messages and pull request titles follow Conventional Commits (`AGENTS.md`, "Commits and pull requests"). A maintainer learns before merging, and a committer as soon as they commit, which messages do not conform and how to fix them, without installing anything beyond the language runtime.

## User scenarios

### Messages are checked one at a time (P1)

- Given `feat(auth): add token refresh`, when it is checked, then it passes.
- Given a message without a type, when it is checked, then it is reported with the expected form.
- Given a type outside the list, when it is checked, then it is reported with the allowed types.
- Given a description ending in a period, when it is checked, then it is reported.
- Given a header longer than 100 characters, when it is checked, then it is reported; given one of exactly 100, then it passes.
- Given the commit the Release workflow writes (`release: v0.4.0 [skip ci]`), when it is checked, then it passes.
- Given a body that follows the header without a blank line, when it is checked, then it is reported.

### Work in progress is handled (P2)

- Given a `fixup!` commit, when it is checked by the commit hook, then it passes; when the branch is checked, then it is reported.

### A branch is checked as a whole (P1)

- Given a branch with conforming commits, a non-conforming commit and a merge of main, when the branch is checked against main, then only the non-conforming commit is reported and the check fails.

## Requirements

The key words MUST, SHOULD and MAY are to be interpreted as described in RFC 2119 and RFC 8174.

- FR-001: The check MUST accept headers of the form `<type>[(scope)][!]: <description>` with the types listed in `AGENTS.md`.
- FR-002: The check MUST report a missing or unknown type, a trailing period, a header over 100 characters and a body without a blank line after the header.
- FR-003: The check MUST be able to check a single title, a single message file and every commit of a revision range.
- FR-004: A range check MUST skip merge commits and MUST report `fixup!`, `squash!` and `amend!` commits; a message-file check MUST accept them.
- FR-005: Without a range, the check MUST compare against the remote's default branch, so it works whatever that branch is called.
- FR-006: Each finding SHOULD name the commit and say what to change.
- FR-007: The check MUST NOT need anything beyond the language runtime and git.
- FR-008: The check MUST accept the type `release`, which the Release workflow uses for the commit that publishes a version.

## Success criteria

- SC-001: Every acceptance scenario above has a passing test named after it.
- SC-002: A pull request with a non-conforming commit or title cannot pass CI.

## Out of scope

Lowercase descriptions and the imperative mood (left to review, too many false positives), footers, changelog generation.

## Decisions

- [ADR-0097](../docs/decisions/0097-adopt-the-agentic-repository-layout.md): why the header limit is 100 here and not the template's 72, and why `release` is a type.
- The template's ADR 0008 holds the reasoning for Conventional Commits: <https://github.com/swimmesberger/agentic-repo-template/blob/main/docs/adr/0008-conventional-commits.md>.

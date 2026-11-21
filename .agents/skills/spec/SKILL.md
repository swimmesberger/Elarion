---
name: spec
description: Write or update a feature specification in specs/ and its companion plan. Use before implementing a feature or behaviour change, when the user asks for a spec, requirements or acceptance criteria, or after a clarifying interview.
---
# Spec

The format lives in `specs/README.md`. Read it first; this skill is the procedure.

1. Read `GLOSSARY.md` and the ADRs the feature touches (`docs/decisions/index.md`). For a change to a feature
   that already ships, read its published page under `docs/concepts/` or `docs/capabilities/`: it is the
   baseline, and the spec covers the change, not the whole feature. Use the glossary's terms; propose new ones
   instead of inventing synonyms.
2. If the intent is unclear, ask the user one question at a time until you can state it in one paragraph. Do
   not guess: write `[NEEDS CLARIFICATION: ...]` for what stays open.
3. Take the next free number and create `specs/NNNN-slug.md` with `status: draft` and the sections from the
   README, in order. Requirements are numbered `FR-NNN`, one testable sentence each, RFC 2119 keywords in
   capitals.
4. Keep the how out. A spec names the public surface a developer will see (an attribute, a package, a
   diagnostic id, a wire shape) and nothing behind it. Generator design, internal types, data layout, test
   seams and benchmarks go into `specs/NNNN-slug.plan.md`; anything hard to reverse becomes an ADR through the
   `adr` skill.
5. Run `make check`.
6. Stop and ask the user to approve the spec before any code is written. On approval, set `status: stable`;
   `make check` then also checks the sections, the numbering and open clarifications. When an issue tracks the
   work, link the spec from the issue.

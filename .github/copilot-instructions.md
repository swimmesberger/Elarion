The agent instructions for this repository live in [`AGENTS.md`](../AGENTS.md) at the repository root. Read it
first; this file exists only so Copilot surfaces that do not read `AGENTS.md` still find the way.

Rules for one kind of file are pattern rules in [`.github/instructions/`](instructions/), each scoped by its
`applyTo`: C# coding standards (`csharp`), package layout and framework invariants (`framework`), source
generator conventions (`generators`), tests (`tests`), documentation (`docs`), workflows and publishing
(`workflows`). They are the source of those rules; add or change guidance in `AGENTS.md` or in a pattern rule,
not here.

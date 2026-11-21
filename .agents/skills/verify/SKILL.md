---
name: verify
description: Run the checks this repository defines (repository consistency, commit messages, and the build and tests of what changed) and report what fails. Use before committing, after finishing a task, or when asked whether the build is green.
---
# Verify

1. From the repository root run `make check`, then `make check-commits` when the branch has a remote. If
   `make` is unavailable, run the commands the Makefile's `check` and `check-commits` targets run.
2. Run the validation of what the branch changed, with the commands of the "Commands" section of `AGENTS.md`
   and no others:
   - `src/`, `tests/`, `samples/` or the build files: the .NET build and tests.
   - an npm package under `src/elarion-*`: its `npm ci`, build, test and `npm pack --dry-run`.
   - `docs/` or `website/`: the website build and `npm run check:links`.
   Nothing else changed: say that step 2 was not needed, and why.
3. For every failing command, report the command and the last twenty lines of its output. Report a command
   you could not run (no SDK, no Docker) as not run, never as passed.
4. Do not fix anything as part of this skill. Fixing is a separate task with its own tests.

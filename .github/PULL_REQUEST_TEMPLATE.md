<!-- Title: Conventional Commits, for example `feat(jsonrpc): add batch cancellation`. It becomes the commit on a squash merge. -->

## Summary

<!-- What does this PR change, and why? -->

## Type of change

- [ ] Bug fix
- [ ] New feature
- [ ] Breaking change
- [ ] Documentation
- [ ] Refactor / internal

## Spec or issue

<!-- Link the spec in `specs/` and name the FR numbers this change covers, or the issue. A change to a feature
     that has no spec says so. -->

## Decisions

<!-- Link any ADR in `docs/decisions/` this change adds or supersedes. -->

## Checklist

- [ ] `dotnet build Elarion.slnx -c Release` passes (warnings are errors).
- [ ] `dotnet test` passes; new/changed behavior is covered by tests, one per acceptance scenario of the spec.
- [ ] Source-generation changes include generator tests under `tests/Elarion.Tests/Generators`.
- [ ] New analyzer diagnostics are recorded in `AnalyzerReleases.Unshipped.md`.
- [ ] Documentation under `docs/` is updated for any user-visible change.
- [ ] `CHANGELOG.md` is updated under **Unreleased**.
- [ ] The change stays domain-neutral and respects the package [architecture boundaries](../CONTRIBUTING.md#architecture-boundaries).
- [ ] `make check` passes and the commits follow Conventional Commits.
- [ ] Spec, glossary, `AGENTS.md` and pattern rules are updated if they changed; routers and mirrors untouched.
- [ ] `.md` pages added under `docs/` are in their index and in `docs/log.md`; `.mdx` pages are in their `meta.json`.

## Notes for reviewers

<!-- Anything that needs special attention, trade-offs, or follow-ups. -->

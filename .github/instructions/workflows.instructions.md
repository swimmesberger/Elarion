---
description: 'Rules for GitHub Actions workflows and for publishing'
applyTo: '.github/workflows/**'
---

# GitHub Actions workflows and publishing

The rules for `.github/workflows/`. This file is their source; `AGENTS.md` points here.

## Action versions

Whenever adding or editing a workflow, look up the latest release of **every** `uses:`
action first and pin it. Do not rely on remembered versions: stale action pins cause runner deprecation
warnings and avoidable breakage.
Look a release up with `gh api repos/<owner>/<action>/releases/latest --jq .tag_name`.

## Publishing

Trusted publishing uses OIDC. `<VersionPrefix>` in `Directory.Build.props` is the source of truth for the
next version; see [`RELEASING.md`](../../RELEASING.md). Pushes to `main` publish
`{VersionPrefix}-preview.{run}.{attempt}`. The Release workflow promotes the current version to stable,
synchronizes doc version literals, rolls the changelog, tags `v<version>`, bumps the next patch, and creates
the GitHub Release that triggers stable publishing. It uses a GitHub App intentionally to bypass protection
and trigger the downstream workflow; keep other workflow changes tokenless unless a registry requires
credentials.

## The repository checks

`check.yml` runs `make check` (the tools' tests and `tools/check_repo.py`) on every push and pull request;
`commits.yml` checks the title and the commits of a pull request with `tools/check_commits.py`. Both need only
Python's standard library. Keep them apart from `ci.yml`, which builds and tests the framework.

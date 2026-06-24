# Releasing Elarion

Elarion versions are driven by **`<VersionPrefix>` in `Directory.Build.props`**, which is the
single source of truth for the *next* version. You declare the next version there once; every push
to `main` publishes it as a preview, and a single workflow promotes it to a stable release.

## The model

| Stage | `VersionPrefix` | Published | Docs reference |
| --- | --- | --- | --- |
| Working toward the next version | `0.1.1` | — | last release |
| Push to `main` | `0.1.1` | `0.1.1-preview.{run}.{attempt}` (CI, [`publish.yml`](.github/workflows/publish.yml)) | last release |
| Run the **Release** workflow | `0.1.1` | **stable `0.1.1`**, git tag `v0.1.1` | **`0.1.1`** |
| After release (automatic) | `0.1.2` | `0.1.2-preview.*` on next push | `0.1.1` |

The docs always pin the **latest stable release** because the release workflow rewrites every
`Version="…"` package-reference literal in `README.md` and `docs/**` as part of cutting the release.
Preview versions are never written into the docs.

> A preview package (`0.1.1-preview.42`) and the stable package (`0.1.1`) are distinct versions in
> NuGet/npm — "releasing the preview" means rebuilding and publishing a stable `0.1.1` from the same
> source, not re-labelling the preview artifact.

## Cutting a release

1. Make sure `main` is green and `<VersionPrefix>` is the version you want to ship (bump it to a
   minor/major by hand if the next release is more than a patch).
2. Move the changes you're shipping under `## [Unreleased]` in [`CHANGELOG.md`](CHANGELOG.md).
3. Run the **Release** workflow (Actions → *Release* → *Run workflow*). Leave the version input
   empty to use `VersionPrefix`, or type an explicit `MAJOR.MINOR.PATCH` to override.

The workflow ([`release.yml`](.github/workflows/release.yml)) then:

- syncs the doc version literals to the release version and rolls the changelog
  `[Unreleased]` → `[<version>] - <date>` (via [`scripts/release.mjs`](scripts/release.mjs));
- commits and tags `v<version>` on `main`;
- bumps `VersionPrefix` to the next patch and commits it (so previews keep climbing);
- creates a GitHub Release, which fires the `release: published` job in `publish.yml` to build, test,
  and push the stable NuGet + npm packages from the tag.

Both release commits carry `[skip ci]`, so they don't trigger a redundant preview publish.

## One-time setup: release identity

The workflow pushes to the protected `main` branch and creates a release that must trigger
`publish.yml`. The default `GITHUB_TOKEN` can do neither (it can't bypass branch protection, and
events it creates don't start other workflows), so the release runs as a **GitHub App**:

1. Create a GitHub App (org or personal) with repository permission **Contents: write**, and install
   it on this repository.
2. Add the App to the branch's bypass list:
   - **Rulesets** (Settings → Rules → Rulesets → your `main` ruleset → **Bypass list** → *Add
     bypass*): select the App and set its bypass mode to **Always** — the release pushes directly
     to `main`, so *Pull request* mode would not exempt it. The App must already be installed on the
     repo to appear in the picker. If a separate ruleset targets tags (`refs/tags/*`) and restricts
     creation, add the App to that ruleset's bypass list too — the workflow pushes a `v<version>` tag.
   - **Classic protection:** add the App under *Allow specified actors to bypass required pull
     requests* (and the push-restriction allowlist if enabled).
3. Add `RELEASE_APP_ID` as a repository **variable** (the App ID is not sensitive) and
   `RELEASE_APP_PRIVATE_KEY` as a repository **secret** (the generated private key, full PEM
   contents).

A fine-grained PAT with `contents: write` from a user on the bypass list works as a drop-in
alternative — swap the `actions/create-github-app-token` step for the PAT secret in
`release.yml`. A GitHub App is preferred (not tied to a person, scoped, no expiry churn).

## Manual scripts

The release steps are plain Node scripts you can run locally to preview the edits:

```bash
node scripts/release.mjs prepare 0.1.1     # set VersionPrefix, sync docs, roll changelog
node scripts/release.mjs notes 0.1.1       # print the changelog notes for a version
node scripts/release.mjs bump-prefix 0.1.2 # set VersionPrefix for the next dev cycle
```

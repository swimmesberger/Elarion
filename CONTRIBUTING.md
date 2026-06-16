# Contributing to Elarion

Thanks for your interest in improving Elarion. This document covers how to build, validate, and
extend the framework, the architectural boundaries that keep it reusable, and how releases are
published.

By participating in this project you agree to abide by the [Code of Conduct](CODE_OF_CONDUCT.md).

## Getting started

```bash
git clone https://github.com/swimmesberger/Elarion.git
cd Elarion
dotnet restore Elarion.slnx
dotnet build Elarion.slnx --configuration Release
```

You need the **.NET 10 SDK** (see [`global.json`](global.json) for the exact version) and, for the
TypeScript client generator, **Node.js 18+**.

## Validation

Run the same checks CI runs before opening a pull request:

```bash
# .NET
dotnet build Elarion.slnx --configuration Release
dotnet test --project tests/Elarion.Tests/Elarion.Tests.csproj --configuration Release
dotnet pack Elarion.slnx --configuration Release --no-build

# TypeScript client generator
cd src/elarion-jsonrpc-client-generator
npm ci
npm run build
npm test
npm pack --dry-run
```

When changing the TypeScript generator, also generate from a representative `rpc-schema.json` and
type-check the emitted `rpc-types.ts`, `rpc-schemas.ts`, and `rpc-client.ts` under
`moduleResolution: NodeNext`.

`TreatWarningsAsErrors` is enabled, so a warning fails the build. Keep the tree warning-clean.

## Architecture boundaries

Elarion is a reusable framework. Keep it independent from any downstream application — do not
mention, depend on, or optimize for a consuming app by name. Application-specific domain code,
database conventions, UI frameworks, and deployment quirks belong in consuming repositories.

- Core framework packages stay reusable and domain-neutral.
- `Elarion.Abstractions` must not depend on runtime integration packages.
- `Elarion` may depend on abstractions but should avoid ASP.NET Core, EF Core, and transport
  concerns.
- `Elarion.JsonRpc` owns JSON-RPC runtime contracts, dispatch, telemetry, and schema export without
  ASP.NET Core dependencies.
- `Elarion.AspNetCore` owns HTTP/JSON-RPC integration and ASP.NET Core-specific behavior.
- EF Core packages own only EF-specific marker APIs and source generation.
- Source generators emit deterministic, inspectable code and fail with diagnostics for unsupported
  patterns. Do not add runtime reflection scanning where compile-time generation is feasible.

See [`docs/reference/packages.mdx`](docs/reference/packages.mdx) for the full package map.

## Source generators

- Add or update Roslyn generator tests in [`tests/Elarion.Tests/Generators`](tests/Elarion.Tests/Generators)
  for any source-generation change.
- Record new analyzer diagnostics in `AnalyzerReleases.Unshipped.md` and move them to
  `AnalyzerReleases.Shipped.md` on release. Diagnostic IDs are a stable contract.
- Prefer a clear build-time diagnostic over a silent runtime failure.

## TypeScript client generator

The npm package lives in [`src/elarion-jsonrpc-client-generator`](src/elarion-jsonrpc-client-generator).

- Keep generated output deterministic.
- Keep the direct API ergonomic (`rpc.clients.get(params, options)`) and the generic transport
  primitive available for advanced adapters.
- Preserve tuple-aware batch typing through generated `$request` helpers and `$batch`.
- Use generated Zod schemas for runtime validation by default, with opt-outs/transform hooks.
- Generated code must type-check under modern browser projects and NodeNext projects.
- Do **not** import React, TanStack, Vite, or any downstream framework from generated runtime code.

## Adding public framework surface

Before adding new public API:

- Keep application-specific generators, handlers, modules, and infrastructure out of the Elarion
  packages.
- Add or update Roslyn generator tests for source-generation behavior.
- Add or update schema generation tests when changing the JSON-RPC exporter, build targets, or
  host-launching code.
- Extend the generated JSON-RPC client deliberately when repeated frontend adapter patterns emerge.
- Update the relevant page under [`docs/`](docs/) — documentation is part of the change, not a
  follow-up.

## Commit and PR guidelines

- Use clear, conventional commit messages (e.g. `feat(jsonrpc): …`, `fix(scheduler): …`,
  `docs: …`).
- Keep pull requests focused; describe the motivation and the user-visible effect.
- Ensure CI is green and documentation is updated before requesting review.

## Publishing

Publishing uses GitHub Actions trusted publishing / OIDC, so no long-lived registry tokens are
stored in the repository. The `publish.yml` workflow runs for pushes to `main`, published GitHub
releases, and manual dispatches.

### Versioning

- Pushes to `main` publish **preview** packages using `VersionPrefix` from
  [`Directory.Build.props`](Directory.Build.props) plus the workflow run identity, e.g.
  `0.2.0-preview.123.1`. Keep `VersionPrefix` set to the next intended release line.
- Release tags may be prefixed with `v` (e.g. `v0.1.0` publishes `0.1.0`).
- Manual dispatches can publish any explicit SemVer version; an empty version input uses the
  automatic preview version.
- Prerelease labels such as `v0.2.0-preview.1`, `v0.2.0-beta.1`, and `v0.2.0-rc.1` are supported.
  NuGet receives them as prerelease packages. npm publishes stable versions with the `latest`
  dist-tag and prerelease versions tagged with the first prerelease label (e.g. `preview`).

### NuGet trusted publishing

| Field | Value |
| --- | --- |
| Repository Owner | `swimmesberger` |
| Repository | `Elarion` |
| Workflow File | `publish.yml` |
| Environment | Leave empty unless the workflow is updated to use a GitHub environment. |

Requires the `NUGET_USER` repository variable (or secret) — the NuGet.org profile name used by
`NuGet/login` for trusted publishing.

### npm trusted publishing

The npm trusted publisher for `@swimmesberger/elarion-jsonrpc-client-generator`:

| Field | Value |
| --- | --- |
| Provider | GitHub Actions |
| Repository Owner | `swimmesberger` |
| Repository | `Elarion` |
| Workflow File | `publish.yml` |
| Environment | Leave empty unless the workflow is updated to use a GitHub environment. |

Keep workflow changes tokenless unless a registry explicitly requires otherwise.

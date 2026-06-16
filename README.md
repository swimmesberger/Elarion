# Elarion

Elarion is a .NET application framework for module-based handler pipelines, compile-time registration, JSON-RPC hosting, scheduled jobs, and optional Entity Framework Core source generation.

## Packages

| Package | Purpose |
| --- | --- |
| `Elarion.Abstractions` | Implementation-neutral attributes, handler contracts, result types, module metadata, scheduling contracts, and source-generation triggers. |
| `Elarion` | Runtime primitives for handler caches, decorators, modules, resilience policies, current-user access, and the in-memory scheduler. |
| `Elarion.JsonRpc` | Transport-neutral JSON-RPC dispatcher, envelopes, result/error types, telemetry, schema export, and source-generation trigger. |
| `Elarion.AspNetCore` | ASP.NET Core JSON-RPC endpoint mapping, batch execution, current-user middleware, and HTTP transport support. |
| `Elarion.AspNetCore.SchemaGeneration` | MSBuild package for generating JSON-RPC schema files during `dotnet build`. |
| `Elarion.EntityFrameworkCore` | Marker attributes for EF Core entity and DbSet generation. |
| `Elarion.Generators` | Roslyn generators for handlers, validators, services, modules, RPC method maps, resilience policies, and scheduled jobs. |
| `Elarion.EntityFrameworkCore.Generators` | Roslyn generator for DbSet properties and entity configuration application. |
| `@swimmesberger/elarion-jsonrpc-client-generator` | TypeScript CLI/library that converts exported Elarion JSON-RPC schemas into method contracts, Zod result schemas, and a portable fetch client. |

## Telemetry

Elarion emits OpenTelemetry-compatible tracing and metrics through `System.Diagnostics` sources/meters for JSON-RPC, scheduling, handler caching, and resilience. Hosts register the sources they want to collect; the framework runtime packages do not require an OpenTelemetry SDK dependency. See [docs/elarion.md](docs/elarion.md#telemetry-and-tracing) for source names and examples.

## Development

```bash
dotnet restore Elarion.slnx
dotnet build Elarion.slnx --configuration Release
dotnet test --project tests/Elarion.Tests/Elarion.Tests.csproj --configuration Release
dotnet pack Elarion.slnx --configuration Release --no-build

cd src/elarion-jsonrpc-client-generator
npm ci
npm run build
npm test
```

## Publishing

The publish workflow runs for pushes to `main`, published GitHub releases, and manual dispatches. NuGet and npm publishing both use trusted publishing/OIDC, so no long-lived package registry tokens are stored in GitHub.

The NuGet trusted publishing policy should match:

| Field | Value |
| --- | --- |
| Repository Owner | `swimmesberger` |
| Repository | `Elarion` |
| Workflow File | `publish.yml` |
| Environment | Leave empty unless the workflow is later updated to use a GitHub environment. |

It expects this repository variable:

| Name | Type | Used for |
| --- | --- |
| `NUGET_USER` | Variable preferred, secret also supported | NuGet.org profile name used by `NuGet/login@v1` for trusted publishing. |

The npm trusted publisher for `@swimmesberger/elarion-jsonrpc-client-generator` should match:

| Field | Value |
| --- | --- |
| Provider | GitHub Actions |
| Repository Owner | `swimmesberger` |
| Repository | `Elarion` |
| Workflow File | `publish.yml` |
| Environment | Leave empty unless the workflow is later updated to use a GitHub environment. |

Pushes to `main` publish preview packages using `VersionPrefix` from `Directory.Build.props` and the workflow run identity, for example `0.2.0-preview.123.1`. Manual dispatches use the same automatic preview version when the version input is left empty. Keep `VersionPrefix` set to the next intended release line before merging changes that should produce previews.

Release tags may be prefixed with `v`; for example, `v0.1.0` publishes version `0.1.0`. Manual dispatches can also publish any explicit SemVer version.

Prerelease versions are supported with SemVer labels such as `v0.2.0-preview.1`, `v0.2.0-beta.1`, or `v0.2.0-rc.1`. NuGet receives them as prerelease packages. npm publishes stable versions with the `latest` dist-tag and prerelease versions with the first prerelease label as the dist-tag, for example `preview` for `0.2.0-preview.1`.

## Documentation

See [docs/elarion.md](docs/elarion.md) for the full developer guide.

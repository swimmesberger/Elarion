# CLAUDE instructions for Elarion

Elarion is a reusable .NET framework. Keep it independent from downstream applications.

## Core boundaries

- Do not add consuming-app names, domain rules, deployment conventions, or app-specific dependencies.
- Keep framework packages reusable and transport-neutral where intended.

### Package ownership

- `Elarion.Abstractions`: contracts, attributes, module metadata, scheduling triggers.
- `Elarion`: runtime primitives and decorators, no ASP.NET Core/EF Core coupling.
- `Elarion.JsonRpc`: transport-neutral JSON-RPC dispatcher, envelopes, results/errors, telemetry, schema export, RPC map trigger.
- `Elarion.AspNetCore`: HTTP endpoint integration and ASP.NET Core-specific behavior.
- `Elarion.AspNetCore.SchemaGeneration`: build-time schema generation targets/tooling.
- `Elarion.Generators` + `Elarion.EntityFrameworkCore.Generators`: deterministic source generators.

## C# coding style

- Use latest repo-supported C# (currently C# 14).
- Default to `sealed` classes; only leave extensible types unsealed intentionally, with XML docs explaining inheritance intent.
- Prefer immutable records for DTOs/options.
- Prefer nominal (property-based) records for public DTOs (`required` + `init` for non-nullable fields).
- Private instance fields use `_camelCase`; locals/parameters use `camelCase`; public members use PascalCase.
- Prefix interfaces with `I` and generic type parameters with `T`.
- Use file-scoped namespaces and keep opening braces on the same line.
- Prefer early returns, pattern matching, switch expressions, and `nameof` where clearer.
- Follow `.editorconfig` as formatting source of truth.

## API/docs/comments

- Add XML docs for public APIs.
- Use normal comments sparingly for intent/constraints/tradeoffs; avoid obvious comments.
- When behavior exists for AOT/trimming/source-gen compatibility, document the reason.

## Nullability and async

- Keep nullable reference types strict; validate at boundaries.
- Prefer `is null` / `is not null`.
- Do not add fire-and-forget tasks.
- Flow cancellation tokens through async calls; do not use `CancellationToken.None` without clear justification.
- Treat expected cancellation as cancellation, not an error.

## JSON-RPC and generators

- JSON-RPC runtime contracts belong in `Elarion.JsonRpc`, not `Elarion.AspNetCore`.
- Keep generated output deterministic and inspectable.
- Prefer compile-time generation over runtime reflection scanning.
- Keep linker/AOT/trimming safety in mind for public APIs and startup paths.

## Tests and validation

- Add regression tests for bug fixes.
- Keep tests deterministic and follow nearby naming/style conventions.
- For generator changes, update generator tests and validate deterministic output.

Common validation commands:

```bash
dotnet restore Elarion.slnx
dotnet build Elarion.slnx --configuration Release
dotnet test --project tests/Elarion.Tests/Elarion.Tests.csproj --configuration Release
dotnet pack Elarion.slnx --configuration Release --no-build
```

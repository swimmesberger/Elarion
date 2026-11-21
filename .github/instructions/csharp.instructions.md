---
description: 'C# coding standards for the framework code'
applyTo: '**/*.cs'
---

# C# coding standards

The rules for every `.cs` file in this repository. This file is their source; `AGENTS.md` points here, and
`.claude/rules/csharp.md` scopes it for Claude Code. Mechanical formatting is in `.editorconfig`.

### Style

- Use the latest C# every target framework supports: runtime packages build for `net10.0` and `net11.0`, so source
  is C# 14. C# 15 features (unions, `closed`, collection-expression arguments, extension indexers, labeled
  `break`/`continue`) only inside `#if NET11_0_OR_GREATER`, until `net10.0` is dropped.
- Make classes `sealed` unless they intentionally support inheritance; document the extensibility contract.
- Use immutable records for DTOs, options, and data containers. Prefer nominal property-based records with
  `required` and `init` properties (nullable `init` for optional values); reserve positional records for tiny
  internal helpers and tests.
- Use read-only collection types (`IReadOnlyList<T>`, `ImmutableArray<T>`, and similar) for immutable public
  surfaces. Use concise primary constructors for DI services.
- Mint general identifiers with `Guid.CreateVersion7()`, not `Guid.NewGuid()`. Its important benefit for
  persisted values is index locality plus a debuggable timestamp; use it for never-persisted identifiers too
  for consistency. Never use v7 where guessability matters: it leaks creation time and has 74 random bits.
  Capability-style codes, tokens, and sharing values use v4 or a real CSPRNG token, and an id is never the
  only authorization gate.

## Naming

- Use PascalCase for types, methods, and public members; `_camelCase` for private instance fields; camelCase
  for locals and parameters; PascalCase for constants/static readonly members. Interfaces begin with `I` and
  type parameters with `T`.

## Formatting

- `.editorconfig` is the source of truth. Use file-scoped namespaces, single-line usings, and opening braces
  on the same line.
- Prefer early returns to deep nesting. Use pattern matching, switch expressions, and `nameof` when they
  clarify intent; leave the final `return` on its own line.

## Comments and public API docs

- Public APIs need XML documentation; include `<example>`/`<code>` for non-obvious APIs. Comments explain
  intent, constraints, or non-obvious trade-offs, never restate code. Document *why* for a non-obvious
  compatibility, source-generation, AOT, or performance pattern.

## Nullable reference types

- Non-nullable is the default. Validate nullability at entry points, use `is null`/`is not null`, and trust
  the type system instead of adding redundant null checks.

## Async and background work

- Do not fire-and-forget unobserved work. Thread `CancellationToken` through async flows; use
  `CancellationToken.None` only with a documented reason. Long-lived work belongs to a host-managed
  `IHostedService`, scheduler, explicit queue, or loop, not a hidden helper. Handle expected
  `OperationCanceledException` deliberately and do not log it as an error.
- Hot-path async methods return `ValueTask`/`ValueTask<T>`; keep `PoolingAsyncValueTaskMethodBuilder` where a method
  suspends on I/O. The `net11.0` build compiles with runtime async, where the compiler ignores the builder; the `net10.0`
  build uses compiler state machines, where it keeps suspension allocation-free (ADR-0092).
- In generic code, inspect a response with `ResultInspection.IsSuccess`/`IsFailure`/`TryGetError`, never an inline
  `response is IResultLike { … }` pattern: inside a runtime-async method body the inline pattern boxes a struct
  `Result<T>` on every call.

## Telemetry

- Follow OpenTelemetry semantic conventions when they exist. Duration histograms record floating-point
  **seconds** (`s`), never milliseconds, and use semconv bucket boundaries through
  `InstrumentAdvice<double>`; SDK default buckets are millisecond-scaled and unsuitable for seconds-valued
  histograms. `Record*` helpers take `TimeSpan` so this unit decision lives in one place per meter.
- Reuse semconv names such as `rpc.server.call.duration`; namespace custom names and attributes with
  `elarion.*`. Metric tags stay bounded (type, operation, outcome), never payloads, keys, or user identity.
  Put high-cardinality identity on spans only. Explicitly unit-suffixed span tags such as `*_ms` are
  self-describing and exempt from the seconds rule.

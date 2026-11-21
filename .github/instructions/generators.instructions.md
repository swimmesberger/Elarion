---
description: 'Conventions for incremental source generators and their tests'
applyTo: 'src/*.Generators/**,tests/Elarion.Tests/Generators/**'
---

# Source generator conventions

The rules for the `*.Generators` projects and their tests. This file is their source; `AGENTS.md` points here.

Generators run on every keystroke, so incrementality is correctness. Read
[ADR-0006](../../docs/decisions/0006-incremental-source-generator-conventions.md) for rationale, and copy a
current reference generator (`AppModuleDiscoveryGenerator`, `ElarionManifestGenerator`,
`ModuleDefaultServicesGenerator`, or a registration generator), not the old “scan and emit” shape.

When adding or changing a generator:

- **Discover through a syntax provider, never by scanning `CompilationProvider`.** Use
  `ForAttributeWithMetadataName` for attribute triggers (branch on `ctx.TargetSymbol` for methods versus
  types), or a predicate-filtered `CreateSyntaxProvider` for structural triggers. Never enumerate
  `SyntaxTrees` from `RegisterSourceOutput(CompilationProvider, …)`.
- **Make every pipeline value value-equatable.** `ImmutableArray<T>.Equals` is reference equality; use
  `EquatableArray<T>` for every collection field, including nested collections. Carry immutable facts and
  fully qualified strings, not `ISymbol`, `Compilation`, `SyntaxNode`, `Location`, or `object?[]` through a
  pipeline.
- **Treat diagnostics as data.** Transforms stay pure and return `EquatableArray<DiagnosticInfo>` built with
  `DiagnosticInfo.Create` and `LocationInfo`; report diagnostics only in the final
  `RegisterSourceOutput` callback. Do not call `spc.ReportDiagnostic` from a transform.
- **Reuse shared discovery.** Use `ModuleProviders.CollectModules`, `ModuleScanner.FindBest`/`IsInScope`, and
  `ModuleProviders.HasTrigger`. Do not hand-roll module scanning, namespace matching, or assembly-trigger
  detection.
- **Make emitted output deterministic and prove caching.** Provider order is unspecified, so preserve
  emit-time sorting. Tag collect/combine nodes with `.WithTrackingName`; add
  `GeneratorCacheAssert.ReusesOutputsAfterIrrelevantEdit`; and run the affected `*GeneratorTests.cs` suite.
  Generated text is a byte-identical contract, not an incidental implementation detail.
- **Emit concrete AOT-safe code.** Avoid reflection and open generics in generated hot paths. For
  cross-assembly discovery, emit/read assembly metadata through `MetadataReferencesProvider` using the
  existing Elarion/EF manifests; never scan referenced symbol trees.
- Add new analyzer diagnostics to `AnalyzerReleases.Unshipped.md` (RS2008) and preserve stable diagnostic
  behavior. Prefer a clear compile-time error over a delayed, silent runtime fallback.

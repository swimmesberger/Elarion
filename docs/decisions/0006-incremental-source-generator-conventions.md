# ADR-0006: Incremental source generator conventions

- Status: Accepted
- Date: 2026-06-23
- Related: [Source generation](../source-generation.mdx) (user-facing reference),
  [ADR-0003](0003-decorator-attachment-predicates.md) (a generator emits the predicate plumbing),
  the `Elarion.Generators` and `Elarion.EntityFrameworkCore.Generators` generator projects (their
  analyzer DLLs ship bundled inside the `Elarion` and `Elarion.EntityFrameworkCore` runtime packages).

## Context

Source generation is the mechanism behind everything in Elarion: handlers, services, validators,
scheduled jobs, event consumers, resilience policies, module bootstrappers, DbSets, and keyset
pagination are all discovered at compile time and emitted as ordinary, inspectable registration code.
There are ~12 generators across two packages, and there will be more.

A source generator does not run once. The IDE re-runs it **on every keystroke** in any project that
references it, and the build runs it on every compile. Roslyn's `IIncrementalGenerator` is designed
for exactly this: it models generation as a pipeline of cached nodes, and re-runs only the nodes whose
inputs actually changed. Used correctly, typing in one file re-runs almost nothing; used incorrectly,
typing in one file re-runs *all* discovery and emission for the whole compilation.

Several of the early generators were written in the natural-but-wrong shape:

```csharp
context.RegisterSourceOutput(context.CompilationProvider, (spc, compilation) => {
    foreach (var tree in compilation.SyntaxTrees) {            // every file
        var model = compilation.GetSemanticModel(tree);        // bind the whole tree
        foreach (var decl in tree.GetRoot().DescendantNodes()) { /* discover + emit */ }
    }
});
```

This compiles, passes tests, and emits correct code — and it re-runs the **entire** O(all-source)
semantic scan on **every edit**, because `CompilationProvider` produces a *new* `Compilation` value on
every change, so the output node's cache key never matches. A 2026-06 review found six generators in
this shape, several scanning every syntax tree twice, plus a quieter problem: even generators that used
a proper `SyntaxProvider` carried **non-value-equatable** pipeline models, so their cached nodes
compared unequal every run and re-emitted anyway.

The fixes were mechanical once understood, but the failure mode is invisible — there is no compiler
warning, no failing test, just an IDE that gets slower as the codebase grows. This ADR records the
conventions that prevent re-introducing it, and **why** each is the way it is, so the next generator is
correct by construction rather than by review.

## Decision

A generator added to Elarion follows six conventions. Each has a concrete rejected alternative.

### 1. Incremental-first, attribute-indexed discovery — never off `CompilationProvider`

Implement `IIncrementalGenerator` and discover work through the **syntax provider**, not by registering
output against the compilation:

- Attribute-triggered generators use `context.SyntaxProvider.ForAttributeWithMetadataName(...)` (FAWMN).
  This is the fast path: Roslyn maintains a per-tree index of which trees contain which attributes, so
  the predicate/transform run **only for changed annotated declarations**, not the whole compilation.
  A single FAWMN call matches the attribute on **any** target kind — `[ScheduledJob]`/`[ConsumeEvent]`
  apply to both methods and types, and one provider handles both (branch on `ctx.TargetSymbol` in the
  transform).
- Convention-triggered generators (a *base type*, not an attribute — e.g. a validator deriving from
  `AbstractValidator<T>`, or a handler implementing `IHandler<,>`) use a predicate-filtered
  `context.SyntaxProvider.CreateSyntaxProvider(predicate, transform)` where the predicate cheaply
  narrows by syntax (`node is ClassDeclarationSyntax { BaseList: not null }`) before the transform pays
  for semantics.

**Rejected: `RegisterSourceOutput(context.CompilationProvider, …)` for discovery, with a
`foreach (compilation.SyntaxTrees)` scan.** It re-binds and re-walks every source file on every
keystroke with no caching. The reference generators (`AppModuleDiscoveryGenerator`,
`ElarionManifestGenerator`, `ModuleDefaultServicesGenerator`) all use FAWMN — copy them, not the old
shape. `CompilationProvider` is acceptable only when **projected to a small equatable value** (see
conventions 4 and 5), never as the thing you scan in the output callback.

### 2. Every pipeline value is value-equatable — the `EquatableArray<T>` rule

The incremental cache compares each node's output to its previous output **by equality**; if they are
equal, every downstream node (including emission) is skipped. So every type that flows through the
pipeline must have correct **structural** value equality. The traps:

- **`ImmutableArray<T>` is not value-equatable.** Its `Equals` compares the *underlying array
  reference*, not the contents. A record with an `ImmutableArray<string>` field re-built each run is
  never equal to the previous one, so it silently defeats the cache. This is *the* canonical
  incremental-generator pitfall. Use `EquatableArray<T>` (a thin wrapper with sequence equality and a
  stable hash) for every collection field. Element types must themselves be value-equatable — nest
  `EquatableArray<…>` all the way down.
- **`Location`, `object?[]`, `ISymbol`, `Compilation`, `SyntaxNode` must never be stored in a pipeline
  value.** They are reference-identity (so they break equality) and they **pin** the compilation /
  syntax tree they came from in memory across edits. Carry **strings** (FQNs via
  `SymbolDisplayFormat.FullyQualifiedFormat`), not symbols; carry `LocationInfo` (file path + spans),
  not `Location`.

Pipeline models are therefore small `record`s of strings, bools, enums, and `EquatableArray<T>`.

**Rejected: passing `ISymbol`/`Compilation`/`ImmutableArray<T>` through the pipeline.** It works
functionally and is the most obvious code to write, which is exactly why it is dangerous: it compiles,
the output is correct, the tests pass, and the cache is quietly dead.

### 3. Diagnostics are data, reported only in the output stage

A discovery transform is **pure**: it must not call `spc.ReportDiagnostic` (there is no
`SourceProductionContext` in a transform anyway). Instead, when a candidate is invalid the transform
returns a result carrying `EquatableArray<DiagnosticInfo>` (built with `DiagnosticInfo.Create`, which
captures a `LocationInfo`), and the single `RegisterSourceOutput` callback reports them via
`diagnostic.ToDiagnostic()`. Cross-item diagnostics (duplicate names, duplicate responders) are
computed in the output stage where the whole collected set is available.

**Rejected: reporting diagnostics inline during a full-compilation scan.** It forces discovery into the
output stage (convention 1 violation) and tempts you to keep a raw `Location` on the model
(convention 2 violation). `DiagnosticInfo`/`LocationInfo` keep the model equatable while preserving the
exact reported location.

### 4. Share module discovery and the namespace matcher — don't re-implement them

Generators that group registrations by owning `[AppModule]` source the module set from the single
shared `ModuleProviders.CollectModules(context)` provider (a FAWMN provider returning
`EquatableArray<ModuleScanner.Module>`), and match a namespace to its module with
`ModuleScanner.FindBest` / `ModuleScanner.IsInScope`. The matcher is boundary-aware and ordinal
(`ns == module.Namespace || ns.StartsWith(module.Namespace + ".", Ordinal)`).

**Rejected: a private `ModuleInfo` record and a hand-rolled scan per generator.** That is how the same
namespace-prefix bug (`Foo.BarBaz` wrongly matching module `Foo.Bar`) reappeared in one generator after
being fixed in the others — copy-paste drift. One provider, one matcher, no drift.

### 5. Read the assembly opt-in as a projected `bool`, not as the whole compilation

Most framework generators are gated by an assembly trigger (`[UseElarion]` or a narrow
`[Generate…]`). Read it once via `ModuleProviders.HasTrigger(context, name)`, which projects
`CompilationProvider` to a `bool`. The output node combines that bool with the (separately cached)
discovered work and returns early when it is `false`. Because the projection is a `bool`, an edit that
does not touch the opt-in leaves it `Unchanged`, so the combined output node stays cached.

**Rejected: checking `HasAssemblyTrigger(compilation)` inside an output callback that also scans the
compilation.** That re-couples the opt-in check to convention 1's anti-pattern. Project narrow,
equatable facts out of the compilation; never put the compilation itself in the output node.

### 6. Output is a byte-identical contract, verified by a cache-assertion test

Two contracts are tested for every generator:

- **Emitted text.** Generator tests assert the exact generated source (`string.Contains` on a named
  hint file). Changing *what* is emitted is a deliberate change that updates those assertions; a
  refactor of *how* the model is computed (e.g. moving discovery to FAWMN) must keep the output
  **byte-identical**. Preserve every emit-time `OrderBy`/`Sort` — FAWMN delivery order is unspecified,
  so the sorts are what make output deterministic.
- **Caching actually holds.** Each generator tags its collect/combine nodes with `.WithTrackingName(…)`
  and carries a `GeneratorCacheAssert.ReusesOutputsAfterIrrelevantEdit` test. It runs the generator,
  makes an **irrelevant edit that re-runs the transform but yields an equal model** (it appends an
  unrelated declaration), and asserts every tracked node reports `IncrementalStepRunReason.Unchanged`
  or `Cached`. This is the *only* check that catches a re-introduced non-equatable field — output stays
  correct, output tests stay green, but the cache test goes red. A generator without this test has an
  unverified cache.

**Rejected: trusting that "tests pass" means the generator is incremental.** Output correctness and
cache correctness are independent; a non-equatable model passes the first and fails the second. Both
must be tested.

### Also: AOT/trim and cross-assembly discovery

- **Emit concrete, statically-typed code.** No reflection, open generics, or runtime type discovery in
  generated hot paths — explicit `typeof`/closed generics keep generated code NativeAOT- and trim-safe
  (see [ADR-0003](0003-decorator-attachment-predicates.md) for the canonical pattern).
- **Cross-assembly discovery goes through manifests, not symbol scanning.** A generator cannot read
  another generator's same-compilation output, and recursively walking referenced assemblies' symbols
  is slow and uncacheable (it needs the `Compilation`, which changes on every edit). The pattern: emit
  the assembly's data as `[assembly: AssemblyMetadata(key, value)]` and read referenced assemblies'
  metadata via `context.MetadataReferencesProvider` (`ElarionManifestReader` reads it straight from PE
  metadata with no symbols, so it is cached **per reference** — a source edit re-reads nothing). Used
  by `[AppModule]`/`[HttpEndpoint]`/`[RpcMethod]` (`ElarionManifest`) and by `[EntityConfiguration]`
  (`EntityConfigurationManifest` — a `DbContext` reads referenced configurations from their manifest
  instead of walking the referenced symbol tree). The convention's cost: a referenced project must run the generator to
  emit its manifest (the same requirement that already applies to referenced modules).

## Consequences

**Positive**

- The IDE stays responsive as the codebase grows: typing re-runs only the nodes whose inputs changed,
  not all discovery and emission.
- The cache is **provable**, not assumed — the `GeneratorCacheAssert` tests fail the moment a model
  stops being value-equatable, turning the invisible failure mode into a red test.
- Discovery and the namespace matcher live in one place, so the prefix-matching bug class cannot
  reappear by copy-paste.
- New generators have a concrete template: copy a reference generator, define string-only
  `EquatableArray<T>` models, carry diagnostics as data, gate on `HasTrigger`, add tracking names and a
  cache test.

**Negative / accepted**

- More ceremony than "scan and emit": value models, `EquatableArray<T>` for every collection,
  `LocationInfo`/`DiagnosticInfo`, tracking names, and a cache test per generator.
- `EquatableArray<T>` exists specifically to work around `ImmutableArray<T>`'s reference equality; it is
  duplicated into both generator assemblies (linked source file, like `Polyfills.cs`) because they do
  not reference each other.
- Two generators have a genuinely cross-file dependency and are handled specially rather than by a plain
  attribute index:
  - `HandlerRegistrationGenerator` — a handler's decorator pipeline can come from the handler, its
    `[AppModule]`, or the assembly, so resolution **must** re-derive on any compilation change (a pure
    per-node `CreateSyntaxProvider` transform would leave handlers with stale decorators when a *different*
    file's `[DecoratorList]` changes — a silent cross-edit bug). It keeps `Combine(CompilationProvider)`
    for correctness, but builds the module-`[DecoratorList]` map **once per pass** (not per handler) and
    returns a value-equatable array, so irrelevant edits don't re-emit.
  - `DbContextGenerator` — in-compilation `[EntityConfiguration]` classes use the syntax provider
    (incremental); referenced configurations use the **manifest** (`EntityConfigurationManifest`, cached
    per reference) instead of a symbol scan. The earlier "scans referenced assemblies every edit" cost is gone.
- The byte-identical-output contract makes conversions deliberate: you change *where* the model is
  computed, never the `StringBuilder`/hint-name/sort code, and you re-run the generator's tests after
  each step.

## Implementation

Foundation (in `Elarion.Generators`, with `EquatableArray.cs` linked into
`Elarion.EntityFrameworkCore.Generators`):

- `EquatableArray<T>` — value-equatable array wrapper (sequence equality + stable FNV hash), implicit
  conversions to/from `ImmutableArray<T>`, `IReadOnlyList<T>` plus `Length`/`IsEmpty` so it is a drop-in
  for `ImmutableArray<T>` in emit code.
- `LocationInfo` / `DiagnosticInfo` (`SourceModels.cs`) — value-equatable diagnostics-as-data;
  `LocationInfo.From(symbol|location)` and `DiagnosticInfo.Create(...).ToDiagnostic()`.
- `ModuleProviders` — `CollectModules(context)` (the shared FAWMN `[AppModule]` provider) and
  `HasTrigger(context, name)` (the projected `bool` opt-in). `ModuleScanner` keeps the `Module` record
  and the pure `FindBest`/`IsInScope` helpers.
- `GeneratorCacheAssert` (`tests/Elarion.Tests/Generators`) — the run-twice cache-assertion helper.

Reference generators to copy: `AppModuleDiscoveryGenerator`, `ElarionManifestGenerator`,
`ModuleDefaultServicesGenerator` (FAWMN), and the six converted in 2026-06 (`ResiliencePolicy`,
`ModuleService`, `ModuleValidator`, `ModuleApi`, `Scheduler`, `EventConsumer`). The actionable
checklist lives in `AGENTS.md` → "Source generator conventions".

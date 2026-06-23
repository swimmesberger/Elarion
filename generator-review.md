# Roslyn Source Generator Review — Elarion

> **Update — incrementality rewrite landed.** The headline finding (§2: six generators
> registering output off `CompilationProvider`, plus non-equatable pipeline models) has since
> been **fixed**. All six generators (`ResiliencePolicy`, `ModuleService`, `ModuleValidator`,
> `ModuleApi`, `Scheduler`, `EventConsumer`) now run as `ForAttributeWithMetadataName` /
> `CreateSyntaxProvider` pipelines gated by an equatable trigger, a shared
> `ModuleProviders.CollectModules` provider replaced `ModuleScanner.Collect` (deleted), and a new
> `EquatableArray<T>` + `LocationInfo`/`DiagnosticInfo` made every pipeline model value-equatable
> (also applied to `HandlerRegistrationGenerator`, `RpcMethodEmission`, `DbContextGenerator`,
> `KeysetGenerator`). Each conversion is proven by a run-twice cache-assertion test and keeps
> byte-identical output. Full suite green (347 passed / 8 Docker-skipped / 0 failed).


**Scope:** `src/Elarion.Generators/**` (26 files, ~7.6k LOC) and
`src/Elarion.EntityFrameworkCore.Generators/**` (~1.3k LOC).
**Dimensions:** maintainability · performance · anti‑patterns.
**Method:** six parallel reviewers (incrementality, hot‑path allocation, anti‑patterns,
maintainability/duplication, EF generators, analyzer/diagnostics), each finding then
**adversarially re‑verified against the actual code** by an independent agent. 48 raw
findings → **47 confirmed, 1 rejected**.

> This is a temporary review artifact (not intended to be committed). Fixes applied in
> this pass are listed in [§5](#5-fixes-applied-in-this-pass); larger work is in
> [§6](#6-recommended-follow-ups).

---

## 1. Executive summary

The generators are, on the whole, **well structured**: all 10 are `IIncrementalGenerator`,
most emit deterministic and inspectable code, diagnostics are modelled as data in the
better files (`KeysetGenerator`, the handler pipeline), and the `ModuleBoundaryAnalyzer`
follows analyzer best practice (concurrent execution, generated‑code opt‑out, symbols
resolved once at `CompilationStart`).

The **dominant systemic issue is incrementality**. A cluster of generators is wired so
that the *entire* discovery‑and‑emit body re‑executes on **every keystroke in the whole
solution**, each doing an **O(all source)** semantic scan. This is the single largest
IDE‑typing‑latency cost in the set and the headline recommendation.

| Severity | Count | Theme |
|---|---|---|
| 🔴 Critical | 1 | Six generators register output off `CompilationProvider` (no caching) |
| 🟠 High | 6 | Full‑compilation scans per edit / per handler; non‑equatable pipeline models |
| 🟡 Medium | 16 | Duplicated module discovery, `ImmutableArray` cache breaks, full assembly walks |
| ⚪ Low | 24 | Display‑string compares, allocation nits, ID/style inconsistencies, AOT nits |

Two **genuine correctness bugs** were found (not just performance): a namespace‑prefix
matching bug in the validator generator, and a fragile `EndsWith` base‑type match. Both
are fixed in this pass.

---

## 2. The headline finding — incrementality

### 2.1 Six generators run their whole body on every keystroke

`ModuleServiceRegistrationGenerator`, `ModuleValidatorRegistrationGenerator`,
`SchedulerRegistrationGenerator`, `ResiliencePolicyRegistrationGenerator`,
`EventConsumerRegistrationGenerator`, and `ModuleApiGenerator` all do:

```csharp
context.RegisterSourceOutput(context.CompilationProvider, static (spc, compilation) => {
    // ... full discovery + emit ...
    foreach (var syntaxTree in compilation.SyntaxTrees) {
        var semanticModel = compilation.GetSemanticModel(syntaxTree);   // binds every tree
        foreach (var decl in tree.GetRoot().DescendantNodes().OfType<...>()) { ... }
    }
});
```

`CompilationProvider` produces a **new `Compilation` value on every edit**, so the output
node has no usable cache key — the body re‑runs unconditionally. Worse, the body then
**binds every syntax tree** (`GetSemanticModel` is the expensive part) and walks every type
declaration, often **twice** (once for modules, once for the discovered symbols). Several
also call `ModuleScanner.Collect(compilation)`, which is *itself* a full scan — so the same
module set is recomputed three or more times per build.

**Why it matters:** this is O(all source) semantic work × ~6 generators on every keystroke,
which is exactly the work incremental generators exist to avoid.

**The fix (recipe).** Convert each to an attribute‑driven pipeline, **one at a time, running
that generator's test after each** (every one of the six has a dedicated
`*GeneratorTests.cs` asserting exact emitted output, and `ModuleDefaultServicesGenerator`
must run alongside in isolated generator tests per `AGENTS.md`):

1. **Trigger gate** → its own equatable node:
   `context.CompilationProvider.Select((c,_) => FrameworkFeatureTriggers.HasAssemblyTrigger(c, …))`
   reducing to a `bool`.
2. **Discover candidates** with `context.SyntaxProvider.ForAttributeWithMetadataName(attr, predicate, transform)`
   where `transform` returns a **string‑only equatable record** — no `ISymbol`, no
   `Location`, no raw `ImmutableArray` (wrap collections in an `EquatableArray<T>`).
   Attribute keys: Resilience→`ResiliencePolicyAttribute`, Service→`ServiceAttribute`,
   Scheduler→`ScheduledJobAttribute` (predicate must accept **method *and* type** targets),
   EventConsumer→`ConsumeEventAttribute` (predicate accepts **class‑level handler form *and*
   method‑on‑`[Service]` form**), ModuleApi→`GenerateModuleApiAttribute` + a 2nd FAWMN for
   handlers. `ModuleValidator` is base‑type driven (no attribute) so it keeps a
   predicate‑filtered `SyntaxProvider` on `ClassDeclarationSyntax`.
3. **One shared module provider:** a single
   `ForAttributeWithMetadataName(AppModuleAttribute, …).Collect()` feeds all of them, instead
   of each re‑scanning (this is also the fix for `ModuleScanner.Collect`).
4. `.Collect()` candidates, `.Combine(modules)`, `.Combine(trigger)`, then
   `RegisterSourceOutput`, returning early when the trigger is false. `EmitFiller` /
   `ModuleDefaultsEmitter` calls stay unchanged.

`AppModuleDiscoveryGenerator`, `ModuleDefaultServicesGenerator`, and `ElarionManifestGenerator`
already use this exact pattern in‑repo — they are the reference.

### 2.2 Non‑equatable pipeline models silently defeat caching

Even where a `SyntaxProvider` is used, several pipeline value models carry fields that are
**not value‑equatable**, so the cache key compares unequal every run:

- **`ImmutableArray<T>` fields** use *reference* equality, not structural — e.g.
  `DbContextGenerator`'s `EntityInfo`/`ConfigInfo`/`CollectedData` record structs
  (`DbContextGenerator.cs:556‑572`), `HandlerInfo` (`HandlerRegistrationGenerator.Models.cs:15‑54`),
  `KeysetTarget`/`ColumnModel`. Two structurally identical results compare unequal → re‑emit.
- **`Location` / `object?[]` fields** in diagnostic models
  (`HandlerRegistrationGenerator.Models.cs:49‑54`, `ElarionManifestGenerator`,
  `RpcMethodEmission.Model`) both break equality *and* pin syntax trees in memory.

**Fix:** introduce a small `EquatableArray<T>` wrapper (the standard generator idiom) and a
`LocationInfo`/`DiagnosticInfo` value record (file path + `TextSpan` + `LinePositionSpan`,
already done well in `KeysetGenerator.LocationModel`), and use them in every collected model.

---

## 3. Findings by theme

Legend — Sev: crit/high/medi/low · Cat: perf/anti/main. Locations are file:line at review time.

### Incrementality & pipeline design  (7)

| # | Sev | Cat | Location | Issue |
|---|-----|-----|----------|-------|
| 1 | crit | perf | `ModuleValidatorRegistrationGenerator.cs:37-173` | Move six registration generators off CompilationProvider to ForAttributeWithMetadataName |
| 2 | medi | perf | `AppModuleDiscoveryGenerator.cs:122-140` | AppModuleDiscoveryGenerator recursively walks the entire symbol namespace tree on every compilation change |
| 3 | medi | perf | `HandlerRegistrationGenerator.Models.cs:15-54` | HandlerInfo pipeline model is not value-equatable, defeating per-handler caching |
| 4 | medi | main | `HandlerRegistrationGenerator.Modules.cs:43-74` | DiscoverModules duplicates the full-compilation module scan inside HandlerRegistrationGenerator |
| 5 | medi | perf | `HandlerRegistrationGenerator.Pipeline.cs:21-63` | HandlerRegistrationGenerator transform re-scans whole compilation per handler node |
| 6 | medi | perf | `ModuleScanner.cs:16-50` | ModuleScanner.Collect is a full-compilation scan re-run by every consumer |
| 7 | low | perf | `RpcMethodEmission.cs:50-60` | RpcMethodEmission.Model holds a non-equatable IReadOnlyList in collected pipeline state |

### Allocation & hot-path performance  (10)

| # | Sev | Cat | Location | Issue |
|---|-----|-----|----------|-------|
| 1 | high | perf | `HandlerRegistrationGenerator.Pipeline.cs:21-63` | Module decorator-list resolution does a full-compilation scan per handler |
| 2 | medi | perf | `DbContextGenerator.cs:118-166` | DbContextGenerator recursively walks every referenced assembly's full namespace tree on every compilation |
| 3 | medi | perf | `HandlerRegistrationGenerator.Discovery.cs:60-68` | Handler interface match allocates a display string per interface in symbol-visiting hot paths |
| 4 | medi | perf | `ModuleApiGenerator.cs:83-96` | ModuleApiGenerator walks the entire compilation three separate times |
| 5 | medi | perf | `ModuleValidatorRegistrationGenerator.cs:50-125` | ModuleValidatorRegistrationGenerator scans the whole compilation twice and string-matches base types |
| 6 | medi | perf | `SchedulerRegistrationGenerator.cs:156-177` | Scheduler and EventConsumer generators scan the compilation, then re-scan it via ModuleScanner.Collect |
| 7 | low | perf | `FrameworkFeatureTriggers.cs:13-19` | HasAssemblyTrigger allocates a display string per assembly attribute on every generator pass |
| 8 | low | perf | `HandlerRegistrationGenerator.Cache.cs:13-13` | Per-handler GetTypeByMetadataName for the same attribute metadata names |
| 9 | low | perf | `ModuleServiceRegistrationGenerator.cs:268-274` | ModuleServiceRegistrationGenerator computes hint names via chained string.Replace allocations per service |
| 10 | low | perf | `SchedulerRegistrationGenerator.cs:773-786` | GetScheduledJobAttribute/ParseResilient use FirstOrDefault with display-string compare per symbol |

### Anti-patterns & correctness  (8)

| # | Sev | Cat | Location | Issue |
|---|-----|-----|----------|-------|
| 1 | high | perf | `HandlerRegistrationGenerator.Pipeline.cs:21-63` | Decorator-list resolution does a full-compilation scan once per handler |
| 2 | high | perf | `HandlerRegistrationGenerator.cs:14-33` | Per-handler pipeline combines with CompilationProvider and emits non-equatable HandlerInfo, defeating caching |
| 3 | high | anti | `ModuleServiceRegistrationGenerator.cs:88-133` | Six generators register directly on CompilationProvider and scan every SyntaxTree, with no incremental caching |
| 4 | medi | perf | `ElarionManifestGenerator.cs:21-57` | ElarionManifestGenerator collects models holding Diagnostics (Locations) and IReadOnlyList, defeating caching and pinning trees |
| 5 | medi | anti | `ModuleValidatorRegistrationGenerator.cs:110-118` | Validator module matching is culture-sensitive and matches partial namespace prefixes |
| 6 | low | perf | `DbContextGenerator.cs:556-573` | Collected EF generator value models use ImmutableArray fields, silently breaking caching |
| 7 | low | anti | `HandlerRegistrationGenerator.Modules.cs:43-74` | CancellationToken not threaded through full-compilation scans |
| 8 | low | main | `ModuleValidatorRegistrationGenerator.cs:94-123` | Validator detection relies on fragile EndsWith string matching instead of the resolved symbol |

### Maintainability & duplication  (8)

| # | Sev | Cat | Location | Issue |
|---|-----|-----|----------|-------|
| 1 | high | main | `ModuleScanner.cs:16-69` | Centralize module discovery; remove 4+ hand-rolled copies of the same scan |
| 2 | medi | main | `ElarionManifestGenerator.cs:220-243` | Module-discovery constants and helpers duplicated between ManifestGenerator and DiscoveryGenerator |
| 3 | medi | anti | `ModuleValidatorRegistrationGenerator.cs:110-114` | Validator generator's namespace match diverged from canonical IsInScope (copy-paste drift bug) |
| 4 | medi | main | `SchedulerRegistrationGenerator.cs:961-964` | Identical string/identifier-formatting helpers copy-pasted across many generator files |
| 5 | low | main | `AppModuleDiscoveryGenerator.cs:548-766` | BuildSource in AppModuleDiscoveryGenerator is a ~220-line monolith mixing many concerns |
| 6 | low | main | `EventConsumerRegistrationGenerator.cs:159-212` | Per-module registration generators share an unextracted boilerplate skeleton that will drift |
| 7 | low | main | `EventConsumerRegistrationGenerator.cs:14-14` | Inconsistent brace/coding style between sibling generators in the same folder |
| 8 | low | main | `ModuleServiceRegistrationGenerator.cs:86-133` | RegisterSourceOutput(CompilationProvider) shared across six generators is an unshared anti-pattern wrapper |

### EF Core generators  (7)

| # | Sev | Cat | Location | Issue |
|---|-----|-----|----------|-------|
| 1 | high | perf | `DbContextGenerator.cs:36-37, 118-192` | DbContextGenerator collects all entities/configs via a full-compilation scan on CompilationProvider, running on every edit |
| 2 | medi | perf | `DbContextGenerator.cs:556-572` | Pipeline value models hold ImmutableArray<T>, silently breaking incremental caching |
| 3 | medi | perf | `DbContextGenerator.cs:30-34, 77-116` | Inferred DbContext class target uses unfiltered CreateSyntaxProvider with no metadata-name fast path |
| 4 | low | main | `DbContextGenerator.cs:125-126, 206, 230` | Entity/config de-duplication keys on ToDisplayString instead of symbol equality |
| 5 | low | anti | `DbContextGenerator.cs:442-446, 502-505, 515-549` | Pluralize can collide two distinct entities onto the same DbSet/file, with no diagnostic |
| 6 | low | perf | `KeysetGenerator.cs:77-101, 110-129` | KeysetGenerator reads the assembly provider attribute off CompilationProvider, defeating caching of the most common edit |
| 7 | low | anti | `KeysetGenerator.cs:458-507, 509-535` | ToEntriesAsync emits runtime reflection (GetConstructors()[0]) inside an AOT-targeted generator |

### Analyzer & diagnostics  (7)

| # | Sev | Cat | Location | Issue |
|---|-----|-----|----------|-------|
| 1 | medi | anti | `ModuleApiGenerator.cs:78-96` | ModuleApiGenerator drives diagnostics off CompilationProvider, re-reporting on every keystroke |
| 2 | low | main | `HandlerRegistrationGenerator.Models.cs:58-88` | Normalize inconsistent diagnostic ID prefixes (WIMCACHE*/WFRE* vs EL*) |
| 3 | low | perf | `HandlerRegistrationGenerator.Models.cs:49-54` | Location stored in equatable pipeline diagnostic models defeats incremental caching |
| 4 | low | perf | `ModuleBoundaryAnalyzer.cs:42-43` | Cache SupportedDiagnostics in a static readonly array |
| 5 | low | perf | `ModuleBoundaryAnalyzer.cs:144-156` | Look up the IHandler interface symbol once at CompilationStart instead of comparing display strings per symbol |
| 6 | low | main | `ModuleBoundaryAnalyzer.cs:50-66 and 172-208` | Thread the analyzer's CancellationToken into the CollectModules namespace walk |
| 7 | low | perf | `ModuleBoundaryAnalyzer.cs:74 and 104-110` | Hoist repeated ContainingNamespace.ToDisplayString() out of the per-symbol/per-candidate boundary checks |

---

## 4. What's already good (keep doing this)

- `KeysetGenerator` and the handler pipeline model diagnostics **as data**
  (`DiagnosticModel`/`LocationModel`) and report them at emit time — the correct pattern.
- `ModuleBoundaryAnalyzer` resolves marker symbols **once** at `CompilationStart`, enables
  concurrent execution, and opts out of generated code.
- `DbContextGenerator` and the keyset generator use `ForAttributeWithMetadataName`.
- Metadata‑name lookups use `GetTypeByMetadataName` + `SymbolEqualityComparer` (mostly), not
  string parsing of attribute syntax.
- Provider‑aware emission (`KeysetGenerator` Npgsql row‑value seek) is resolved from a cheap
  equatable enum, not by re‑scanning.

---

## 5. Fixes applied in this pass

All changes are **generator‑only** (plus one ADR doc and one test file). Verified:
**both generator projects build clean**, **115/115 generator tests pass** (incl. a new
regression test), **4/4 analyzer tests pass**. (The full suite has one *pre‑existing,
environmental* failure — a PostgreSQL Testcontainers integration test that errors when Docker
is unavailable; it is unrelated to and untouched by these changes.)

| # | File | Change | Finding |
|---|------|--------|---------|
| 1 | `ModuleValidatorRegistrationGenerator.cs` | **Correctness bug:** namespace match `validatorNs.StartsWith(module.Namespace)` → `ModuleScanner.IsInScope(...)` — adds the `.` boundary + `Ordinal` so `Foo.BarBaz` no longer folds into module `Foo.Bar`. | anti #5, main #3 |
| 2 | `ModuleValidatorRegistrationGenerator.cs` | **Correctness:** base‑type detection `EndsWith("AbstractValidator<T>")` → `SymbolEqualityComparer.Default.Equals(baseType.OriginalDefinition, abstractValidatorSymbol)` (the resolved symbol was looked up but unused). | main #8 |
| 3 | `FrameworkFeatureTriggers.cs` | Resolve trigger symbols once + symbol‑compare instead of allocating `AttributeClass.ToDisplayString()` per assembly attribute (runs at the head of nearly every generator). | hot‑path #7 |
| 4 | `ModuleBoundaryAnalyzer.cs` | `SupportedDiagnostics` → cached `static readonly` array (no per‑access alloc). | analyzer #4 |
| 5 | `ModuleBoundaryAnalyzer.cs` | Resolve `IHandler\`2` once into `BoundaryState`; replace per‑symbol `OriginalDefinition.ToDisplayString() == "…"` with `SymbolEqualityComparer`. | analyzer #5 |
| 6 | `ModuleBoundaryAnalyzer.cs` | Thread `CancellationToken` through `CollectModules`→`Walk`→`Inspect` with `ThrowIfCancellationRequested()`. | analyzer #6 |
| 7 | `DbContextGenerator.cs` | Narrow inferred‑class `CreateSyntaxProvider` predicate to `partial` + has‑base‑list before the semantic transform (was every class in the compilation). | EF #3 |
| 8 | `DbContextGenerator.cs` | Compute `type.ToDisplayString()` **once**, reuse for the de‑dup key and `FullName` (was two allocs per entity/config), preserving the entity↔config join format. | EF #4 |
| 9 | `ModuleServiceRegistrationGenerator.cs` | Local `IsNamespaceInScope` now delegates to `ModuleScanner.IsInScope` (removes a 4th copy; can't drift). | main #1 |
| 10 | `HandlerRegistrationGenerator.Models.cs`, `ResiliencePolicyRegistrationGenerator.cs`, `AnalyzerReleases.Unshipped.md`, `…GeneratorTests.cs`, `0004-…md` | Normalize stray diagnostic IDs to the repo `EL*` convention: `WIMCACHE001‑004`→`ELCACHE001‑004`, `WFRE001/002`→`ELRES001/002`. All IDs were *unshipped*, so renamable without a shipped‑release entry; all reference sites (code, analyzer‑release tracking, test asserts, ADR) updated. | analyzer #2 |
| 11 | `ModuleValidatorRegistrationGeneratorTests.cs` | **New regression test** `GenerateValidators_PrefixSiblingNamespace_IsNotAssignedToModule` locking in fix #1. | — |

---

## 6. Recommended follow‑ups

Ordered by value/effort. The first is the **primary recommendation** and was deliberately
*not* auto‑applied: it is a six‑generator pipeline rewrite that the review itself flagged as
not mechanically safe (new equatable models + each generator has exact‑output tests that must
be re‑verified per conversion). It should be done as its own change, one generator at a time.

1. **Move the six `CompilationProvider` generators to `ForAttributeWithMetadataName`**
   (§2.1). Biggest perf win. Do per‑generator with its test after each.
2. **Add `EquatableArray<T>` + `LocationInfo` and adopt them in every collected pipeline
   model** (§2.2): `DbContextGenerator`, `HandlerRegistrationGenerator`,
   `ElarionManifestGenerator`, `RpcMethodEmission`, `KeysetGenerator`. Without this, even the
   FAWMN‑based generators don't actually cache.
3. **Centralize module discovery** in `ModuleScanner` as a shared incremental *provider* and
   delete the 4 hand‑rolled `ModuleInfo` records + scans
   (`HandlerRegistrationGenerator.Modules`, `ModuleService`, `ModuleValidator`, `ManifestGenerator`).
4. **Stop walking referenced assemblies on every edit** — `DbContextGenerator.CollectFromNamespace`
   recurses every referenced assembly's full namespace tree per compilation (EF #1/#2).
5. **Hoist remaining per‑symbol `GetTypeByMetadataName` / `ToDisplayString` compares**
   (`HandlerRegistrationGenerator.Cache`, `SchedulerRegistrationGenerator`,
   `HandlerRegistrationGenerator.Discovery`).
6. **Extract the shared per‑module emit skeleton** and the duplicated string/identifier
   helpers (`SchedulerRegistrationGenerator.cs:961`) to stop drift; consider splitting
   `AppModuleDiscoveryGenerator.BuildSource` (~220 lines).
7. **AOT honesty (`KeysetGenerator.ToEntriesAsync`)** — it emits `GetConstructors()[0]` +
   string‑named `Expression.Property` despite the "reflection‑free" header. The runtime tree
   composition is *unavoidable* (it composes a runtime `selector`), so the right action is a
   scoped comment documenting it as the one intentional exception, not a rewrite.

---

## 7. One finding rejected on verification

**`KeysetGenerator.ReadProvider` early‑return "fallthrough."** The verifier confirmed the
described behavior is *correct today*: with `AllowMultiple = false`, returning on the first
provider attribute is right, and a missing `Provider` argument correctly falls through to
`ProviderKind.Portable`. The "issue" was a hypothetical contingent on flipping `AllowMultiple`
— a stylistic nit with zero real impact. **No change.**

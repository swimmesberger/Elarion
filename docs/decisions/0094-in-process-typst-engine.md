---
type: Architecture Decision Record
title: "ADR-0094: An in-process Typst engine is an opt-in second implementation of ITypstCompiler — prototype findings"
description: "An in-process Typst engine is an opt-in second implementation of ITypstCompiler — prototype findings."
status: draft
---
# ADR-0094: An in-process Typst engine is an opt-in second implementation of ITypstCompiler — prototype findings

- Status: Proposed (prototype; CLI remains the only shipped engine)
- Date: 2026-11-18
- Related: [ADR-0093](0093-typst-pdf-rendering.md) (the CLI engine and the request model this reuses),
  [research: Typst in-process concurrency](../research/2026-11-18-typst-in-process-concurrency.md) (profiling and the upstream fix),
  [ADR-0092](0092-runtime-async-on-net11.md) (the net10/net11 split the CLI engine's process handling follows)

## Context

ADR-0093 drives Typst through its CLI: one process per document. That buys hard isolation (a runaway compilation is
killed, a crash stays in the child) at the cost of a process start, a font scan, and a template parse per document, and
a temporary workspace on disk. The question was whether calling Typst directly is worth offering, without depending on
one of the community .NET bindings, and whether maintaining two engines is affordable.

A prototype was built to answer it with measurements rather than estimates. It lives on branch [`spike/typst-native-engine`](https://github.com/swimmesberger/Elarion/tree/spike/typst-native-engine)
(commit `18c24c6d`), not on `main`:

- `native/elarion-typst` — a Rust `cdylib` over the `typst`, `typst-pdf` and `typst-kit` 0.15.1 crates, implementing
  Typst's `World` with an in-memory project per compilation and a process-wide engine (fonts loaded once, package files
  cached).
- `src/Elarion.Typst.Native` — `NativeTypstCompiler : ITypstCompiler` over that library, registered with
  `AddElarionTypstNative()`. Not packable.
- `tests/Elarion.Typst.Native.Tests` — the same test templates as the CLI engine, plus conformance tests.
- `tests/Elarion.Typst.Native.Benchmarks` — the CLI-versus-native measurements behind this ADR and the research note.

## Interop design

The boundary is small, data-only, and owned on both sides:

- **C ABI, opaque handles.** `engine_create/destroy`, `compile_pdf`, `result_pdf/diagnostics/destroy`, plus
  `abi_version` (checked at load; a mismatching library is refused) and `version`. Inputs are borrowed
  `(pointer, length)` views valid for one call; outputs are owned by a result handle until it is destroyed.
- **No unwinding across the boundary.** Every export runs under `catch_unwind`; a Typst panic becomes an error status
  with the panic message as a diagnostic.
- **Structured diagnostics.** Errors and warnings come back as one JSON array with severity, message, file, one-based
  line and column, hints and trace, surfaced as `TypstCompilationException.Errors`.
- **.NET side:** `[LibraryImport]` source-generated stubs with `[assembly: DisableRuntimeMarshalling]` (only blittable
  structs cross; trimming- and NativeAOT-safe — the project builds with the AOT analyzers clean), `SafeHandle` for the
  engine and results, `ReadOnlyMemory<byte>.Pin()` instead of copying assets (a `PinnedArena` owns every pin and native
  allocation for exactly one call), and the PDF exposed as an `UnmanagedMemoryStream` over the native buffer that frees
  the result on dispose — no managed copy.
- **Threading.** A compilation is synchronous native code, so it runs on a dedicated thread (`LongRunning`), not a
  thread-pool thread. The concurrency limit holds the slot until the native call returns, even when the caller stopped
  waiting.

Hand-written bindings were chosen over generators (csbindgen, uniffi): the surface is nine functions, and a generator
would add a build step and conventions for less code than it saves. That choice should be revisited if the surface
grows.

## Findings

**Conformance.** For the same request with a fixed creation timestamp, the native engine produces a PDF that is
**byte-identical** to the CLI's, with and without PDF/A-2b. The request model, templates, and package layout need no
change; the CLI is a free reference implementation for testing the native one.

**Performance** (Apple M-series, 14 cores, .NET 11, Typst 0.15.1; each document has different data so Typst's
memoization cannot return a cached layout):

| Scenario | CLI | Native |
| --- | --- | --- |
| Small invoice, one at a time (median) | 15.5 ms | 1.8 ms |
| 2,000-row report, one at a time (median) | 235 ms | 173 ms |
| Small invoice, 14 in parallel | 171 docs/s | 627 docs/s |
| 2,000-row report, 14 in parallel | 20.8 docs/s | 5.9 docs/s |
| Engine startup (library load and font scan; first time / cached) | — | 350 ms / 7 ms |

- Small documents gain an order of magnitude: the process start and font scan dominate them, and the template package
  stays parsed between documents.
- Large documents gain about 25 % one at a time — layout dominates, and that is the same code.
- **Large documents in parallel scale badly in-process**, peaking at two concurrent compilations and ending 3.5x below
  the CLI. Profiling and a series of experiments ([research note](../research/2026-11-18-typst-in-process-concurrency.md)) found:
  - Two process-wide locks in Typst's memoization (comemo) serialize concurrent compilations: one cache lock per
    memoized function, and the tracked-call accelerator, which holds a global read guard during the call and resizes
    under the global write lock. Three small comemo changes remove the waiting (PDFs stay byte-identical) and cut the
    in-process CPU cost per document from about 3.5x to about 2.2x of the single-document cost.
  - Process isolation is what makes the CLI scale: the same native code in 14 separate processes costs about 1.3x, like
    the CLI. A residual of about 1.6x remains in one process. Fonts, the rayon pool, thread priority and the allocator
    are each ruled out as its main cause; per-compilation caches would recover part of it.
  - Disabling memoization is not a shortcut: a single document then costs 2.3–4.6x more CPU, and the in-process
    inflation remains.
- **Memory.** The in-process cache grows with document size: about 1.4–2.7 GB working set after the large-document
  runs, depending on the eviction age, in the host process. The CLI's memory is returned when each process exits.
  comemo [#23](https://github.com/typst/comemo/pull/23) (open) addresses part of this.

**Robustness.** A timeout or cancellation can only *abandon* a native compilation: Typst has no cancellation hook and a
thread cannot be killed, so the work runs to its end and keeps its slot. A stack overflow or out-of-memory condition
inside Typst would end the host process. The CLI engine kills the process and survives both.

## Options considered

- **Ship the in-process engine now.** Rejected: it is worse than the CLI for large documents in parallel, takes
  gigabytes of host memory, and loses the hard timeout.
- **A pool of long-lived worker processes running the native engine.** It would scale like the CLI and keep fonts,
  packages and caches warm between documents, with killable timeouts. But it is the CLI kept warm: the same process
  model, plus our own Rust binary, protocol and release pipeline instead of Typst's official, checksummed release.
  Not justified at the document volumes the target applications have.
- **Fork comemo with the lock fixes.** Rejected: owning a fork of Typst's caching layer is the wrong place for this
  fix. The change is proposed upstream instead (draft in the research note, not yet filed).

## Decision (proposed)

The CLI stays the only shipped engine. The native engine is not merged: its code stays on [`spike/typst-native-engine`](https://github.com/swimmesberger/Elarion/tree/spike/typst-native-engine), an
implementation of the same `ITypstCompiler` with its tests (including byte-for-byte conformance with the CLI) and
benchmarks, so the interop path can be picked up when it pays off rather than rebuilt.

**Revisit when** a comemo release removes the lock contention (the three changes proposed in the research note, or
equivalents), ideally together with the memory release of comemo #23 and caches scoped to one compilation, and a Typst
release picks it up. Then rebase the spike branch onto that Typst release and re-run
`tests/Elarion.Typst.Native.Benchmarks` on an idle machine. Ship the native engine as an opt-in package only if it is
**better than or equal to the CLI on every measured metric**: latency for small and large documents, parallel
throughput for both, and steady-state memory.

Shipping it would additionally need: per-RID native runtime packages built by a CI matrix (Windows, Linux glibc and
musl, macOS; x64 and arm64), an option for the cache eviction age, and tracking the Typst crate API on each release.
The .NET side stays cheap to maintain twice: both engines share the request model, main-file generation, options,
telemetry and tests, and the engine-specific code is a few hundred lines each.

## Consequences

- `main` carries no native code, no Rust toolchain requirement, and no API that only the native engine would use. The
  spike branch adds those shared changes itself: a nullable `TypstCompilationException.ExitCode`, structured `Errors`
  (file, line, column), and `TypstPdf` over any stream.
- On revisit, the CLI engine is the reference: the spike's conformance test compiles each request with both engines and
  compares bytes.

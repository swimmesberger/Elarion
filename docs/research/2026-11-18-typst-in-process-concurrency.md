---
type: Research note
title: "Typst in-process concurrency: where the cost of compiling in one process comes from"
description: "Profiling of Typst compiled in-process under concurrent load, and the upstream fix; input for ADR-0094."
status: stable
---
# Typst in-process concurrency: where the cost of compiling in one process comes from

- Date: 2026-11-18
- Status: Research input for [ADR-0094](../decisions/0094-in-process-typst-engine.md). This is not a decision.
- Scope: Typst 0.15.1 and comemo 0.5.1 compiled into one process, many documents compiled concurrently on different
  threads, as a server rendering PDFs on request does. Editor (incremental) performance is out of scope.
- Method: the `Elarion.Typst.Native` prototype and `tests/Elarion.Typst.Native.Benchmarks` (its `Program.cs` lists the
  modes), both on branch [`spike/typst-native-engine`](https://github.com/swimmesberger/Elarion/tree/spike/typst-native-engine), macOS on Apple silicon (10 performance and 4 efficiency cores), .NET 11. Two documents: a 2,000-row table
  ("table") and 60 sections with an outline, cross-references and a "page X of Y" footer ("cross-ref", which needs
  several layout passes). Every document carries different data so memoization cannot return a whole cached layout.
  Thread stacks were sampled with `sample(1)`; comemo variants were built from a patched copy via `[patch.crates-io]`.
  **Inference** marks my reading.

## Summary

1. **Two global locks in comemo serialize concurrent compilations.** Each memoized function has one process-wide cache
   behind one `RwLock` (every miss takes the write lock), and the tracked-call accelerator holds a global read guard
   while running the tracked call, with a resize under the global write lock for nearly every new tracked value. Both are
   fixed by small, semantics-preserving changes (below); PDFs stay byte-identical to the CLI's.
2. **The fixes roughly halve the in-process cost but do not close the gap.** At 14 concurrent compilations a document
   costs about 1.6x the CPU it costs when the same native code runs in 14 separate processes. The CLI and the separate
   native processes inflate alike (about 1.15–1.35x from 1 to 14 concurrent, the machine under load); in-process
   inflates about 2.2x even with all fixes.
3. **No single remaining component explains the residual.** Shared fonts and engine state, the shared rayon pool, thread
   priority, and the allocator each change it by 10 % or less. Giving each thread its own comemo cache recovers 10–15 %,
   so sharing the caches themselves (size and cross-document entries) is part of it. **Inference:** the rest is the sum
   of many shared, frequently written cache lines in one address space; finding them needs hardware counters (Linux
   `perf c2c` on an idle machine), which this investigation did not have.
4. **Disabling comemo is not the simpler fix.** A switch is trivial to add, but a single document then costs 2.3x more
   CPU (table) and 4.6x more (cross-ref, which reruns layout until page numbers settle), and the in-process inflation
   remains (2.6–3.8x from 1 to 14 concurrent).
5. **Memory grows in the host.** The shared cache reached 1.4–2.7 GB working set. comemo
   [#23](https://github.com/typst/comemo/pull/23) (open) releases emptied cache storage on eviction; it is complementary.

## How to read the numbers

Wall-clock throughput was unreliable: the machine ran another project's test containers (load average 25–125 on 14
cores), and macOS shares an overloaded CPU per runnable thread, so whichever engine runs more threads wins more CPU.
CPU per document is robust to that and separates the two possible causes — threads waiting (utilization) versus each
document costing more (efficiency). Configurations were interleaved and repeated; ranges below span the rounds.

## Evidence

### CPU per document

| Configuration | 1 at a time: table / cross-ref | 14 at a time: table / cross-ref |
| --- | --- | --- |
| Typst CLI, one process per document | 220–242 / 132–154 ms | 249–303 / 143–160 ms |
| Native, 14 separate processes | — | 245–265 / — ms |
| Native in-process, comemo 0.5.1 | 169–170 / 115–129 ms | 691–845 / 455–546 ms |
| + memoize caches sharded (fix 1) | — | 467–469 / 297–391 ms |
| + accelerator lock fix (fixes 1–2) | 164–193 / 112–130 ms | 330–496 / 209–384 ms |
| + per-thread ID blocks (fixes 1–3) | 174 / 124 ms | 347–492 / 244–301 ms |
| Memoization disabled | 396–400 / 594–619 ms | 873–1,361 / 1,772–2,273 ms |

Alone, the native engine is cheaper than the CLI (no process start, warm fonts and packages). In parallel, everything
in one process costs more.

### Where the threads wait

| Stage | Blocked samples (14 concurrent, 4–5 s) |
| --- | --- |
| comemo 0.5.1 | ~22,600 in `RwLock` ← `comemo::memoize` ← `typst_layout::flow::layout_fragment`; ~3,700 in `CachedCell` |
| fix 1 | 34 in `memoize`; ~16,600 in `RwLock` ← `comemo::accelerate::get` ← `Tracked::call` |
| fixes 1–2 | lock waits negligible; remaining profile is CPU work (glyph drawing, layout, PDF conversion), spread like the single-document profile |

### Hypotheses tested after the lock fixes

| Hypothesis | Experiment | Result |
| --- | --- | --- |
| My `comemo::evict(10)` after each compile evicts other compilations' in-flight entries | eviction off vs on | Mixed, at most ~20 %; not the cause |
| Shared `Font` objects and engine state (reference-count traffic) | one engine per thread, each with its own fonts and package cache | 339–492 / 268–352 ms vs 385–544 / 255–329 ms shared: no change |
| Long-lived worker threads lose priority (macOS decays busy timeshare threads; observed: in-process 26 vs CLI 31) | compile and rayon threads at `USER_INITIATED` QoS | No change |
| One rayon pool fed by 14 outside threads (yield/spin churn) | private single-thread pool per compilation | No change, slightly worse |
| Allocator contention in one heap | mimalloc | ~5–10 % better |
| Global ID counter (`fetch_add(SeqCst)` on every `track()`) | per-thread ID blocks (fix 3) | ~10 % on cross-ref, none on table |
| Shared caches grow large and mix documents | one cache shard per thread (approximating per-compilation caches) | 10–15 % better at 14 concurrent; single document 20 % worse (rayon helpers no longer share results) |

## Proposed upstream changes (comemo)

All three keep the macro and the public API unchanged; the patched build passed the prototype's conformance tests
(PDFs byte-identical to the CLI) and 15 consecutive concurrent runs.

**Fix 1 — shard each memoized function's cache by the call key** (`memoize.rs`, ~40 lines). Entries for one key live in
one `CallTree` path, so one shard per key preserves lookup, insert and eviction. comemo
[#5](https://github.com/typst/comemo/pull/5) avoided `DashMap` because of a Safari/WebAssembly bug; a fixed array of
`parking_lot::RwLock` needs no new dependency.

```rust
const SHARDS: usize = 64;

pub struct Cache<C, Out> {
    init: fn() -> RwLock<CacheData<C, Out>>,
    shards: OnceLock<Box<[RwLock<CacheData<C, Out>>]>>,
}

impl<C, Out> Cache<C, Out> {
    pub const fn new(init: fn() -> RwLock<CacheData<C, Out>>) -> Self {
        Self { init, shards: OnceLock::new() }
    }

    fn shards(&self) -> &[RwLock<CacheData<C, Out>>] {
        self.shards.get_or_init(|| {
            let mut shards = Vec::with_capacity(SHARDS);
            shards.push((self.init)()); // registers the evictor exactly once
            shards.extend((1..SHARDS).map(|_| RwLock::new(CacheData::default())));
            shards.into_boxed_slice()
        })
    }

    fn shard(&self, key: u128) -> &RwLock<CacheData<C, Out>> {
        let shards = self.shards();
        &shards[(key as usize) % shards.len()]
    }
}
// memoize(): cache.0.read()/write() -> cache.shard(key).read()/write(); evict() visits every shard.
```

**Fix 2 — never hold accelerator locks during the tracked call; grow the accelerator list geometrically**
(`input.rs`, `accelerate.rs`, ~20 lines).

```rust
// Tracked::call
let call_hash = crate::hash::hash(call);
let cached = crate::accelerate::get(self.id)
    .and_then(|accelerator| accelerator.lock().get(&call_hash).copied());
let hash = match cached {
    Some(hash) => hash,
    None => {
        let hash = self.value.call(call);
        if let Some(accelerator) = crate::accelerate::get(self.id) {
            accelerator.lock().insert(call_hash, hash);
        }
        hash
    }
};

// accelerate::resize
let target = len.max(pair.1.len() * 2).max(64);
pair.1.resize_with(target, || Mutex::new(FxHashMap::default()));
```

**Fix 3 — reserve accelerator IDs in per-thread blocks** (`accelerate.rs`, ~20 lines): a thread takes 1,024 IDs per
`fetch_add` instead of one per `track()`. IDs only need to be unique; an ID reserved before an eviction falls below the
new offset and gets no accelerator, which is always correct. Smallest effect of the three.

**Structural direction:** the per-thread-cache experiment suggests that caches scoped to one compilation, rather than
process-wide, would help further for servers. That is a larger design change (comemo
[#6](https://github.com/typst/comemo/pull/6), "Local memoization", explored it for typst-preview and was closed), and
rayon helper threads would need to share the compilation's scope.

## Draft issue for typst/comemo (not filed)

> **Concurrent memoized calls serialize on process-wide locks when a host compiles many documents in parallel**
>
> We embed Typst 0.15.1 as a library in a server that renders PDFs on request, one compilation per thread. With 14
> concurrent compilations (different data each), a document costs 3–4x the CPU it costs alone, while the same code in 14
> separate processes costs about 1.3x. Sampling shows the compilation threads blocked first in
> `RwLock::lock_*_slow` under `comemo::memoize::memoize` (from `typst_layout::flow::layout_fragment`), and once that is
> sharded, under `comemo::accelerate::get` from `Tracked::call`.
>
> Three small changes, no API or macro change, PDFs byte-identical in our tests:
> 1. Shard each function's cache by the call key (64 `RwLock<CacheData>`; one key's entries stay in one shard). No new
>    dependency, so it avoids the `DashMap`/Safari concern from #5.
> 2. In `Tracked::call`, look up the accelerator and release its locks before performing the call; grow the accelerator
>    list geometrically so a new ID rarely needs the global write lock.
> 3. Reserve accelerator IDs in per-thread blocks instead of one `fetch_add(SeqCst)` per `track()`.
>
> Together they bring the in-process cost from about 3.5x to about 2.2x of the single-document cost (blocked samples
> from ~22,600 to negligible). Would you accept PRs along these lines? We can share the benchmark. A shard count of 1 on
> WebAssembly would keep today's behaviour there. Related: #23 (retained memory), #6 (local memoization).

Before filing, re-measure on an idle machine and attach the numbers.

## Sources

- comemo 0.5.1 source: `src/memoize.rs`, `src/accelerate.rs`, `src/input.rs`, `src/tree.rs` (crates.io).
- comemo [#5](https://github.com/typst/comemo/pull/5) "Parallel comemo & optimizations" (merged): per-function caches with
  `RwLock`; why `DashMap` was not used.
- comemo [#6](https://github.com/typst/comemo/pull/6) "Local memoization" (closed).
- comemo [#23](https://github.com/typst/comemo/pull/23) "Release empty cache backing storage during eviction" (open).
- Searches of typst/comemo and typst/typst issues for contention, `RwLock`, and concurrent compilation found no existing
  report (2026-11-18).

# ADR-0092: The net11 build compiles with runtime async

- Status: Accepted
- Date: 2026-11-13
- Related: [ADR-0066](0066-low-allocation-connection-dispatch.md) (the pooled `ValueTask` state machines the net10 build keeps), [ADR-0042](0042-in-memory-actors.md) (the actor call path measured below),
  [ADR-0027](0027-declarative-request-validation.md) (the .NET 11 multi-targeting this builds on)

## Context

.NET 11 ships *runtime async*: with `<Features>runtime-async=on</Features>` the compiler emits async methods without a
state machine and the runtime manages suspension and resumption. The .NET 11 libraries are built this way. Expected
benefits: faster synchronously completing calls, cheaper suspension than a heap-allocated state machine, and live
stack traces (profilers, debuggers, `new StackTrace()`) that show the real handler and decorator chain.

Elarion's runtime packages build for `net10.0` and `net11.0`, so only the `net11.0` build can switch. The setting
changes how Elarion's own methods compile; code generated into an application's assembly follows the application's
setting. Runtime async is still marked preview in .NET 11.

## Decision

The `net11.0` build compiles with runtime async (`Directory.Build.targets`); `ElarionRuntimeAsync=false` builds it with
compiler state machines instead. The `net10.0` build is unchanged. Generic code inspects results through
`ResultInspection`, never an inline `response is IResultLike { … }` pattern (see below).

`ValueTask` stays the return type of hot-path async methods and `PoolingAsyncValueTaskMethodBuilder` stays where it
is: the compiler ignores the builder under runtime async, and the `net10.0` build (and an opted-out `net11.0` build)
still relies on it for allocation-free suspension.

## Measurements

`tests/Elarion.Benchmarks` has an A/B mode (`ELARION_BENCH_RUNTIME_ASYNC=compare`) that runs each benchmark on net11.0
twice: as built by default and rebuilt with `/p:ElarionRuntimeAsync=false`. Apple M-series laptop, .NET 11.0.0,
BenchmarkDotNet 0.16.0-preview.2 (0.15.x does not recognize `net11.0`). The suspending arms used BenchmarkDotNet's
default job; the rest a quick job (1 launch, 3 warmup, 10 iterations), so timings within ~20% are noise. Allocations
are exact.

| Path | State machines | Runtime async |
| --- | --- | --- |
| Handler called directly, completes synchronously | 6.1 ns, 0 B | 2.0 ns, 0 B |
| Decorated handler (observability decorator) | 47 ns, 0 B | 34 ns, 0 B |
| Scoped resolve + call / `IHandlerSender` | 472 / 496 B | 472 / 496 B |
| Handler that suspends once (plain `async`, like application code) | 409 ns, 257 B | 294 ns, 128 B |
| Same, behind the observability decorator | 569 ns, 258 B | 337 ns, 240 B |
| Connection dispatch, low-allocation profile | 65 ns, 0 B | 45 ns, 0 B |
| Actor call awaited by an `async` caller (`Ask_Awaited`) | 1,686 ns, 213 B | 1,473 ns, 159 B |
| Actor `Ask_Pipelined` / `PingPong` | 182 / 168 B | 111 / 97 B |

Two findings shaped the decision:

- **A missed box elision.** The first pass showed +32 B per decorated handler call. Allocation sampling attributed it
  to a boxed `Result<int>`: the decorators checked the outcome with an inline `response is IResultLike { … }` on the
  generic `TResponse`. For a struct response that pattern is a box the JIT normally removes; inside a runtime-async
  method body it does not (also with struct-only type arguments, and after inlining a small helper back into the async
  body). `ResultInspection` (`IsSuccess`, `IsFailure`, `TryGetError`) runs the check in never-inlined generic helpers,
  compiled specialized and box-free in both models; the observability, transaction, audit, audit-commit, idempotency
  and cache-invalidation decorators and `HybridHandlerCache` use it. No matching dotnet/runtime issue was found
  (closest: dotnet/runtime#137275). The documented boxing struct-request overload of connection dispatch shows the same
  effect (+24 B); its explicit-generic overload does not.
- **Suspension is cheaper, not dearer.** An isolated chain of *pooled* methods suspends for 0 B under state machines and
  192 B under runtime async, which suggested a regression. Real call chains are not all pooled: the application's
  handler and its caller are plain `async` methods that box their state machines when they suspend, and runtime
  async's continuations are cheaper than those boxes — hence the suspending-handler and `Ask_Awaited` rows above. The
  actor arms that return the `ValueTask` straight to BenchmarkDotNet (`Ask`, `Ask_NoCallTimeout`, `Ask_Reentrant`)
  show +55 B under runtime async; that is the harness's own continuation (the harness is compiled per job too), not
  the actor path, as `Ask_Awaited` shows.

## Consequences

- Applications on .NET 11 get Elarion's net11 assemblies compiled with runtime async: faster dispatch, equal or lower
  allocations on the measured paths, and readable live stack traces. Exception stack traces are unchanged.
- Generic code in Elarion — and application decorators, per the documentation — uses `ResultInspection` for result
  checks. AGENTS.md records the rule.
- Sync-over-async (blocking on an actor or handler call from a thread-pool thread) remains unsupported; it can starve
  the pool under either model, and which thread ends up blocking differs between them.
- Re-measure with `ELARION_BENCH_RUNTIME_ASYNC=compare` when changing async hot paths and on .NET servicing releases.
  Benchmark methods await the call under test the way application code does.
- Move BenchmarkDotNet back to a stable release once one supports .NET 11.

## Alternatives considered

- **Opt-in only.** The first measurement pass recommended it (+32 B per decorated call, apparent actor regression);
  both were explained above, and the remaining data favors runtime async on every measured application path.
- **Drop `ValueTask` / the pooling builder for net11.** Rejected: `ValueTask` is still ~5x cheaper than `Task` for
  synchronous completion under runtime async, the builder is ignored there at no cost, and the net10 build needs it.
- **Per-package opt-in.** Rejected: splitting the model inside one call chain makes profiling and stack traces
  inconsistent for no measured gain.

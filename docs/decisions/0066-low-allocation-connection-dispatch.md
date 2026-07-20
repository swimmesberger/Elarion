# ADR-0066: Opt-in low-allocation dispatch profile for high-rate connections

- Status: Accepted
- Date: 2026-07-19
- Related: [ADR-0053](0053-bidirectional-client-connections.md), [ADR-0059](0059-merged-handler-observability-decorator.md),
  [ADR-0065](0065-self-typed-request-markers-and-bound-connection-invoker.md).

## Context

A game-server-like connection transport dispatches thousands of concurrent connections, each producing
frequent small messages through `ConnectionHandlerInvoker`. At those rates the steady-state garbage per
message dominates GC pressure: a fresh seeded DI scope, the decorator chain rebuilt per resolution, a
`DispatchScopeContext` dictionary per dispatch, payload buffers materialized per send, and telemetry
plumbing. The full pipeline remains desirable for these messages — decorators, `Result<T>`, typed dispatch —
so the goal is to make the pipeline cheap, not to bypass it.

**Explicit non-goal:** simulation-rate traffic (tens of messages per second per connection for movement)
stays outside the pipeline in application-owned in-memory state (actors, tick loops). A framework mode
chasing that tier would compromise pipeline semantics for everyone else.

Everything below is opt-in; the per-message defaults and their semantics are untouched.

## Decision

### Per-connection dispatch scope (`ConnectionDispatchScopeMode.PerConnection`)

`ConnectionHandlerInvoker` accepts `ConnectionHandlerInvokerOptions` at construction — chosen per
connection, never globally. In `PerConnection` mode the invoker creates one DI scope lazily on first
dispatch, reuses it for every unary and named message, and disposes it in `DisposeAsync` (the invoker is
now `IAsyncDisposable`; the adapter that constructed it owns that call). Per message the invoker refills
one reusable `DispatchScopeContext` (`Clear()` + `Set`) and re-runs the cached
`IDispatchScopeInitializer` list, so identity promotion (ADR-0053) is observed by the very next message
while nothing allocates in steady state.

Supporting semantics that make re-seeding correct:

- `ClaimsPrincipalCurrentUser.Initialize` drops its lazily cached claims/roles when re-seeded with a
  different principal (reference-equality fast path keeps the common case free). This was a latent bug for
  any reused scope, fixed for all modes.
- The idempotency-key initializer always seeds (`null` clears): a message without a key must not inherit
  the previous message's. `IIdempotencyKeySeed.Seed` takes `string?` accordingly.
- Scoped services live for the **connection's lifetime**. A pipeline that assumes per-message scoping —
  the unit-of-work `TransactionDecorator` (an EF `DbContext`'s change tracker) or the
  `IdempotencyDecorator` — keeps its state across every message on the connection. The invoker warns once
  per handler type when such a pipeline is dispatched in this mode, by reading the generated
  keyed-by-request-type `HandlerMetadata` registration. The warning is advisory ("should" tier): the mode
  is intended for handlers whose writes go through explicit stores, not ambient EF transactions.
- Dispatch is assumed sequential (the adapter's single receive loop is the per-connection ordering
  guarantee, the same contract codecs already rely on); the mode's caches are deliberately unsynchronized.
- Stream invocations keep their own scope in both modes: an accepted stream owns its scope through lazy
  enumeration and disposes it at terminal state — tying that to the connection scope would couple stream
  disposal to connection lifetime.

### Cached chain resolution

The composed handler chain is a scoped registration, so MS DI's scoped-instance caching already returns
the same chain instance on every resolution within the reused scope — the chain is built once per
connection, not once per message. The invoker additionally memoizes the resolved chain per
`(request, response)` type in a plain dictionary, skipping the provider's per-resolution lock and lookup.
Caching across per-*message* scopes is semantically unsafe (chains capture scoped services), which is why
this rides the per-connection opt-in instead of being unconditional.

### Singleton handlers (`[Handler(Scope = ServiceScope.Singleton)]`)

A handler whose dependencies are all singletons (data source, time provider, options) declares a singleton
lifetime with the same property name, enum, and semantics as `[Service(Scope = …)]`. The registration
generator then registers the chain singleton — no scope participation at all for its dispatches. A
generator-backed compile-time check makes misuse impossible to ship:

- **ELSG011** (error): a constructor dependency is provably scoped/transient (`[Service(Scope = …)]` facts
  from the compilation, plus a curated framework table — `IUnitOfWork`, `ICurrentUser`, DbContext-derived,
  and friends are scoped).
- **ELSG012** (error): a dependency's lifetime cannot be verified at compile time. There is deliberately no
  escape hatch; the fix is `[Service(Scope = ServiceScope.Singleton)]` on the dependency or keeping the
  handler scoped. Cross-assembly `[Service]` facts are invisible to the generator today and fall to
  ELSG012; carrying service scopes through the assembly manifest is a possible future relaxation.
- **ELSG013** (error): the handler's pipeline attaches a scope-dependent feature — `[Idempotent]`,
  `[Auditable]`, authorization requirements, feature variants, user-scoped caching, or a custom decorator
  with non-allowlisted dependencies (this catches the transaction decorator via `IUnitOfWork`).

A singleton chain is built from the root provider, so per-caller context enrichment (scoped
`IHandlerContextEnricher`s) is definitionally unavailable on singleton handlers; the observability
decorator is emitted enrichment-free (span + metric survive). That is inherent to a root-built chain, not
a compatibility concession.

### Telemetry opt-down (`[HandlerTelemetry(HandlerTelemetryMode.None)]`)

A separate, leveled attribute — valid on the handler class, the module bootstrapper class, and the
assembly, nearest declaration wins — so a hot module opts down once and a cold handler inside it re-enables
by declaring `Full`. When the effective mode is `None`, the generator simply does not emit the
observability decorator: zero runtime cost, AOT-clean, and errors are unaffected because failures remain
`Result` values and exceptions still propagate to the transport. An invoker-level option was rejected: the
chain is composed at registration, so an invoker option would need a second runtime composition path and
would strip telemetry from cold handlers sharing the connection. The separate attribute matches the
`[Idempotent]`/`[Auditable]`/`[Cacheable]` idiom (a per-handler pipeline concern is an attribute, not a
`[Handler]` property); lifetime stays a `[Handler]` property because it mirrors `[Service(Scope = …)]`.

Also rejected: a runtime skip of the enrichment run when its output is provably unobservable
(`Activity.Current is null && loggerFactory is null`). Spans and metrics are already free without
listeners; the per-message cost that remains is context enrichment, whose consumers are the **log scope**
and the **ambient transport span** — deliberately independent of tracing listeners, because handler log
lines should carry user context whenever logging is on. Virtually every host has a logger factory, so the
gate would fire only in benchmarks and logging-free minimal hosts while adding a subtle behavioral branch.
The honest lever for a hot handler is compile-time: don't generate the decorator (`None`), or unregister
the enricher (`AddElarionUserContextEnrichment(o => o.Enabled = false)`).

### Writer-based outbound sends

`SendBinaryAsync(ReadOnlyMemory<byte>)` forces one materialized buffer per outbound message. Connection
sinks additionally offer `SendBinaryAsync<TState>(TState state, Action<TState, IBufferWriter<byte>>
serialize, CancellationToken)`: the caller serializes the payload directly into the framed outbound
buffer. The synchronous state-passing callback shape is deliberate — no closure allocation, and the shared
frame buffer structurally cannot be held across an `await`. A `ref struct` session API was rejected: it
cannot survive the contended path's queue hand-off.

The framer seam gains **abstract** `BeginMessage(IBufferWriter<byte>)` (reserve/emit the prologue, return
its length) and `CompleteMessage(Span<byte> prologue, Span<byte> payload, IBufferWriter<byte>)`
(backfill the length prefix / validate delimiters; the payload span was later made writable so a framer
carrying negotiated cipher state can encrypt the in-place serialized payload and backfill its tag — a
same-length transform). No capability flag and no buffering fallback: pre-1.0,
custom framers break and implement the real contract (seams are designed for the strongest
implementation). Admission, backpressure (`MaxPendingSends`), and completed-send meaning are identical on
both send paths; a contended writer-send serializes into a rented buffer and queues with unchanged FIFO
semantics.

Inbound is the mirror: the memory handed to `OnBinaryAsync` is pooled and call-scoped on every adapter.
TCP already sliced a reused buffer; the WebSocket adapter's per-message `MemoryStream.ToArray()` copy was
an implementation shortfall against the already-documented contract and now reuses a pooled per-connection
reassembly buffer unconditionally. Code that retained the payload array in violation of the documented
contract breaks — deliberately.

### Allocation-conscious shapes

`Result<T>` was already a readonly struct with a zero-allocation success path. The self-typed-marker
overloads (ADR-0065) box a `readonly record struct` request through their interface-typed parameter; hot
value-type requests use the explicit-generic overloads, which dispatch unboxed (documented, and measured
in the benchmark). Hot framework async methods (`HandlerInvoker`, the connection invoker cores, the
observability core) use `PoolingAsyncValueTaskMethodBuilder` so common suspensions do not allocate state
machines — the mechanism the TCP outbound writer already used.

### Measurement

`ConnectionDispatchBenchmarks` (BenchmarkDotNet, `[MemoryDiagnoser]`) is the requirement-0 baseline:
per-message vs per-connection vs singleton vs the full low-alloc profile, with and without stateless
decorators and a tracing listener, plus the struct-request boxing delta. A deterministic xunit allocation
gate (`GC.GetAllocatedBytesForCurrentThread` around a synchronously-completing dispatch loop) runs in the
normal test suite as the CI regression signal; BenchmarkDotNet itself stays a manual tool.

Recorded run (`ConnectionDispatchBenchmarks`, Apple Silicon, .NET 10, short job; per-message dispatch of
a trivial echo handler through `ConnectionHandlerInvoker`, current-user initializer registered, 0 or 3
stateless custom decorators — the 0/3 split changed nothing but the per-message chain rebuild cost):

| Mode | Allocated/op | Time/op |
| --- | --- | --- |
| Per-message scope (default) | 2 352–2 424 B | ~550 ns |
| Per-connection scope | 312 B | ~155 ns |
| Full low-alloc profile (+ singleton + telemetry `None`) | **0 B** | **~62 ns** |

The residual 312 B in per-connection mode is **not** tracing — spans, the pipeline tag, and metrics
remain allocation-free no-ops while no listener is attached. It is the context-enrichment leg of the
merged observability decorator (ADR-0059), which runs whenever any `IHandlerContextEnricher` is
*registered* (the benchmark registers current-user support, which enables the default user-context
enricher per ADR-0033) — deliberately independent of listeners, because its outputs are the ambient
`Activity.Current` (a transport span can exist without any `Elarion.Handlers` listener) and the log
scope (a logging concern). A host with no enrichers sits at ~0 B in this mode already; telemetry `None`
removes the leg regardless. The struct-request marker-overload boxing (24 B) is invisible beside the
per-message scope cost and only matters on the zero-allocation profile — hence the explicit-generics
guidance rather than an API change.

## Consequences

- The full low-alloc profile — per-connection scope + singleton handler + telemetry `None` + writer sends —
  dispatches a message with no scope, no chain resolution, no context, no telemetry object, and no payload
  buffer allocation; each piece also stands alone.
- Per-connection mode trades transactional per-message isolation for allocation-freedom and is advisory-
  warned, not blocked, for transaction/idempotency pipelines; the database-first default guidance stands.
- Custom `TcpMessageFramer` implementations must implement the two new abstract members.
- Applications that (incorrectly) retained the `OnBinaryAsync` payload on WebSocket connections break and
  must copy, as TCP codecs always had to.
- `IIdempotencyKeySeed.Seed` takes `string?`; hosts seeding in-band keys are unaffected at call sites.

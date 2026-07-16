# ADR-0044: Request-driven server-streaming handlers

- Status: Accepted (HTTP/SSE server-streaming foundation; producer-owned live streams remain covered by
  [ADR-0052](0052-ordered-streams.md); bidirectional transports remain in
  [ADR-0053](0053-bidirectional-client-connections.md))
- Date: 2026-07-06
- Related: [ADR-0043](0043-client-events.md) (server-push of facts; the ephemeral progress tier),
  [ADR-0035](0035-protocol-neutral-staged-upload-seam.md)/[ADR-0039](0039-binary-file-responses.md)
  (streaming bytes into the system — the staged-blob tier), [ADR-0010](0010-event-bus-is-pub-sub-only.md)
  (the bus never returns values; request/reply is dispatch), [ADR-0021](0021-idempotency.md) (per-request
  guarantees the pipeline provides), [ADR-0026](0026-openapi-http-transport.md) (own only what Microsoft
  can't), [ADR-0031](0031-imperative-handler-transport-mapping.md) (transports stay concrete and AOT-safe).

## Context

"Streaming support" comes up as one feature request but is four different problems. Three already have
answers in Elarion; this ADR records them, decides the fourth, and — most importantly — pre-decides the
design so the topic is not relitigated per feature request:

1. **Streaming bytes in** (uploads): answered by the staged-blob tier — `IStagedUploadStore`, tus,
   resumability (ADR-0035/0039). A raw stream parameter would give up resume for nothing.
2. **Server-push of facts** (data changed, notify browsers): answered by client events (ADR-0043).
3. **Progress of an operation**: answered by the ephemeral client-event tier (ADR-0043). The
   design-relevant subtlety: progress-as-streamed-response ties progress to one held connection — a page
   reload loses it, a second tab never sees it, a retry restarts it. Progress-as-client-event is
   addressable by scope, survives reloads, reaches every tab. Progress therefore does **not** motivate
   streaming handlers, although it is the argument most commonly reached for.
4. **Responses that *are* streams** — LLM token output, exporting a large result set row-by-row, a
   tail/watch query. Request/reply where the reply is a sequence. This is the only genuine gap.

A fifth framing — "the client sends facts as a stream" (`ChannelReader<TFact>` into a handler) — appears
to be a gap but dissolves on inspection; see the decision below.

Two structural facts constrain any design:

- **`Result<T>` states success or failure once, upfront.** A stream can fail after item 400. The error
  channel of a streaming response is structurally different: upfront `Result` for
  authorization/validation/not-found, then transport-specific termination (the initial SSE adapter aborts the
  connection). Bolting
  `Result<IAsyncEnumerable<T>>` onto `IHandler` would misrepresent that contract the same way routing
  client events through `IIntegrationEventBus` would have misrepresented Plane B's guarantee (ADR-0043).
- **The decorator pipeline is per-request.** Authorization, feature gating, validation, idempotency, and
  the transaction all attach to one request with one outcome. Any streaming shape must state what "one
  request" means, or the pipeline cannot be composed honestly.

## Decision

Ship the server-streaming foundation only. This is a distinct interaction shape: transports opt in explicitly,
starting with HTTP/SSE. JSON-RPC and MCP remain single-shot and unsupported. The typed invocation seam is all
the default packages ship: a future application or adapter package may adopt gRPC server streaming through it,
while gRPC itself (including client and duplex streaming) remains a deliberate non-goal for default packages.

### Streaming responses

Streamed responses use a **distinct handler contract**, not a widening of `IHandler`:

- `IStreamHandler<TRequest, TItem>` — the request is a normal DTO; the response is a stream of `TItem`.
  Semantics: the pipeline (tracing → authorization → feature gate → validation) runs **before the first
  item** and can reject with a normal upfront `Result`; after the first item, SSE has no framework error
  envelope, so a fault aborts the connection rather than replacing the accepted response.
- Stream handlers have their own decorator pipeline. A `[DecoratorList]` can contain both unary and stream
  decorators; generation selects only the interface-compatible shape and preserves its relative order. Stream
  authorization, feature gates, validation, and observability attach as stream wrappers. Caching, idempotency,
  cache invalidation, transaction, unary resilience, and unary audit never attach to a stream pipeline.
  A dual-shape class consequently has two independent pipelines.
- **Scope lifetime is the pipeline's job**: lazy enumeration outlives the handler invocation, so the
  dispatch infrastructure must keep the DI scope (and therefore the `DbContext`) open until the stream
  completes or the client disconnects. This is the real engineering cost and the reason the contract is
  its own interface — the generator can wire the different lifetime only if the shape is statically
  distinguishable.
- **Transport mapping is asymmetric, and that is accepted**: the initial imperative HTTP helper maps typed
  handlers to SSE and takes a host-owned request binder. It awaits startup before committing response headers,
  writes canonical source-generated JSON items through ASP.NET Core's native
  `TypedResults.ServerSentEvents`, and retains the invocation scope through enumeration.
  JSON-RPC 2.0 and MCP have no standard streaming result shape; no bespoke dialect is invented.

### Streaming requests: rejected as a handler contract

"The client sends facts as a stream" is not a handler shape; it is either a transport optimization or a
batching decision, both already available:

- **A fact arriving from a client is a command.** Hand a handler a `ChannelReader<TFact>` and "request"
  becomes ambiguous: either the pipeline runs once for the whole stream — permissions checked once and
  stale a minute later, one unbounded transaction, validation with no defined subject — or it runs per
  item, in which case the items *are* the requests and the channel was never a semantic unit, only a pipe.
- **The wire efficiency already exists.** At normal rates, HTTP/2 multiplexing makes per-call overhead
  trivial and JSON-RPC **batch** is client-side micro-batching with the per-item pipeline preserved. At
  high rates (telemetry, cursor positions, autosave operations), the recommended pattern is a
  **list-carrying batch command** — `RecordTelemetry { IReadOnlyList<Reading> Readings }`, flushed every
  N items or M milliseconds — where one authorization, one validation pass, and one transaction per batch
  are *chosen deliberately in the contract*, visible in the schema, and work over every transport today.
- **The browser seals it.** There is no interoperable browser primitive for streaming a request body
  (`fetch` duplex streaming is Chromium-only); the only real client→server stream from a browser is a
  WebSocket. Streamed input was therefore never a handler-contract question — it is the bidirectional-
  transport question, deferred below. Server-to-server callers stream bytes via staged blobs and facts
  via batch commands.

### Bidirectional transports: a future seam, never grown complexity

SignalR (and equally a raw WebSocket protocol, or gRPC streaming) is rejected as a fourth RPC transport:
a hub protocol, its own serialization pipeline, sticky-session and backplane posture (Redis, colliding
with the one-Postgres positioning) — to serve gaps that SSE covers unidirectionally while client→server
is already covered by JSON-RPC/HTTP/MCP. If a consuming app ever has a genuine bidirectional need
(collaborative editing, live telemetry ingest at rates where batching fails), it arrives as a **new
transport package over the existing dispatch seam** — adopted whole, like every other transport — not as
incremental streaming features on the defaults (ADR-0025 discipline).

## Alternatives considered

- **`IHandler<TRequest, Result<IAsyncEnumerable<TItem>>>`** (widen the existing contract). Rejected — it
  misstates the error channel (upfront `Result` cannot represent mid-stream failure), silently breaks
  `[Cacheable]`/`[Idempotent]`/transaction composition, and hides the scope-lifetime change from the
  generator.
- **A streaming JSON-RPC dialect** (notification-based partial results tied to the request id, as LSP's
  `$/progress` does). Rejected — nonstandard, every client must implement the dialect, and the TS
  generator would own a protocol no one else speaks. The shipped adapter uses SSE; another framing is a future
  adapter decision.
- **SignalR as a fourth transport** (native `ChannelReader`/`IAsyncEnumerable` both directions). Rejected
  as a default — whole-transport adoption behind a future seam if ever needed; see above.
- **`ChannelReader<TFact>`/`IAsyncEnumerable<TFact>` request parameters.** Rejected — the pipeline
  ambiguity above; batch commands and JSON-RPC batch are the shipped answers.
- **A default gRPC streaming package.** Rejected for the same reason as the S3 wire protocol on blobs: gRPC is
  a deliberate non-goal for default packages. An application may still adopt server streaming in its own adapter
  over the typed invocation seam; that transport decision is separate from this framework contract.

## Consequences

- `IStreamHandler<TRequest,TItem>` returns an upfront `Result<IAsyncEnumerable<TItem>>`: startup rejection is
  distinct from lazy completion, cancellation, and fault. `StreamHandlerInvoker` returns an accepted invocation
  lease that owns its DI scope until enumeration ends or is explicitly disposed.
- ADR-0052's actor/`StreamHub` streams remain producer-owned, sequenced and resumable. Request-driven handler
  streams are not a replacement for that hot-stream model: use the actor SSE leg when a single sequencer and
  resume semantics are required; use this contract for a request's deferred response (exports, token output).
- The batch-command pattern (list-carrying DTO, client-side flush) becomes the documented recommendation
  for high-frequency client-to-server facts.
- The initial slice deliberately ships no schema/TypeScript-client surface, NDJSON adapter, or analyzer rule
  beyond the generator's normal compile-time registration checks. A future transport owns its own framing and
  contract projection rather than being implied by this handler shape.
- Accepted asymmetry: JSON-RPC and MCP callers never see streamed responses; a handler that streams is an
  HTTP-only handler by declaration.

# ADR-0067: Per-connection sessions and a single framer emit path

- Status: Accepted
- Date: 2026-07-20
- Related: [ADR-0053](0053-bidirectional-client-connections.md),
  [ADR-0066](0066-low-allocation-connection-dispatch.md).

## Context

Both connection adapters (TCP and WebSocket) exposed the same handler seam: one DI singleton with three
correlated per-connection callbacks — `ConfigureConnectionAsync` (per-connection settings), an
authenticator, and `CreateProtocol`. Implementing a mid-stream **encryption toggle** (a handshake derives
a session key, then the link switches into encrypted framing) exposed that shape's compromises:

- The three callbacks had **no per-connection state channel**. The binding-configuration row resolved in
  `ConfigureConnectionAsync` was thrown away; key material derived during authentication had no home on
  the way to the codec; the per-connection framer was reachable only through accessor properties plus a
  runtime downcast.
- The singleton invited **per-connection state on the handler** — a race under concurrent connections.
  Our own test handlers exhibited the pattern (instance counters, "last created protocol" properties).
- A stateful framer placed in *endpoint* options survived dialer reconnects with mutated state; only
  documentation guarded the stateless-shared versus stateful-per-connection split.
- The framer had **two abstract emit paths**: `WriteMessage` (memory-sends, drain batches, handshake IO)
  and ADR-0066's `BeginMessage`/`CompleteMessage` (writer-sends). A cipher framer had to implement its
  transform twice — and "encrypted on one path, forgotten on the other" is a silent wire-corruption bug
  class.
- `InMemoryTcpLink` handed the **same framer instance** to both ends of the simulated link. For stateless
  framers that is free; for a negotiated framer it is wrong — both ends flip together, so a broken
  negotiation can pass in simulation and fail on sockets.

Pre-1.0, seams are redesigned rather than patched (clean abstractions over compatibility).

## Decision

### Handlers are factories; sessions own the connection

`TcpConnectionHandler` and `WebSocketConnectionHandler` shrink to one member:
`CreateSessionAsync(peer|HttpContext, ct)` → `TcpConnectionSession?` / `WebSocketConnectionSession?`.
It runs before any byte is exchanged (before the WebSocket is even accepted), is the
binding-configuration lookup point, and `null` is an **early reject**: the TCP socket closes quietly;
the WebSocket upgrade request gets `403 Forbidden`.

The session is the per-connection object. It exposes `Settings` (the per-connection overrides,
typically assembled in its constructor — a stateful framer is created here, per session, so dialer
reconnects always start fresh), `AuthenticateAsync`, and `CreateProtocol`. Per-connection state — the
binding row, the framer, key material — lives in typed session fields and flows into the codec's
constructor. No downcasts, no correlation lookups, and the handler stays a stateless singleton. The
formerly added `TcpClientConnection.Framer`/`TcpHandshakeContext.Framer` accessors are removed as
redundant: the session already holds its framer, typed.

### One abstract emit path on `TcpMessageFramer`

The abstract outbound surface is `BeginMessage`/`CompleteMessage` alone. Every adapter send route —
uncontended memory-sends, writer-sends, the drain batch — frames through the pair against the pooled
frame buffer (`GetWrittenSpan` provides the prologue/payload views); `WriteMessage` remains as a
**non-virtual base convenience** that builds the frame through the same pair in a scratch buffer and
copies it out (handshake IO, simulators, tests). Framing and any negotiated transform are therefore
written exactly once. `CompleteMessage`'s payload is a writable span (same-length in-place transforms —
encrypt in place, backfill the tag into the reserved prologue), and `TryReadMessage` may return
framer-owned memory (the decrypted payload cannot slice the receive buffer), with the reader's
malformed-framer validation enforcing region bounds only for slices that alias its own array.

The trade-off is explicit: length-changing wire framings (byte-stuffing/escaping) are not expressible —
consistent with `DelimitedTcpFramer`, which already rejects delimiter-carrying payloads and directs
escaping to the codec. Length-*adding* framing stays available via prologue reserve and epilogue append.

### Simulated links get per-end framers

`InMemoryTcpLink.Start` accepts a `clientFramer`; the default (sharing the endpoint framer instance)
is correct only for stateless framers, and handlers whose sessions create stateful framers supply the
client's own instance. The two ends of a link never share framing state, in simulation as on sockets.

## Consequences

- The encryption-toggle scenario is first-class: the session creates the cipher framer, returns it via
  `Settings`, hands it typed to the codec, and the codec flips it after awaiting the mode-switch send —
  with the cipher implemented once, applied on every send route, and simulated honestly.
- One allocation per connection for the session object — noise against socket/TLS/handshake costs; the
  ADR-0066 steady-state dispatch path is untouched (the memory-send now frames through the same pooled
  pipeline via a cached static delegate, still allocation-free).
- Every handler implementation breaks (pre-1.0, intended): trivial handlers gain a small session class;
  handlers that carried per-connection state lose a latent race by construction.
- Early rejection is now expressible before the handshake (unprovisioned peers never cost an accept on
  WebSocket, never cost a TLS upgrade decision on TCP beyond the session lookup itself).

# ADR-0064: Acknowledgment-gated outbound delivery is a codec-tier helper, adopted on second demand

- Status: Proposed
- Date: 2026-07-18
- Related: [ADR-0053](0053-bidirectional-client-connections.md) (bidirectional connections; bounded
  outbound writer), [ADR-0052](0052-ordered-streams.md) (ordered delivery doctrine), and
  [ADR-0025](0025-distributed-scheduler-coordination.md) (the one-Postgres scale posture).

## Context

Industrial device protocols in the field (the evidence comes from a materials-handling gateway speaking a
classic confirmed-telegram wire protocol) layer a **protocol acknowledgment discipline** on top of the byte
stream:

- Telegrams are *confirmed* (sequence-numbered 0001–9999, wrapping) or *unconfirmed* (sequence 0000).
- Sequence numbers are tracked **per direction** and must **survive process crashes** — they live in
  durable storage and are reset only by an explicit resynchronisation exchange (a SYN telegram).
- **Before the peer acknowledges the in-flight confirmed telegram, no other confirmed telegram may be
  sent** — a one-in-flight (stop-and-wait) send window, with retransmission on timeout.
- Processing an inbound confirmed telegram produces its ACK **plus** follow-up outbound telegrams that
  must go out **after** that ACK, in FIFO order.

ADR-0053's TCP adapter deliberately solves a different, lower layer. Its bounded outbound writer
guarantees **admission-order FIFO onto the wire** and completion-after-physical-write; it knows nothing of
protocol acknowledgments. Two of the field requirements therefore already fall out of the transport
contract today:

- *"ACK first, then the queued follow-ups"* is admission ordering. A codec that admits the ACK before the
  follow-up frames — from its `OnBinaryAsync` turn, or from the single actor turn that processed the
  telegram — gets exactly that wire order, coalesced or not. No new mechanism is required, but the
  ordering is only as strong as the admission ordering: two *independent* tasks admitting sends have no
  defined order between them, which is the same single-sequencer rule ADR-0052 states for streams.
- Request/reply correlation is `ConnectionPendingRequests<TKey,TResponse>` (register-before-send via
  `SendAndWaitAsync`).

What Elarion does **not** provide is the acknowledgment-gated layer itself:

- a send queue whose head is released by the **peer's protocol ACK**, not by the physical write — the
  transport writer's FIFO deliberately completes on write, because "the peer confirmed it" is protocol
  knowledge;
- a durable **per-direction sequence-number seam** (crash-safe increment, compare-and-set for
  resynchronisation);
- retransmission of the unacknowledged head after a protocol timeout, and a resync hook that flushes or
  replays the queue when the sequence space is reset.

Today a consuming gateway owns all of that in its codec/actor tier — correctly, per ADR-0053's boundary
("codecs own protocol encoding/decoding", conversations are codec/actor state). The question this ADR
answers: **when, and in what shape, does that layer belong in Elarion?**

## Decision

**Not in the transport.** The bounded writer stays protocol-neutral: it orders admissions, bounds memory,
and reports physical writes. Gating the queue head on a protocol ACK, sequence numbering, retransmission,
and resync are protocol semantics; folding them into `TcpOutboundWriter` would couple every plain
connection to one wire discipline's state machine and violate the framing/codec split that lets one
endpoint serve differently-speaking device families.

**As kernel conversation helpers, when a second consumer needs them.** The shape, so it is ready to lift
when demand arrives, mirrors the existing helper tier (`ConnectionPendingRequests`, `ConnectionInbox`).
The field protocol's own vocabulary fixes four properties a naive design gets wrong:

- `ConfirmedSendQueue<TFrame>` — the ordered *business* lane. **Every business frame rides the queue for
  ordering**, confirmed or not: a confirmed entry holds the head until the codec reports the peer's
  ACK/NCK (`Acknowledge(sequence)`/`Reject(sequence, reason)`), an unconfirmed entry completes on
  physical write but still queues behind its predecessors. One-in-flight is the shipped default (the
  strongest field protocol requires it); a configurable window is the seam's growth axis, not a day-one
  feature. A per-entry confirmation timeout retransmits the head or faults the entry; resync callbacks
  settle or replay the queue when the sequence space resets. Protocol/runtime traffic (the ACK itself,
  NCK, SYN, relays) deliberately **bypasses** the queue — that lane already exists and is the ADR-0053
  sink (`SendBinaryAsync`, admission-FIFO, bounded).
- **The queue is channel-scoped, not connection-scoped.** The field engine keeps entries across TCP
  reconnects and re-sends after resynchronisation. The helper therefore outlives the sink it writes to:
  it detaches on disconnect, reattaches to the replacement connection, and applies a **per-entry
  disconnect policy** — fail-on-disconnect (default: the caller re-issues, matching the sink's
  at-most-once posture) or retry-on-reconnect for entries whose caller awaits one authoritative outcome.
  Entries settle exactly once with a bounded outcome vocabulary: acknowledged, rejected (NCK),
  timed out, disconnected, or withdrawn.
- `ConfirmedReceiveGate` — the receive half of the discipline. Stop-and-wait implies the peer retransmits
  a confirmed frame whenever our ACK is lost; the receiver must **detect the duplicate sequence, re-emit
  the ACK, and not reprocess**. The gate wraps that dedup/re-acknowledge decision around the codec's
  dispatch so the discipline stays symmetric — without it, every consumer reinvents the
  duplicate-delivery bug the protocol exists to prevent.
- `ISequenceNumberStore` — the durable per-direction counter seam (next/current/compare-and-set, scoped
  by a source→destination pair), with the in-memory default for tests/simulators and a PostgreSQL
  provider on the host's existing database, per ADR-0025. Persisting the counter is what makes the
  discipline crash-safe; it is exactly the kind of provider seam Elarion already ships for settings and
  idempotency. Frames themselves are **not** persisted — a queue that must survive a process crash is
  outbox territory.

The helpers compose with, and never replace, the ADR-0053 pieces: the queue *uses* `SendBinaryAsync`
(admission FIFO makes "ACK before released head" a local ordering decision), `ConnectionPendingRequests`
keeps serving the request/reply shape, and a per-channel actor remains the recommended owner — it is the
natural place for the channel-scoped queue to live across connection generations.

**Adoption trigger.** This tier ships when a **second** consuming project needs the discipline (the
recorded posture for conversation helpers: one consumer's protocol engine is application code; two make a
framework seam). Until then this ADR documents the boundary so the first consumer's engine is built
*against* the sink/helper seams — which the field gateway's implementation already validates.

## Consequences

- Consuming gateways keep a small, honest protocol engine today: a codec/actor that admits ACK-then-
  follow-ups in one turn (supported ordering), holds its confirmed queue, and persists its own sequence
  numbers. Nothing in Elarion has to change for that engine to be correct.
- The transport writer's guarantees stay simple and universally true — admission FIFO, bounded admission,
  completion-on-physical-write — and remain the substrate the confirmed tier would sit on.
- When adopted, the helpers land in `Elarion.Connections` (queue + receive gate) plus a provider package
  for the durable sequence store, with the in-memory store as the closest-semantics weaker tier
  (documented delta: not crash-safe), per the strongest-implementation seam rule.
- Deliberately out of scope, now and at adoption: sliding-window ARQ beyond a configurable window,
  cross-connection ordering, priority preemption inside the business lane (a frame that must overtake
  the queue is protocol traffic and uses the bypass lane), and any broker-like durability for the frames
  themselves — a frame queue that must survive a crash is outbox territory, not a connection helper.

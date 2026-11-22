---
type: Architecture Decision Record
title: "ADR-0104: A command previews by running and rolling back, and commits only the outcome it previewed"
description: "A previewable command is one handler that previews (runs, reports, rolls back) and commits; the commit carries the previewed outcome's fingerprint and refuses with preview.changed when the decision changed."
status: stable
---
# ADR-0104: A command previews by running and rolling back, and commits only the outcome it previewed

- Status: Accepted (implemented 2026-11-22)
- Date: 2026-11-22
- Related: [ADR-0021](0021-idempotency.md) (idempotency owns its unit of work),
  [ADR-0080](0080-errors-are-a-declared-contract-with-a-stable-code.md) (errors are a declared contract),
  [ADR-0015](0015-ef-core-transaction-participation.md) (what enlists in the caller's transaction),
  [ADR-0003](0003-decorator-attachment-predicates.md) (decorator attachment),
  [ADR-0101](0101-a-decision-required-answer.md) (a request that needs the caller's decision)

## Context

A consuming application (a single-tenant ERP) has about twenty pairs of operations that are a preview plus a commit:
an import preview and the import, a bank-statement preview and its commit, a waste-calendar import preview and the
import. Each pair is two handlers that share their decision only by discipline: the preview handler computes what the
commit would do, the commit handler computes it again and does it. Two things go wrong.

- **The two drift.** A rule added to the commit and forgotten in the preview, or the other way round, makes the
  preview show something the commit does not do.
- **The data moves between the calls.** In the bank-statement import a row shown as *new* in the preview became a
  possible duplicate before the person confirmed, and the commit silently held it back. The commit acted on a decision
  the person never saw, and nothing told them.

The framework already has every part of a dry run: the unit of work (`IUnitOfWork`) rolls back database writes, and
outbox integration events, domain-event consumers, PostgreSQL blob commits, settings and the audit success record all
enlist in it. What is missing is a way to say "run this command, show me what it decided, keep nothing", and a way for
the commit to say "only if you still decide the same".

## Decision

A **previewable command** is one handler that is both the preview and the commit.

- **Contracts in Abstractions** (`Elarion.Abstractions.Previews`). The request implements `IPreviewableRequest`, an
  `ICommand` with `bool Preview` and `string? PreviewFingerprint`. The handler's success value implements
  `IPreviewOutcome<TSelf>`: `string? PreviewFingerprint` and `TSelf WithPreviewFingerprint(string?)`, so the framework
  stamps the fingerprint without mutation or reflection (`this with { PreviewFingerprint = fingerprint }`).
- **A built-in decorator in core**, `PreviewDecorator<TRequest, TOutcome>`, attached by the handler generator to every
  handler whose request implements `IPreviewableRequest` — a compile-time decision like `[Idempotent]`, keyed on the
  request type because the request carries what the decorator reads. A response that is not `Result<T>` with
  `T : IPreviewOutcome<T>` is `ELPREV001` (an error, never a decorator that silently ignores `Preview`). A singleton
  handler with it is `ELSG013`, because the decorator resolves the per-dispatch unit of work.
- **Position: just inside idempotency, outside the `[DecoratorList]`.** It owns the unit of work of a preview and of a
  fingerprinted commit; under `[Idempotent]` its scope is a savepoint in the idempotency transaction, and an
  application transaction decorator inside it nests.
- **Behavior.**
  - Neither member set: an ordinary command, passed straight through (no unit of work, no hashing, no state machine).
  - Otherwise the unit of work must really roll back. `IUnitOfWork.SupportsRollback` is a default interface member
    that returns `false`; the EF Core and SQL-tier units of work return `true`. Without it (or without any unit of
    work; the decorator resolves it softly) the call fails with `preview.unsupported` (`Internal`) and the handler
    never runs.
  - The decorator begins a scope and runs the inner pipeline in a nested scope that is committed on success (rolled
    back on failure), so the EF change tracker is flushed inside the preview: a constraint violation surfaces in the
    preview as it would on commit, and a flushed tracker cannot write the rows again when an outer idempotency scope
    commits its own row.
  - A failed result rolls back and is returned unchanged.
  - A successful outcome is fingerprinted: SHA-256, base64url, of its canonical JSON (`IElarionJsonSerialization`,
    source-generated metadata) with the fingerprint cleared, and stamped with it. A preview rolls back and returns the
    stamped outcome (a preview wins over a fingerprint sent with it). A commit whose fingerprint matches commits and
    returns it; one whose fingerprint differs rolls back and fails with `AppError.Conflict`, code `preview.changed`,
    carrying the new stamped outcome as data, so the client can show what changed and confirm it without another call.
  - An outcome with members visible by permission ([ADR-0103](0103-one-permission-decision-and-members-visible-by-permission.md))
    is fingerprinted as the caller sees it: the generator passes the caller's permission check and the copy that
    hides those members. A hash over members the caller may not see would let it confirm a guess of a low-entropy
    hidden value (an opening balance); a preview and its commit come from the same caller, so both hash the same
    view, and the `preview.changed` payload is hidden like any answer.
- **The error contract.** Where the decorator attaches, the generator implies `preview.changed` (`Conflict`, the
  outcome type as payload) and `preview.unsupported` (`Internal`), so the schema export lists them and the generated
  client types `error.data` of `preview.changed` as the outcome.
- **Telemetry.** The handler span carries `elarion.preview.outcome` for calls that are not ordinary.

`preview.unsupported` is `Internal` because the caller can do nothing about it: it is a host without a transactional
unit of work, a configuration error that `500` describes honestly.

## Consequences

- One handler, one decision: a preview can no longer drift from the commit, and the twenty pairs become twenty
  handlers. A commit that would act on a different decision than the one confirmed is refused, with the new decision in
  the answer.
- The outcome must be deterministic for the same decision: no fresh ids, timestamps or random values, collections in a
  stable order, nothing that depends on `Preview`. A non-deterministic outcome makes every fingerprinted commit fail
  with `preview.changed`, which is loud, not silent.
- A preview costs a full run of the command, writes included, inside a transaction that is rolled back. That is the
  price of "the preview is the commit"; it is the same work the separate preview handler did, plus the writes.
- A rollback undoes only what enlists in the transaction. Client events are broadcast when published (harmless
  re-query hints), cache invalidation runs after a successful result, and external calls (a payment provider, e-mail,
  an Azure blob commit) happen for real: the handler checks `Preview` and skips them, or moves them behind an
  integration event, which the outbox rolls back.
- Under `[Idempotent]` the preview's key and response are stored; the preview and the commit are two calls with two
  keys (the default request fingerprint rejects a reused key as `idempotency.key_reused`).
- `IUnitOfWork` gains a member. It is a default interface member, so existing implementations compile and report
  `false` until they claim the capability; a custom unit of work over a real transaction sets it to use previews.
- The fingerprint is a consistency check, not a credential: a client that forges one only skips its own check.

## Rejected alternatives

- **Separate preview and commit handlers sharing code by discipline.** What applications do now. Nothing stops the two
  from drifting, and nothing tells the commit that the data moved since the preview.
- **A fingerprint without a dry run.** A dedicated preview handler computes the outcome and its fingerprint, and the
  commit compares. It catches moved data, but the preview and the commit are still two implementations of one
  decision, so they can still disagree.
- **A dry run without a fingerprint.** The preview is the real command rolled back, but the commit re-decides
  silently: the bank-statement row is still held back without anyone being told.
- **A server-side preview token store.** The preview stores its outcome under a token and the commit replays or
  compares it. It is state between two calls (expiry, cleanup, a table, multi-node visibility) for what a hash the
  client sends back answers statelessly; replaying a stored outcome would also commit a decision made on old data.
- **HTTP `ETag`/`If-Match`.** The fingerprint as an entity tag and the commit as a conditional request. It is not
  transport-neutral: JSON-RPC, MCP and in-process senders have no conditional headers, and the outcome is not a
  resource with a URL.
- **A `"previewable": true` flag on the method in the schema export.** Not added for now: the request schema already
  carries `preview` and `previewFingerprint`, and the declared `preview.changed` error is typed. Revisable when a
  client needs to find previewable operations generically.

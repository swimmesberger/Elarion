# ADR-0045: Handler-action audit trail (`[Auditable]` + `IAuditScope` + transactional EF sink)

- Status: Accepted
- Date: 2026-07-07
- Related: [ADR-0021](0021-idempotency.md) (the in-transaction store-write pattern the success record
  reuses), [ADR-0003](0003-decorator-attachment-predicates.md) (decorator attachment),
  [ADR-0034](0034-abstractions-holds-contracts-not-implementations.md) (contracts in Abstractions,
  decorators in core), [ADR-0033](0033-user-context-trace-and-log-enrichment.md) (the multi-registration
  enrichment seam `IAuditChangeContributor` mirrors), [ADR-0025](0025-distributed-scheduler-coordination.md)
  (scale positioning: replace the seam, never grow the default), [ADR-0038](0038-client-assigned-entity-identity.md)
  (client-assigned Guid v7 ids).

## Context

Every downstream application eventually asks the same question of the framework: *"Wer hat was geändert?"*
— a UI-visible, searchable record of who created/changed/deleted which resource, filterable by several
properties at once (resource + user + time range). Today each app hand-rolls this, and the framework's own
documentation has been using an app-owned `AuditDecorator`/`IAuditTrail`/`[Auditable]` as its canonical
"custom decorator" teaching example — a strong signal the concern is framework-worthy.

The handler pipeline already assembles every ingredient an audit record needs: the actor (`ICurrentUser`),
the operation identity (the `[Handler("module.action")]` name and `HandlerMetadata`), the outcome
(`Result<T>`, including the `AppError` kind), and correlation (the tracing decorator's `Activity`). No
application can capture these more cheaply than the framework can — the same argument that justified
authorization, validation, and idempotency as auto-attached decorators.

"Audit logging" names two different features, and conflating them is the classic failure mode:

1. **An action trail** — "user X performed `properties.update` on property 123 at T, outcome: success."
   Handler-granular, intent-shaped, structurally searchable. Also sees what no entity-level mechanism can:
   denied attempts, failed attempts, and actions with no database write at all.
2. **Entity change history** — field-level before/after diffs, point-in-time reconstruction, undo. Recorded
   at the persistence layer; mechanics-shaped (one business action explodes into N row changes), blind to
   intent, denials, and non-DB effects.

Users asking the opening question almost always need the first, *plus* "welche Änderungen" — the changed
fields with old/new values — without which "Liegenschaft geändert" disappoints. A pure action trail answers
that only when handlers are task-shaped; a coarse `update` handler needs the diff attached.

## Decision

Ship a **handler-action audit trail** with **field-level diffs as an opt-in capture mode of the same
record** — one record per audited handler invocation, not a parallel entity log. Full temporal entity
history (point-in-time reconstruction, undo) stays a non-goal; so do tamper-evidence chains and SIEM/log
shipping — the sink seam is where those bolt on.

### Contracts (`Elarion.Abstractions.Auditing`)

- `[Auditable]` — class-level handler attribute; `Enabled = false` opts a handler out under defaults;
  optional `Resource = "…"` names the resource type when the handler doesn't set one at runtime.
- `[ElarionAuditDefaults]` — assembly-level: every command handler in scope is audited by default
  (queries stay opt-in), mirroring `[ElarionAuthorizationDefaults]`.
- `[Audited]` / `[AuditIgnore]` — entity-class opt-in and property opt-out for automatic change capture.
  They live in Abstractions (dependency-free) so domain assemblies never reference the EF package.
- `AuditRecord` — the structured fact: id (client-assigned Guid v7), timestamp, module + action (the
  handler name), actor (user id/name, null for anonymous), resource type/id **plus an optional parent
  resource reference** (so "Feuerlöscher X on Liegenschaft Y" supports the aggregate-level audit tab
  without joining domain tables), outcome (`Succeeded`/`Failed`/`Denied`) + error kind, correlation id
  (the current `Activity` trace id), a list of `AuditChange { Entity, EntityId, Property, OldValue,
  NewValue, Kind }`, and a string-keyed details bag.
- `IAuditScope` — the scoped, ambient per-invocation accumulator. Three producers write into it: the
  pipeline (actor/action/outcome — always), the EF interceptor (field diffs — default-on for opted-in
  entities), and the handler (`scope.SetResource(…)`, `scope.AddChange(…)`, `scope.AddDetail(…)` for
  intent-level facts and the paths auto-capture can't see).
- `IAuditTrail` — the sink seam, two write modes with explicit semantics: `RecordAsync` (enlists in the
  ambient transaction when one exists, writes immediately otherwise) and `RecordDetachedAsync` (always its
  own scope/transaction — the write must survive the caller's rollback). `RecordAsync` takes a **record
  factory**, not a record: the common handler shape leaves its writes for the unit-of-work commit flush, so a
  persistence sink must flush those pending writes first (letting change capture contribute their diffs to
  the scope) and materialize the record only after. A capture-less sink (a log shipper) invokes the factory
  immediately. This wrinkle was found by the integration tests, not the design pass — an eager record missed
  every diff of the "mutate and let commit flush" handler, the most common one.

### Two decorators, because the transaction makes the outcome paths physically asymmetric

A success record must commit **atomically with the business change** (a record claiming "order cancelled"
for a rolled-back transaction is worse than none — the ADR-0021 argument). But denial and failure records
must be written on a **detached** path: denials happen before any transaction opens, and a failure's
transaction rolls back, which would discard an enlisted record. No single pipeline position can see both
sides, so the generator attaches two thin decorators (both in `Elarion` core, EF-free, soft-attached only
when an `IAuditTrail` is registered — the inbox precedent):

- **`AuditDecorator` (outer)** — a new generator slot just *outside* `AuthorizationDecorator` (inside
  context enrichment and tracing, so `Activity.Current` is populated). It starts the scope with actor +
  action, and records non-success outcomes via `RecordDetachedAsync`: a failed `Result` maps to `Failed`
  (or `Denied` for unauthorized/forbidden error kinds), an exception is recorded and rethrown.
- **`AuditCommitDecorator` (inner)** — a new generator slot just *outside* `CacheDecorator`, which places
  it *inside* `TransactionDecorator` and `IdempotencyDecorator` (both live further out in the chain). On a
  successful result it drains the scope into an `AuditRecord` and calls `RecordAsync`: the EF sink does a
  tracked `Add` on the handler's own scoped `DbContext`, and the unit-of-work commit flush persists it
  atomically with the business writes. With no ambient transaction (queries, read auditing) the sink
  saves immediately instead.

The two paths are mutually exclusive per attempt (inner records success, outer records everything else),
so no invocation produces duplicate records. Because `ResilienceDecorator` sits outside the transaction,
a retry re-enters the inner decorator: the scope therefore **resets its accumulated changes at each
attempt start**, so a record never mixes a rolled-back attempt's diffs with the succeeding attempt's.

Deliberate consequences of the positions:

- An idempotent **replay** short-circuits before the inner decorator → no second success record. The
  action executed once; the trail says so once.
- Neither decorator short-circuits or fabricates failures, so no `IResultFailureFactory` guard
  (`ELAUTH001`-style) is needed; the decorators attach to any response shape.
- A rare split can occur under resilience timeouts: an attempt commits (inner records `Succeeded`) but the
  caller sees a timeout (outer records `Failed`). Both records are true — one describes the database, the
  other the caller — and the shared correlation id links them.
- Failure/denial records include whatever changes the scope accumulated before the rollback. `Outcome`
  tells the reader those changes did **not** apply; the forensic value ("what did they try to change")
  outweighs the risk of misreading. Only changes from flushes that actually happened are present — a
  failed handler that never flushed contributes no diffs (the transaction is already rolling back, so no
  capture flush can be forced on that path).

### Automatic change capture (`IAuditChangeContributor` + a `SaveChangesInterceptor`)

Reading the change tracker once "before commit" is lossy — a handler may call `SaveChangesAsync`
mid-flight, and every flush resets original values (`EfUnitOfWork.CommitAsync` flushes again regardless).
The only reliable capture point is **each `SavingChanges`**, so the EF package attaches a scoped
`SaveChangesInterceptor` via `IDbContextOptionsConfiguration<TContext>` (the settings/messaging pattern —
the interceptor shares the DI scope with `IAuditScope`). When a scope is active, the interceptor invokes
every registered **`IAuditChangeContributor`** (an additive `IEnumerable<>` multi-registration, the
`IHandlerContextEnricher` shape); contributors append `AuditChange`s to the scope. Store-generated values
that are temporary at `SavingChanges` are patched at `SavedChanges` (contributors are scoped, so they can
carry state between the two hooks).

The default contributor diffs the change tracker for entities marked `[Audited]`, skipping `[AuditIgnore]`
properties and unmodified properties. The composition levers, in order of altitude:

- **Process** — the contributor list: unregister the default for action-records-only, add specialists
  (e.g. a semantic diff for a JSON document column), or alternate sources (temporal tables). Overlap is
  resolved by composition (exclude the entity from the default's opt-in), never by a priority system;
  invocation order is deterministic (registration order) but must never carry semantics.
- **Entity** — `[Audited]` opt-in. Fail-closed on purpose: auto-capturing every column is a PII incident
  generator (password hashes, IBANs), and opt-in also keeps framework entities (outbox messages,
  idempotency claims, the audit rows themselves) out of capture — no recursion, no noise.
- **Property** — `[AuditIgnore]`.

**Known hole, by design:** `ExecuteUpdate`/`ExecuteDelete`/raw SQL bypass the change tracker, so the
interceptor never sees them. The handler covers those paths by writing to the scope directly — the same
seam, one record.

### The EF sink (`Elarion.Auditing.EntityFrameworkCore`)

Follows the settings/idempotency package template exactly: `AddElarionAuditingEntityFrameworkCore<TDbContext>`
registers the sink, scope, interceptor, and default contributor; `UseElarionAuditing(...)` /
`[GenerateElarionAuditing]` (bundled generator, seam `OnEntitiesConfigured_GenerateElarionAuditing`,
`ELAUD001` when `[GenerateDbSets]` is missing) map the append-only `AuditLogEntry` table — indexed on
`(resource_type, resource_id, occurred_at)`, `(user_id, occurred_at)`, and `occurred_at`, so the classic
multi-property search ("Feature: Feuerlöscher, User: Max") is a plain indexed `WHERE` plus keyset paging.
Changes/details serialize as JSON columns via a source-generated context. Querying/display (localization,
"Max hat den Feuerlöscher zugewiesen", the admin UI) stays app-owned; the concept doc ships the recipe.

**Retention is off by default** — an audit trail that silently deletes itself is a compliance bug. When
`RetainFor` is configured, a hosted purge worker (the `IdempotencyKeyPurgeService` shape) deletes expired
rows on an indexed probe.

Scale positioning per ADR-0025: the one-Postgres append-only table serves the 1–10-node tier. Past that —
or for SIEM/tamper-evidence requirements — replace `IAuditTrail`; never grow this default.

## Alternatives considered

- **Entity change auditing as the primary mechanism** (SaveChanges snapshots, temporal tables). Records
  mechanics, not intent; blind to denials, failures, and non-DB actions; reconstructing "wer hat was
  gemacht" from row diffs is the reverse-mapping layer every team regrets. Rejected as the spine — it
  survives as the *diff producer* attached to action records via `IAuditChangeContributor`.
- **A single outer decorator writing success records after commit.** Simpler, but opens a crash window in
  which a committed business change has no audit record. The two-position design closes it at the cost of
  one extra thin decorator.
- **Wrapping `IUnitOfWork` to inject the record at commit.** Achieves the same atomicity with one
  decorator, but via DI-decoration of a seam two other framework decorators resolve — more magical than a
  second pipeline slot in a framework whose idiom *is* pipeline slots. Rejected.
- **Capturing diffs in the decorator by reading the change tracker after the handler returns.** Lossy
  (mid-handler flushes reset original values) and a layering violation (core is EF-free). Rejected for the
  `SavingChanges` interceptor.
- **HTTP middleware auditing.** `HttpContext`-coupled — misses JSON-RPC, MCP, and event consumers; not
  transactionally coupled. The pipeline is the transport-neutral point (the ADR-0021 argument verbatim).
- **FluentAudit-style payload capture (store the request DTO).** Request payloads carry PII; metadata-only
  is the safe default, and the details bag gives apps an explicit, redactable channel instead.

## Consequences

- Downstream apps get the "wer hat was geändert, durchsuchbar, mit Feldern" feature from attributes +
  one DI call, with intent-shaped records that align with Elarion's task-based handler grain.
- Two new generator slots in `HandlerRegistrationGenerator` (audit-commit inside the transaction,
  audit outside authorization), both soft-attached on `IAuditTrail` presence — an app without auditing
  pays nothing.
- `Elarion.Auditing.EntityFrameworkCore` (+ bundled generator) joins the EF sibling family; `ELAUD001`
  added to diagnostics.
- The decorator-pipelines concept doc loses its long-standing teaching example — `AuditDecorator` is now
  a shipped framework decorator — and needs a replacement custom-decorator example (rate limiting).
- Handlers may inject `IAuditScope` only in hosts that register auditing; module authors who want
  host-independence guard on `IsActive` (documented).
- Read auditing (`[Auditable]` on queries) works via the sink's no-ambient-transaction path; queries are
  never audited by defaults, only explicitly.

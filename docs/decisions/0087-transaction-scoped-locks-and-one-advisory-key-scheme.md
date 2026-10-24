# ADR-0087: Transaction-scoped locks are a framework helper with one advisory key scheme

- Status: Proposed
- Date: 2026-10-25
- Related: [ADR-0015](0015-ef-core-transaction-participation.md) (EF stores enlist in the caller's transaction),
  [ADR-0025](0025-distributed-scheduler-coordination.md) (scheduler claims), [ADR-0049](0049-role-leases.md) (role
  leases are not a lock API), [ADR-0073](0073-leased-work-rows.md) (leased work rows).

## Context

Some invariants span rows no constraint can express, so applications lock: `SELECT … FOR UPDATE` on an aggregate
row, or `pg_advisory_xact_lock` on a key when there is no row yet. A consuming application had fourteen hand-written
lock sites with two key-hash schemes in one advisory-lock space (`hashtextextended(…)` and a SHA-256-derived bigint),
mixed `FOR UPDATE`/`FOR NO KEY UPDATE` without a stated reason, and different provider guards. Elarion itself locked
`hashtextextended(<job name>, 0)` (scheduler) and `hashtextextended(<device id>, 0)` (device pairing) — unscoped texts
that an application key could share — and documented neither, so no application could show its keys never meet the
framework's.

## Decision

- **One key scheme.** An advisory lock on `(scope, key)` locks `hashtextextended('scope:key', 0)`.
  `AdvisoryLockKey` (Abstractions) builds the text, rejects an empty scope or one containing `:`, and reserves the
  `elarion.` prefix; the framework's scopes are listed in `AdvisoryLockKey.Scopes` and the docs. The scheduler and
  device-pairing locks move to `elarion.scheduler.job:<job>` and `elarion.devices.pairing:<device>`.
- **Transaction-scoped helpers on `DbContext`** in `Elarion.EntityFrameworkCore.UnitOfWork`, the package that owns the
  handler transaction: `LockRowAsync<TEntity>(id, RowLockMode)` (table and key column from the EF model, the four
  PostgreSQL strengths, `false` when no row has the key) and `AdvisoryLockAsync`/`TryAdvisoryLockAsync(scope, key)`.
- **Fail closed.** Both refuse to run outside a transaction (PostgreSQL would release the lock at the end of the
  statement) and on any provider other than PostgreSQL. There is no silent no-op for EF InMemory: test code that
  locks runs against PostgreSQL, as Elarion's own tests do.
- **The key scheme lives in Abstractions.** `AdvisoryLockKey` is pure string building with no runtime dependency.
  Packages that lock without the EF helper — the scheduler store, raw-SQL hosts on the EF-free tier — compute the
  same text from it. A package that cannot reference Abstractions inlines the text, and a test pins it to the
  scheme.
- Session-level locks stay out of the helper; the migration runner keeps its fixed SHA-256-derived key, documented
  next to the scheme.

## Consequences

- Applications delete their lock helpers and gain a documented, collision-checkable key space.
- **Rolling upgrades stay serialized.** Old nodes lock the bare job name or device id. For one release, new nodes take
  that legacy key as well, always after the scoped key, in a separate statement. Every old/new pair therefore
  contends on the legacy key. No old node waits on the scoped key, so the fixed order cannot deadlock. The bridge
  goes once 0.2.21 is no longer a supported upgrade source. It costs one extra lock per window claim and per pairing
  reissue until then.
- A unit test on EF InMemory that reaches a lock now fails with `NotSupportedException` instead of passing vacuously.

## Rejected alternatives

- **A no-op on non-PostgreSQL providers.** It hides the provider mismatch exactly where locking matters; it also
  covers non-relational providers with real concurrency.
- **Hashing the key ourselves (SHA-256 to bigint).** `hashtextextended` keeps the key computable in plain SQL for
  hosts on the EF-free tier and for operators reading `pg_locks`.
- **A distributed-lock abstraction.** Locks here are per transaction on the one shared PostgreSQL; a cross-process
  lock service beyond that tier is a seam replacement, not a default (ADR-0025's scale rule).

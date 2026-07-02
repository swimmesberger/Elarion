# ADR-0024: Cross-instance settings change notification over PostgreSQL LISTEN/NOTIFY

- Status: Accepted
- Date: 2026-07-02
- Related: [ADR-0011](0011-runtime-settings-subsystem.md) (the settings subsystem and its swappable
  `ISettingsChangeSource`/`ISettingsChangePublisher` seams), [ADR-0017](0017-dependency-light-core.md)
  (dependency-light core; provider-backed defaults live in opt-in sibling packages),
  [ADR-0020](0020-postgres-unlogged-l2-cache.md) (the "reuse the Postgres you already run" posture).

## Context

The settings subsystem was designed with a cross-instance change backend in mind — `ISettingsChangeSource`
(watch) and `ISettingsChangePublisher` (signal) are separate seams precisely so a distributed backend could
publish from its own transport — but only the in-process source shipped. Composed with the durable EF Core
store, that made the flagship composition **broken on more than one node**: a settings write on node A updates
the shared table, but node B's `IChangeToken` watchers — and everything built on them: `ISettingsManager.Watch`,
the settings `IConfiguration` provider, `IOptionsMonitor<T>`, and the scheduler's `${...}` live rescheduling —
never fire until node B restarts. The July 2026 soundness audit rated this *Broken multi-node* (H20); the fix
pass added a startup warning, and this ADR delivers the actual backend.

A second, related gap (M13): the EF store deliberately **skips** notifying a write that runs inside a
caller-owned ambient transaction, because signalling immediately would fire watchers for a value a later
rollback discards — so transactional settings writes were silently unnotified even in-process.

## Decision

Ship **`Elarion.Settings.PostgreSql`**: a cross-instance change source over PostgreSQL `LISTEN/NOTIFY`,
implementing the existing seams unchanged — no contract change, per the original design intent.

1. **One delivery path, commit-ordered.** `PostgreSqlSettingsChangeSource` fires watch tokens **only** from the
   notification loop. A local publish loops back through the database like any remote one (PostgreSQL delivers a
   notification to every listening connection, including the publisher's own node). There is no separate
   in-process fast path to race with, and observed ordering is the database's commit ordering.
2. **The listener is a hosted service** (`PostgreSqlSettingsChangeListener`, a `BackgroundService`): one
   dedicated connection per node, `LISTEN` + `WaitAsync`, reconnect with exponential backoff on failure.
   PostgreSQL does not queue notifications for absent listeners, so after a reconnect the listener fires **all**
   registered watches — a blanket re-read is the only way watchers converge on what was missed, and a spurious
   reload is cheap and always safe (watchers re-read through the store).
3. **Transactional writes are notified by the database itself.** The EF store signals successful writes through
   a new EF-side seam, `IEfCoreSettingsChangeNotifier`, which receives the store's `DbContext`. The default
   implementation preserves the old behavior (publish in-process, skip inside an ambient transaction). The
   PostgreSQL implementation issues `pg_notify` **on the store's own connection**, where PostgreSQL makes
   `NOTIFY` transactional: inside an ambient transaction the notification is delivered only on commit and
   discarded on rollback. This solves M13 outright for this backend — every successful settings write reaches
   every node, phantom notifications are impossible, and no commit-hook machinery is needed in Elarion.
4. **Wiring:** `AddElarionPostgreSqlSettingsChanges(connectionString | NpgsqlDataSource)` replaces the
   in-process source/publisher (authoritative regardless of registration order), swaps the EF notifier for the
   transactional one, and registers the listener. The `MultiInstanceChangeNotificationWarning` the EF store
   registration adds keys off the in-process source type, so it stops firing once this source is registered.

The payload is a small JSON document (scope kind, owner, key) on a configurable channel
(`elarion_settings_changed` by default), serialized with a source-generated `JsonTypeInfo` — well under
PostgreSQL's ~8 kB notification payload limit by schema construction (key/owner lengths are capped).

## Alternatives considered

- **Polling the settings table** — no new transport, but adds constant read load and a latency floor on every
  node; `LISTEN/NOTIFY` is push, effectively free at rest, and already available on the database the settings
  live in (the ADR-0020 posture: reuse the Postgres you already run).
- **Redis pub/sub** — a fine future provider over the same seams, but it introduces an infrastructure
  dependency the EF-store composition does not otherwise have, and it is not transactional with the settings
  write; the M13 fix would need an outbox-style hop instead of falling out of `NOTIFY` semantics.
- **Fire local watchers directly on publish (plus NOTIFY for remote nodes)** — lower local latency, but two
  delivery paths mean local watchers can observe a change that later rolls back (the M13 phantom again) and
  remote/local ordering can diverge. Loop-back keeps one semantics for every node.
- **Changing `ISettingsChangePublisher` to carry transaction context** — rejected; the seam is store-agnostic
  and transport-neutral. The transaction-awareness belongs on the EF side, hence the narrow
  `IEfCoreSettingsChangeNotifier` seam in `Elarion.Settings.EntityFrameworkCore`.

## Consequences

- Multi-node settings now work end to end: write on node A → commit → `NOTIFY` → every node's tokens fire →
  `IConfiguration` reloads → the scheduler live-reschedules `${...}` jobs. The audit's H20 is closed.
- A node observes its own writes with loop-back latency (one database round trip) instead of synchronously.
  Watch-token consumers are asynchronous by design, so this is invisible in practice.
- During a listener outage, notifications are lost (PostgreSQL does not queue them); the reconnect fires all
  watches to converge. In the startup window before the first `LISTEN` is established, changes are not
  observed — consumers that need the current value at startup read it (as the settings `IConfiguration`
  refresher already does) rather than waiting for a change.
- `Publish` on the source is best-effort (failures are logged, not thrown): the write it announces has already
  succeeded, and failing the caller for a lost notification would be worse than a delayed convergence. The EF
  store's path does not have this gap — its `pg_notify` rides the write's own connection and transaction.
- The package depends on `Elarion.Settings.EntityFrameworkCore` (for the notifier seam) and `Npgsql`; like the
  other Npgsql-backed packages it is not `IsAotCompatible`.

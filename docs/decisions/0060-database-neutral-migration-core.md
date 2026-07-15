# ADR-0060: A database-neutral migration core, and a SQLite provider

- Status: Accepted
- Date: 2026-07-14
- Related: [ADR-0057](0057-postgresql-sql-migration-runner.md) (the PostgreSQL runner this generalizes —
  its "PostgreSQL only by design" positioning is amended here; everything else stands),
  [ADR-0025](0025-distributed-scheduler-coordination.md) (replace-the-seam scale doctrine),
  [ADR-0054](0054-device-identity-and-provisioning.md) / [ADR-0053](0053-bidirectional-client-connections.md)
  (the edge/device story SQLite serves).

## Context

[ADR-0057](0057-postgresql-sql-migration-runner.md) shipped `Elarion.Migrations.PostgreSql`: a
Flyway-shaped, PostgreSQL-only SQL migration runner for EF-free (NativeAOT) hosts. It deliberately
rejected multi-dialect support ("no dialects beyond PostgreSQL", lesson 6) — but the same ADR left one
door open: *"Contracts live in the package (nothing in core consumes them — no `Elarion.Abstractions`
seam; a second provider, if ever, extracts a neutral core then, pre-1.0 clean break)."*

A second provider is now wanted. Elarion's edge story — device identity ([ADR-0054](0054-device-identity-and-provisioning.md)),
raw-socket and WebSocket connections ([ADR-0053](0053-bidirectional-client-connections.md)) — points at
NativeAOT hosts that run **single-node on a local SQLite file** rather than against a shared PostgreSQL.
Those hosts want the same startup-applied, roll-forward migration convention the PostgreSQL tier has.
SQLite is not a dialect knob on the PostgreSQL runner; it is a different engine with a different locking
model, so this is exactly the "extract a neutral core then" moment ADR-0057 anticipated.

The tension to resolve honestly: adding SQLite must **not** reopen the multi-dialect door ADR-0057 closed.
The distinction is that a provider is a **whole package** (its own SQL, its own locking, its own NuGet
dependency), never a runtime `Dialect` enum or a config switch inside one runner. Requests for a dialect
*setting* still get the doctrine answer; a genuinely new engine gets a new provider package.

## Decision

Split the runner into a database-neutral engine plus per-engine provider packages.

### `Elarion.Migrations` — the neutral core

Extract everything that is not database-specific into a new `Elarion.Migrations` package
(`IsAotCompatible`, `Microsoft.Extensions.*` abstractions only, **no database driver**):

- The whole script model and policy: discovery (embedded-resource scan), `V…`/`R__` parsing, SHA-256
  normalized checksums, versioning, out-of-order and repeatable **planning**, and the roll-forward
  execution loop (`MigrationRunner`) — including the no-repair / failed-row / fail-closed invariants that
  are the point of ADR-0057. Keeping this policy in **one** place is what stops two providers from
  diverging on the semantics that matter.
- The public contract: `IMigrationRunner`, `MigrationScriptInfo`, `MigrationValidationResult`,
  `OutOfOrderPolicy`, `ResolveAction`, the exceptions, and the neutral `MigrationOptions` base (script
  sources, out-of-order policy, history-table name, command/lock timeouts, apply-on-startup).
- The shared registration helper `AddElarionMigrationRunner(options, runnerFactory)` (the one-runner
  guard + migrate-before-ready hosted service), reused by every provider.

The contracts live in `Elarion.Migrations`, **not** `Elarion.Abstractions`: nothing in core/handler-pipeline
consumes them, and migrations are a standalone host concern (the ADR-0057 rule, just relocated from the
PostgreSQL package to the shared package).

### `IMigrationDatabase` — the provider seam

The neutral runner drives a provider through a narrow seam designed for the **strongest** implementation
(the PostgreSQL session-lock + non-transactional-DDL case), per the seams-for-the-strongest-impl rule:

- `IMigrationDatabase.ConnectAsync(exclusive, ct)` opens one dedicated connection and, when `exclusive`,
  acquires the engine's exclusive migration lock; read-only operations (`Validate`/`GetPending`) pass
  `false`.
- `IMigrationSession` exposes the history-table operations (ensure/exists/load/insert/delete/mark-applied)
  and two execution primitives: `ExecuteInTransactionAsync(sql, historyRowFactory)` (script + history row
  atomic — the no-repair invariant) and `ExecuteWithoutTransactionAsync(sql)` (the `-- elarion:
  no-transaction` path). The runner keeps all failure policy; the provider only speaks SQL.

`Elarion.Migrations.PostgreSql` keeps its exact behavior, reduced to a thin provider (session advisory
lock, `to_regclass` existence check, the dollar-quote-aware statement splitter). `PostgreSqlMigrationRunner`
/ `PostgreSqlMigrationOptions` / `AddElarionPostgreSqlMigrations` are unchanged API — the runner is now a
one-line façade over the neutral `MigrationRunner`.

### `Elarion.Migrations.Sqlite` — the SQLite provider

A new provider (`Microsoft.Data.Sqlite`, `IsAotCompatible`) for the single-node / edge tier.
`AddElarionSqliteMigrations(connectionString, o => …)` / `SqliteMigrationRunner` mirror the PostgreSQL
surface. Because the semantics differ from the strong impl, the provider implements the **closest**
semantics and documents the deltas:

- **Locking.** SQLite has no advisory lock, and the multi-node concern the PostgreSQL lock solves does not
  exist — a SQLite database is one file per node, never shared across nodes (network filesystems + SQLite =
  corruption). So the migration lock's correct scope is *this process*: an exclusive session takes a
  per-file **in-process** lock (a semaphore keyed by the database path), holding it across the per-script
  transactions; concurrent runners on one file serialize on it deterministically, deadlock-free — the
  closest analogue of the session advisory lock at SQLite's single-node/single-process scope. Cross-process
  contention on one file (an unsupported topology) is bounded by `busy_timeout` (from `LockTimeout`) and the
  history table's `version` uniqueness, not this lock. A whole-connection `PRAGMA locking_mode = EXCLUSIVE`
  is deliberately **rejected**: two connections under it each retain a shared lock and deadlock trying to
  promote to exclusive. Pooling is disabled so `Dispose` truly closes the connection.
- **Non-transactional scripts.** SQLite has full transactional DDL and no `CREATE INDEX CONCURRENTLY`, so
  `-- elarion: no-transaction` is rarely needed; when a script uses it, the provider runs it in autocommit
  mode (each statement commits on its own), preserving the "may be half-applied on failure" semantics the
  failed-row machinery expects.
- **Command timeout.** SQLite has no server-side statement timeout, so `MigrationOptions.CommandTimeout`
  does not apply; `LockTimeout` governs how long a contended runner waits for the exclusive lock.
- **History table.** SQLite dialect (`sqlite_master` existence check, `TEXT`/`INTEGER` columns, partial
  unique index on `version`); the roll-forward, history-row-commits-with-its-script invariant is identical.

## Consequences

- ADR-0057's positioning clause "PostgreSQL only by design" is **amended**: the runner is now
  engine-neutral with per-engine providers. Every other ADR-0057 decision (no undo, no repair, no
  placeholders, no dialect *setting*, fail-closed discovery, roll-forward) stands unchanged and is enforced
  once in the neutral core.
- The decision table gains a row: EF app → EF migrations; multi-node PostgreSQL AOT host → the PostgreSQL
  provider; **single-node / edge AOT host on a local file → the SQLite provider**; either + a real deploy
  pipeline → bundles/Flyway in CI.
- A third engine (SQL Server, …) is now a mechanical exercise: a new provider package implementing
  `IMigrationDatabase`, no core change. This is composition by new package, not a dialect flag — the
  doctrine line ADR-0057 drew is preserved.
- SQLite's tests run in-process (no Docker), so the SQLite provider's execution model is covered on every
  `dotnet test`; the PostgreSQL provider keeps its Testcontainers integration tests.
- `docs/reference/packages.mdx`, the `AGENTS.md` package table, `docs/capabilities/sql-migrations.mdx`, and
  the decisions index gain the two new packages.

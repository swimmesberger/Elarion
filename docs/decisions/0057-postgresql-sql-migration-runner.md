# ADR-0057: A Flyway-shaped PostgreSQL migration runner for EF-free (AOT) hosts

- Status: Accepted (the "PostgreSQL only by design" positioning is amended by
  [ADR-0060](0060-database-neutral-migration-core.md), which extracts the neutral `Elarion.Migrations`
  core this ADR anticipated and adds a SQLite provider; every other decision here stands)
- Date: 2026-07-13
- Related: [ADR-0060](0060-database-neutral-migration-core.md) (the neutral-core extraction + SQLite provider),
  [ADR-0025](0025-distributed-scheduler-coordination.md) (scale positioning),
  [ADR-0056](0056-postgres-extensions-posture.md) (one-Postgres composition posture),
  [ADR-0026](0026-openapi-http-transport.md) (the migrate-on-startup guard note),
  [ADR-0041](0041-blob-streaming-connections-clone-the-context-connection.md) (the own-connection principle this deliberately
  does *not* follow — the runner predates any app connection).

## Context

Elarion's persistence story is EF-model-first: every framework table rides the app's `DbContext`
via `UseElarion*` model hooks and the app's EF migrations. That stays the default and is not in
question here.

The gap is the **EF-free host**: an app published NativeAOT that uses raw Npgsql (with or without
Elarion's EF-based packages — typically without) still needs schema management. EF Core's NativeAOT
support remains experimental in EF 10 (Microsoft recommends against production use), and runtime
`Database.Migrate()` is not part of that story at all. EF migrations still cover such an app at
*deploy time* (author with a design-time-only `DbContext`, apply via `migrations script --idempotent`
or a migration bundle on deploy infrastructure) — but Elarion's happy path at the 1–10-node tier is
**startup-applied migrations**, and small apps at that tier often have no deploy pipeline to hang a
bundle on. Nothing maintained fills that hole: Evolve (the .NET Flyway) is dormant (last release
June 2023, no trimming/AOT posture, seven dialects we don't want), and Flyway itself is a JVM tool.

Flyway is the right prior art — and its recurring production failures are documented well enough to
design against explicitly:

1. **Checksum brittleness.** CRC32 over raw bytes means a CRLF/LF difference (git `autocrlf`, an
   editor touching whitespace) breaks deployment validation, teaching teams to reach for `repair`.
2. **`repair` as a habit.** A blunt "make the errors go away" command that rewrites history-table
   checksums and masks real drift between environments.
3. **The `outOfOrder` flag culture.** Strict version ordering by default + parallel branches means
   the first blocked deploy after a merge teaches every team to set `outOfOrder=true` globally —
   the strict default produced neither safety nor order, just a ritual flag.
4. **Transactional locking vs non-transactional DDL.** Flyway ≥9.1.2 defaults to a
   *transaction-scoped* advisory lock on PostgreSQL, which hangs or deadlocks
   `CREATE INDEX CONCURRENTLY` migrations indefinitely (flyway/flyway#3497, #3508, #3854; the fix
   is a config flag users discover after the hang).
5. **Failed-row limbo.** A failed migration leaves a `success=false` history row that blocks all
   subsequent runs until a manual `repair` — the recovery path is a CLI incantation, not an API.
6. **Feature tiering.** Undo, dry-run, and drift checks are paid-edition features; the free tier's
   answer to a bad migration is "restore a backup".

## Decision

Ship **`Elarion.Migrations.PostgreSql`**: a minimal, PostgreSQL-only, `IsAotCompatible` SQL
migration runner for startup-applied migrations in EF-free hosts. Dependencies: `Npgsql` +
`Microsoft.Extensions.*` abstractions only. Contracts live in the package (nothing in core consumes
them — no `Elarion.Abstractions` seam; a second provider, if ever, extracts a neutral core then,
pre-1.0 clean break).

**Positioning (mandatory):** the EF tier keeps EF migrations — this runner is the *AOT tier's*
tool, never a competing default. It does **not** ship Elarion framework-table scripts (the EF-based
packages remain EF-delivered); if an EF-free tier of framework features ever materializes, script
delivery becomes its own ADR.

### Script model

- Embedded resources, `V{version}__{description}.sql` and `R__{description}.sql` (repeatable).
  Discovery reads the assembly manifest (AOT-safe, no filesystem). Docs recommend **timestamp
  versions** (`V20260713093000__add_devices.sql`) — they make branch-merge collisions structurally
  rare instead of policing them at deploy time.
- Startup validation is fail-closed and total: duplicate versions, malformed names, undecodable
  content each fail with the offending resource named. No silently skipped scripts.
- **Checksums are SHA-256 over normalized content** (BOM stripped, CRLF→LF). Line-ending churn can
  never invalidate an applied script (lesson 1). A genuine mismatch fails validation with the
  script name, both hashes, and the two legal resolutions (revert the edit, or add a new script).

### Execution model

- One runner, **one dedicated connection, single-threaded**, guarded by a **session-level**
  `pg_advisory_lock` (never `pg_advisory_xact_lock` — lesson 4). Session scope means a crashed
  runner releases the lock with its connection; there is no lock row to clean up. Concurrent
  startups (1–10 nodes, ADR-0025) serialize on the lock; waiters re-read history and no-op.
- Each versioned script runs in its own transaction, and **its history row commits in that same
  transaction**. A failed transactional migration therefore leaves *no* history row — rerun after
  fixing, no repair step exists or is needed (lessons 2, 5).
- `-- elarion: no-transaction` (first non-comment line) opts a script out for statements PostgreSQL
  forbids in a transaction (`CREATE INDEX CONCURRENTLY`, …). Only here can a failure leave a
  mid-applied state: the runner records an explicit failed row and subsequent runs fail closed,
  naming the script and the recovery API — `IMigrationRunner.ResolveFailedAsync(version,
  ResolveAction.Retry | MarkApplied)` — a deliberate in-code decision at the call site, not a CLI
  habit (lesson 5).
- **Out-of-order policy: `Warn` by default** — a pending script versioned below an applied one is
  applied, logged as a warning, and recorded in true execution order (`installed_rank`). `Deny` is
  the opt-in for teams that want strict ordering. Rationale: Flyway's strict default only taught
  teams a global flag (lesson 3); the history table records truth either way, and timestamp
  versions make the case rare.
- Repeatable scripts run after versioned ones, in name order, only when their checksum changed.
  Doc rule: repeatables are for idempotent `CREATE OR REPLACE` surfaces (views, functions) — never
  destructive DDL.

### API and host integration

- `IMigrationRunner`: `MigrateAsync`, `ValidateAsync` (checksum + pending report, no writes),
  `GetPendingAsync`, `BaselineAsync(version)` (explicit only — no `baselineOnMigrate` auto-magic),
  `ResolveFailedAsync`. Everything in the box, no tiering (lesson 6). Undo/rollback is **rejected**,
  not deferred: roll forward; a dropped column's data cannot be un-dropped, and shipping the
  pretense invites relying on it.
- `AddElarionPostgreSqlMigrations(o => …)` registers the runner + a hosted service that migrates
  **before the host reports ready** and fails startup on error. Failing to start on a failed
  migration is correct (serving traffic against a half-migrated schema is worse); the Flyway
  complaint this echoes is really about *opaque recovery*, which the explicit failure states and
  `ResolveFailedAsync` address.
- History table `elarion_schema_history` (version, description, script name, checksum,
  `installed_rank`, applied-at, duration, state), created by the runner itself under the same lock.
- No placeholders/variable substitution in v1 (Flyway's `${}` escaping is a recurring papercut);
  scripts are plain SQL. `IVariableSource` integration can arrive later behind an explicit opt-in.
- Build-time script validation (an analyzer over `AdditionalFiles` catching naming/duplicate errors
  at compile time) is noted as a natural Elarion follow-up, not part of v1.

## Consequences

- EF-free AOT hosts get the convention-over-configuration startup path the EF tier already has;
  the decision table (EF app → EF migrations; AOT/no-EF app → this runner; either + real deploy
  pipeline → bundles/scripts in CI) goes in a deployment concept doc.
- The runner is deliberately not Flyway: no undo, no repair, no placeholders, no baselines-on-
  migrate, no dialects beyond PostgreSQL. Requests for those get the doctrine answer — replace the
  tool (Flyway/Evolve in CI) rather than grow the default (ADR-0025).
- `docs/reference/packages.mdx`, the package table in `AGENTS.md`, and the decisions index gain
  entries when the package lands.
- App-side *data access* under AOT is out of this ADR's scope and owned by
  [ADR-0058](0058-aot-sql-row-mapping.md) (`Elarion.Sql`, explicit generated row mappers) — chosen
  over recommending Dapper.AOT, whose call-site interception silently falls back to reflection
  Dapper on indirect usage (a runtime failure under NativeAOT).

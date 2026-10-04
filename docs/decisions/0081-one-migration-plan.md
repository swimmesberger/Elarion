# ADR-0081: One migration plan — SQL, code and EF steps share a version sequence, a history and a lock

- Status: Proposed
- Date: 2026-10-04
- Related: [ADR-0057](0057-postgresql-sql-migration-runner.md) and
  [ADR-0060](0060-database-neutral-migration-core.md) (the SQL runner this generalizes; their roll-forward,
  checksum and no-repair rules stand), [ADR-0070](0070-contract-set-registration.md) (compile-time registration
  of an interface's implementations), [ADR-0017](0017-dependency-light-core.md) (driver-free neutral cores),
  [ADR-0025](0025-distributed-scheduler-coordination.md) (the 1–10 node scale rule), and the
  [migrations](../capabilities/migrations.mdx) capability page.

## Context

A schema change that carries data is rarely one tool's job. The safe shape of a column change is *expand →
backfill → contract*: add the new column nullable, fill it with something only C# can compute (parsing, hashing,
a domain service, configuration), then tighten it. Today those three steps live in three places with three
histories and no shared order:

- `Elarion.Migrations` runs **SQL scripts** with a history, checksums and an advisory lock — but only SQL, and
  only for the EF-free tier.
- EF applications run **EF migrations** (`Database.MigrateAsync()`) with EF's own history and EF's own lock —
  and have no hook between two migrations.
- The **C# backfill** is hand-rolled: a marker written separately from the work (a crash between them repeats or
  skips it), no lock (two instances starting together both run it), and nothing ordering it between the expand
  and the contract it sits between.

The ordering is the real problem. "Run the backfill after the expand and before the contract" is today a
deployment convention — a second release, a manual gate — instead of something the tool guarantees. Making the
convention a guarantee needs one sequence that all three kinds of change live in.

The earlier attempt (`Elarion.DataMigrations`, a separate package pair with its own `elarion_data_migrations`
table and lock, ordered by ordinal id and sequenced after the schema runner only through hosted-service
registration order) solved exactly-once and rollback but not ordering: the backfill could run *after* the
schema step or *before* the next one, never *between* two, and an EF host had to call two runners by hand.

## Decision

There is **one migration plan** made of typed **steps** contributed by step **sources**. One version-ordered
sequence, one history table, one lock, one runner — for both host tiers.

### Steps and sources

```csharp
public abstract class MigrationStep {          // Elarion.Migrations
    string Kind { get; }  string Name { get; }  string? Version { get; }  string Description { get; }
    string? Checksum => null;  bool UseTransaction => true;  bool RecordsFailedRow => false;
    ValueTask<bool> IsAlreadySatisfiedAsync(MigrationStepContext, CancellationToken) => false;
    Task ExecuteAsync(MigrationStepContext, CancellationToken);
}
public interface IMigrationStepSource { MigrationStepSet Discover(IServiceProvider services); }
```

Three sources ship:

| Kind | Source | Declaration | Package |
| --- | --- | --- | --- |
| `sql` | embedded scripts | `V{version}__x.sql`, `R__x.sql` (unchanged conventions) | `Elarion.Migrations` |
| `code` | `ICodeMigration` | a class with `Version`/`Description`/`ExecuteAsync`, registered at compile time with `[GenerateContractSetRegistration(typeof(ICodeMigration))]` (ADR-0070) or `AddCodeMigration<T>()` | `Elarion.Migrations` |
| `ef` | EF Core migrations | `o.AddEntityFrameworkMigrations<TContext>()` — each migration is one step | `Elarion.Migrations.EntityFrameworkCore` |

`ICodeMigration` is stateless metadata held as a singleton; it resolves what it needs from
`MigrationStepContext.Services`, a fresh scope per step. No new generator: ADR-0070's contract-set generator
already provides the compile-time, AOT-safe registration of an interface's implementations. The runner collects
`IEnumerable<ICodeMigration>` and `IEnumerable<IMigrationStepSource>` from DI next to the options' own sources.

### One ordering

Every step carries a **version** — numeric segments, conventionally a timestamp — and the plan sorts *all*
versioned steps of *all* sources into one ascending sequence; repeatable scripts run afterwards by name, as
before. Versions are unique across the whole plan regardless of kind (a script and a code step claiming one
version is a validation error naming both). An EF migration's version is the timestamp prefix of its id
(`20260901110000_OrdersAddTotal` → `20260901110000`), so the three kinds share the timestamp convention that
already made branch-merge collisions rare, and expand → backfill → contract is simply three steps whose
timestamps say so. Out-of-order arrivals keep the `Warn`/`Deny` policy; baselining at version V treats every step
of every kind at or below V as applied.

### Semantics of a step

- **Transaction by default.** A step runs in a transaction on the plan's single connection — the one that holds
  the lock — and its history row is inserted in that same transaction. The runner (not the provider) owns the
  transaction: the session seam shrinks to `Connection`, history operations, and
  `ExecuteSqlAsync(sql, transaction?)`, so one code path serves every kind of step on every provider.
- **Failure stops the plan.** A throw rolls the transaction back, records nothing for the step, surfaces as
  `MigrationExecutionException` naming it, fails startup, and the next start retries. Later steps never run
  before an earlier one succeeded.
- **Opt-out for long batches.** `UseTransaction => false` runs a code step without a transaction. The stated
  consequence: partial writes may remain, nothing is recorded, the next start reruns it — it must be idempotent
  and resumable. This differs from a SQL `no-transaction` script on purpose: a half-applied *schema* records a
  `failed` row and fails closed until `ResolveFailedAsync` (the ADR-0057 rule, kept via
  `MigrationStep.RecordsFailedRow`), whereas a half-run *data* conversion is written to be rerun.
- **Honest limits.** A service resolved from `Services` (a `DbContext`, `ISqlSession`) uses its own connection,
  outside the step's transaction, so its writes are not rolled back; the guidance is idempotency, or adopting
  `Connection`/`Transaction`.
- **Satisfied and baseline.** `IsAlreadySatisfiedAsync` runs before the step, outside a transaction; `true`
  records outcome `satisfied` without running (fresh install, adopted database). `BaselineAsync(version)`
  remains the explicit prefix adoption. Neither is automatic.
- **Exactly once.** The existing exclusive lock (PostgreSQL session advisory lock, SQLite per-file in-process
  gate) is taken once for the whole plan; a waiting instance re-reads the history after it holds the lock.
- **Repeatable (`R__`) scripts keep their semantics** — rerun on checksum change, after all versioned steps,
  not recorded on failure. Code and EF steps are versioned only.

### EF Core migrations as steps

`Elarion.Migrations.EntityFrameworkCore` is a new sibling package (below). Its source enumerates
`IMigrationsAssembly.Migrations` for the context and emits one step per migration. A step executes
`IMigrator.GenerateScript(previousId, id, NoTransactions)` — exactly what `dotnet ef migrations script` prints for
that one migration — through `MigrationStepContext.ExecuteSqlAsync`, so the migration and the plan's history row
commit atomically; the plan owns the transaction, hence `NoTransactions`. EF's `__EFMigrationsHistory` row is
part of that script, so `dotnet ef` tooling and EF bundles stay truthful; the plan's history is the authority
for ordering and exactly-once. **Adoption needs no baseline:** an EF step whose id is already in
`__EFMigrationsHistory` reports `IsAlreadySatisfiedAsync` and is recorded as `satisfied`. An EF host drops
`Database.MigrateAsync()` and runs the one runner. Limit, stated in the docs: operations EF flags
`suppressTransaction` (`CREATE INDEX CONCURRENTLY`) cannot run in the plan's transaction and belong in a
`-- elarion: no-transaction` SQL script; the generated script must be a plain statement batch (PostgreSQL,
SQLite).

### History

One table, `elarion_schema_history`, generalized from script-shaped to step-shaped:

`installed_rank, kind, version, description, step_name, checksum, outcome, applied_at, duration_ms`

`kind` is `sql`/`code`/`ef`/`baseline`; `step_name` is the file name, the code type's name, or the EF migration
id; `checksum` is a script's SHA-256 and null for code and EF steps (their identity is kind + version);
`outcome` is `applied`/`satisfied`/`baseline`/`failed`. Version uniqueness stays a partial unique index, which
now enforces the cross-kind uniqueness too. A kind change under a recorded version is a validation error — a
version identifies one step forever.

**Existing history mapping (breaking).** A table of the old layout (`script_name`, `state`, no `kind`) is
upgraded **in place** by the first exclusive run, under the lock: `script_name → step_name`,
`state → outcome`, `kind` added (`sql`, or `baseline` for baseline markers). Nothing re-runs; checksums stay
valid. Read-only calls (`ValidateAsync`, `GetPendingAsync`) read the old layout through column aliases without
writing. Both providers do this, covered by tests. Anyone who queried the old column names directly must
switch.

### Packaging

- **`Elarion.Migrations`** (AOT-compatible, no driver, no EF) owns the plan, the runner, `MigrationStep`,
  `IMigrationStepSource`, `ICodeMigration`, and the SQL script source.
- **`Elarion.Sql.PostgreSql` / `Elarion.Sql.Sqlite`** keep their packages and implement the narrowed
  `IMigrationSession`. The PostgreSQL provider remains the only home of the advisory lock.
- **`Elarion.Migrations.EntityFrameworkCore`** (new) holds the EF step source. It could have lived in
  `Elarion.EntityFrameworkCore`, but that package is the provider-neutral model-convention/marker package every
  EF assembly references; making it depend on the migration engine would pull the runner into assemblies that
  never run migrations (a persistence class library), and `Microsoft.EntityFrameworkCore.Relational` — the
  `IMigrator` home — into a package that deliberately carries only the base EF package. A per-capability EF
  sibling is the repository's established shape (`Elarion.Settings.EntityFrameworkCore`, …). The EF-free
  NativeAOT tier never references it, so it stays AOT-clean.
- There is **no separate data-migrations package**.

An EF host's references: `Elarion.Migrations` + `Elarion.Migrations.EntityFrameworkCore` + a provider package
(`Elarion.Sql.PostgreSql`, whose `AddElarionPostgreSql` supplies the plan's dedicated connection and lock — it
may point at the same database as the `DbContext`).

## Consequences

- **Breaking.** `MigrationScriptInfo` → `MigrationStepInfo` (+ `Kind`); `MigrateAsync` returns
  `IReadOnlyList<MigrationStepResult>` (step, outcome, duration); `MigrationValidationError.ScriptName`,
  `MigrationExecutionException.ScriptName` and `MigrationFailedStateException.ScriptName` → `StepName`;
  `IMigrationSession` loses `ExecuteInTransactionAsync`/`ExecuteWithoutTransactionAsync` for `Connection` +
  `ExecuteSqlAsync` + a transaction parameter on `InsertHistoryRowAsync` (provider authors only);
  `AddElarionMigrations`'s options callback is optional and "at least one script source" becomes "at least one
  step source", checked when the runner is built; the history table layout changes (upgraded in place); EF hosts
  add `AddEntityFrameworkMigrations<TContext>()` and drop `Database.MigrateAsync()`. A reference-branch `Elarion.DataMigrations` consumer maps to
  `ICodeMigration` (`Id` → `Version`, `DataMigrationContext` → `MigrationStepContext`).
- **Ordering becomes a guarantee**, not a convention: a contract step cannot run before the backfill it depends
  on, and a backfill cannot run before its expand.
- **Rollback of an EF step now relies on the plan's transaction** instead of EF's. Providers that cannot run a
  migration's DDL transactionally behave as their scripts would; EF operations flagged `suppressTransaction`
  need a SQL `no-transaction` script.
- **A new package** (`Elarion.Migrations.EntityFrameworkCore`); no change to existing package dependencies.
- **Startup fails when a step fails**, consistent with the schema runner.
- **Scale.** 1–10 nodes on one database, one lock, one table. Hours-long conversions, progress checkpoints and
  per-batch leases are a background job, not a plan step.

## Rejected alternatives

- **A separate `Elarion.DataMigrations` package pair with its own table and lock (the earlier,
  compatibility-driven design).** It kept `Elarion.Migrations` untouched at the price of two histories, two
  locks, two ordering schemes and the one thing that matters left to the host: the backfill could never be
  sequenced *between* an expand and a contract. It also needed an EF host to call two runners in the right
  order by hand. Designing around "don't change the SQL runner" is the pre-1.0-compatibility instinct the
  repository rejects; the history and planner are step-shaped by nature once code exists.
- **Running EF migrations via `Database.MigrateAsync(target)` calls between code steps.** EF's migrator uses its
  own history table, lock and transaction scope, so exactly-once and rollback would again be split between two
  mechanisms, and the interleave would be a hand-written script of `MigrateAsync` calls.
- **Ordering by ordinal code-migration id, with the schema runner first.** Two orderings (ordinal id vs.
  version) that can disagree, and still no interleave.
- **A scheduler one-time job or a coordination lease as the guard.** A scheduled job runs on every node and has
  no record; a role lease is a heartbeat election that can lapse mid-run and never promises once-ever.
- **Reflection scanning for code steps, or a dedicated generator.** ADR-0070 already registers an interface's
  implementations at compile time; a second generator for the same shape adds surface for no gain.
- **Keeping the history script-shaped and adding a side table for non-script steps.** Two tables to join to
  answer "what ran, in what order".
- **Hosting the EF step source in `Elarion.EntityFrameworkCore`.** See Packaging.
- **Per-kind opt-out flags for the plan transaction beyond `UseTransaction`.** One documented switch with a
  stated consequence is enough; more would be knobs for combinations nobody can reason about.

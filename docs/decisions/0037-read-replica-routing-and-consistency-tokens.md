# ADR-0037: Read-replica query routing and read-your-writes consistency tokens

- Status: Proposed
- Date: 2026-07-05
- Related: [ADR-0025](0025-distributed-scheduler-coordination.md) (scale positioning: swap a seam, never grow
  a default), [ADR-0017](0017-dependency-light-core.md) (seam neutral, provider opt-in),
  [ADR-0003](0003-decorator-attachment-predicates.md) (`AppliesTo` attachment),
  [ADR-0015](0015-ef-core-transaction-participation.md) (stores enlist in the ambient transaction),
  [ADR-0021](0021-idempotency.md) (transaction ownership),
  [ADR-0027](0027-declarative-request-validation.md) (business rules validate inside the transaction)

## Context

The first data-tier scaling move at the 1–10-node tier is not sharding or a second primary — it is a
streaming replica: offload list/dashboard/report reads, gain a warm standby. ADR-0025 pins shipped defaults to
*one* PostgreSQL, so replica support is by definition past the tier: it must arrive as an opt-in seam that
leaves the single-Postgres happy path byte-identical, never as new complexity on a default.

**Who this is for.** Read-heavy, high-volume, often public applications where the read:write ratio is lopsided
and the reads are the scaling pressure — the primary handles writes comfortably, but query volume wants to
fan out across replicas.

**Reference deployment.** The concrete shape of the target tier is a self-hosted Kubernetes cluster (k3s) on
owned or budget bare-metal (Strato/Hetzner), with PostgreSQL run by the [CloudNativePG][cnpg] operator across
**≥3 instances in different locations**. That changes the availability story from the raw-`libpq` case: the
operator *does* provide automatic failover — lease-based leader election promotes a replica when the primary
fails (`.spec.failoverDelay` debounces transient blips, quorum prevents split-brain), so a primary loss is a
**brief write interruption during promotion (seconds)**, not an indefinite write outage, and reads keep
serving from the surviving replicas throughout. It also simplifies routing: CloudNativePG publishes stable
Services — `<cluster>-rw` (primary, read-write), `<cluster>-ro` (replicas, read-only), `<cluster>-r` (any) —
so the two data-source views are just two DNS names, with Kubernetes doing replica load-balancing on `-ro` and
re-pointing `-rw`/`-ro` after a promotion. Elarion still does **not** implement failover; it *consumes* the
operator's endpoints and follows the promotion. The honest pitch is "scale reads, survive a primary loss as a
brief write pause plus continuous stale reads" — the operator supplies the HA mechanics, the framework
supplies routing and causal reads. Geo-distributing the ≥3 nodes means WAN-variable replication lag, which is
exactly why the causal LSN token beats a fixed time window (no need to tune a conservative worst-case delay)
and why the `ReplayWaitTimeout` + fail-open posture below earn their keep.

**Recommended replication posture: quorum-synchronous.** For a ≥3-node geo cluster the sane deployment default
is *quorum* synchronous replication — `ANY 1 (…)` in `synchronous_standby_names`, `synchronous_commit =
remote_apply`, and CloudNativePG `dataDurability: preferred`. Two payoffs: **RPO≈0 on failover** (a committed
write is on at least one standby, so the seconds-long promotion loses no data), and the causal replay-wait is
**near-instant** (a quorum member is already *applied*, not just flushed — `remote_apply` is the mode that
buys that; `on`/flush would still need the replay wait more often). `preferred` self-heals: if standbys are
unavailable the primary degrades to async and keeps accepting writes (guarantee relaxes for that window)
rather than blocking — matching the read-availability priority. **Crucial nuance, and the reason sync does not
replace the token gate:** quorum sync guarantees the write is applied on *some* member of the quorum, never on
*the particular* replica a given read is load-balanced onto. So per-read read-your-writes still needs the LSN
gate — synchronous replication makes that gate cheap and gives durability; it is not itself the consistency
mechanism. (This resolves the earlier flush-vs-replay question: recommend `remote_apply`.)

Two standing invariants constrain the design:

- **Handlers inject the concrete `DbContext`.** There is deliberately no repository or `IAppDbContext`
  between a handler and EF, and routing is not a reason to introduce one. The read/write split must happen
  *below* EF, at the connection source, leaving handler code untouched.
- **CQRS markers already carry intent.** `IQuery`/`ICommand` drive HTTP verb inference and
  `TransactionDecorator.AppliesTo` today; "is this dispatch read-only?" is the same question asked one layer
  lower.

The hard problem is not routing but **staleness**. Streaming replication is asynchronous; three anomalies
matter, and they deserve different answers:

1. *Read-your-own-writes*: create → immediate list, the row is missing. The one users notice and report.
2. *Monotonic reads*: successive reads land on differently-lagged replicas and data appears to go backwards.
3. *Cross-user lag*: A writes, B reads a second later. Inherently eventual under asynchronous replication.

Generic frameworks solve (1) heuristically — Rails' [automatic role switching][rails-switching] sends a
user's reads to the primary for a fixed window (2 s, `DatabaseSelector::Resolver`) after any write, tracked
in the session, so "Rails guarantees 'read your own write' … within the `delay` window." The *causal*
mechanism is
better: capture the write position on the primary and require the replica to have replayed past it before
serving the read — MySQL exposes it as a GTID ([ProxySQL GTID consistent reads][proxysql-gtid] tracks
per-server GTIDs and routes each read to a replica known to have applied it; [Vitess reads-after-write][vitess-raw]
is the same idea), and PostgreSQL as a WAL LSN. On PostgreSQL the primary
exposes its write position via `pg_current_wal_insert_lsn()`; a reader can then require a standby to have
replayed past it. The classic way ([GitLab database load balancing][gitlab-lb] is the canonical framework
prior art) is a client-side poll of `pg_last_wal_replay_lsn()` across replicas, with the LSN stashed per user
(GitLab keeps it in Redis for a 30 s window). **PostgreSQL 17** collapses that whole loop into one server-side
primitive — `pg_wal_replay_wait(lsn, timeout_ms)` blocks until the standby catches up
([WAIT FOR LSN technique][pachot-waitlsn]) — and **PostgreSQL 19** promotes it to a proper top-level command,
[`WAIT FOR LSN`][pg19-waitfor] (same author, added modes, a `NO_THROW` status return; merged to master
2025-11, on track for the PG19 release expected late 2026). So the package requires **PostgreSQL 17+** and
builds on that primitive rather than carrying the older poll (PG17 shipped 2024-09; requiring it is cheaper
than maintaining a second, racier code path forever), selecting the command form once 19 is available.
What generic frameworks lack is a place to put the token. Elarion owns the commit point (the unit of work), the transport surface, *and* the generated
TypeScript client, so it can round-trip the token end-to-end with no sticky sessions and no server-side
per-user state.

Platform facts shaping the always-primary hygiene list: hot standbys reject writes (a misroute fails loudly,
never silently); `UNLOGGED` tables — the ADR-0020 L2 cache — are not replicated and error on standby reads;
`LISTEN`/`NOTIFY` (ADR-0024) is unavailable during recovery. Npgsql's multi-host data source provides
primary/standby targeting natively (`TargetSessionAttributes`), including `PreferStandby`, which degrades to
the primary when no standby exists.

## Decision

**1. Vocabulary in Abstractions; machinery in an opt-in package.** `[RequireImmediateConsistency]`
(class-level on handlers; assembly/`[AppModule]` scoping mirrors `[ElarionAuthorizationDefaults]`,
most-specific-wins) declares a *consistency need*, never a topology: without the routing package every read
is already immediate and the attribute is inert, so handlers stay deployment-neutral. The opaque
`ConsistencyToken` and the rail item types live in Abstractions too. The machinery ships as
**`Elarion.EntityFrameworkCore.ReadReplicas`** (`AddElarionReadReplicas<TDbContext>(…)`; EF/Npgsql pairing
like `Elarion.Scheduling.EntityFrameworkCore`; not `IsAotCompatible`-flagged, same posture as the other
Npgsql-backed packages). ADR-0025 design test: the default still covers 10 nodes on one Postgres — this
package *is* the seam swap for the data tier.

**2. Route below EF, at the connection source, per dispatch scope.** The package resolves two stable
data-source views — **`Primary`** and **`PreferStandby`** — and picks between them per scope. Two supported
wirings produce the same pair:

- **CloudNativePG (recommended):** point `Primary` at the `<cluster>-rw` Service and `PreferStandby` at
  `<cluster>-ro` — two DNS names, Kubernetes load-balances replicas on `-ro` and re-points both Services after
  a promotion, so the app's config is failover-stable. (`-ro` has no endpoints at zero replicas, e.g. a
  single-instance dev cluster; the standby view then falls back to `-rw`/`-r`, preserving the "degrades to
  primary" property.)
- **Provider-agnostic (bare PostgreSQL / non-k8s):** one `NpgsqlMultiHostDataSource` from a multi-host
  connection string, split via `WithTargetSession` into `Primary` and `PreferStandby` (the latter degrades to
  the primary when no standby exists).

`AddElarionReadReplicas<T>` owns the context's options wiring through the `(sp, options)` `AddDbContext`
overload; a scoped `ConnectionIntent` read from the dispatch rail picks the view when the scope's options are
built. Standby-intent connections set `default_transaction_read_only = on` at open, so a query handler that
writes fails identically whether the connection landed on a standby or fell back to the primary. Incompatible
with `AddDbContextPool` (pooling requires singleton options) — nothing in Elarion uses it; documented.

**3. Intent rule — fail-closed.** A scope gets standby intent iff: it is a **top-level dispatch of an
`IQuery`**, the handler carries no `[RequireImmediateConsistency]`, and no ambient unit of work exists.
Everything else is primary *by absence*: commands, domain-event consumers (they share the command's scope,
ADR-0001), integration/inbox consumers, `[Idempotent]` handlers, and every background scope (outbox delivery,
scheduler runs, settings writes) — none of these ever seed intent. Seeding rides the existing rail:
`DispatchScopeContext` + `IDispatchScopeInitializer` for the JSON-RPC/MCP dispatchers (which know
`HandlerMetadata` before resolving the pipeline), and the generated HTTP endpoint lambdas (which know the
request type statically) seed as their first statement. Nested typed-direct calls (injected `IHandler<,>`,
`IHandlerSender`) inherit the scope's intent, so a query invoked inside a command reads the primary and the
ADR-0015 single-connection transaction model is preserved. `HandlerInvoker.InvokeAsync` (fresh scope) is the
documented way a background job deliberately offloads a read to the replica.

**4. Framework subsystems pin primary.** The L2 cache keeps its own primary-pointing connection (`UNLOGGED`
tables do not exist on standbys); the settings `LISTEN` connection targets the primary; outbox, scheduler
claims, idempotency, and settings stores run in background/command scopes and are primary by rule 3. The one
deliberate staleness exposure: **resource-grant reads** (`IResourceAuthorizer` inside a query pipeline) ride
the query's scope, so a revocation becomes visible within replication lag — documented bounded staleness,
comparable to a token lifetime. `AddElarionResourceAuthorization` gains an opt-in pin that resolves the
grants store over a primary-bound context for hosts that reject that window.

**5. Read-your-writes via consistency tokens.** The differentiating half, phased but shaped now:

- **Capture.** Whoever commits a unit of work (the EF unit-of-work scope / the owning decorator) stamps a
  committed marker on the dispatch rail; response egress then captures `pg_current_wal_insert_lsn()` once on
  a primary connection. Insert-LSN is a safe over-approximation of the commit LSN (the replica waits
  marginally longer), which keeps `IUnitOfWork` untouched.
- **Carry.** The token rides transport headers — `Elarion-Consistency-Token` on response and request — never
  the JSON envelope: schema-invisible, uniform across REST/JSON-RPC/MCP (all HTTP-hosted), and batch-friendly
  (one header carrying the batch max). Encoded as fixed-width hex so lexicographic order equals LSN order.
- **Ratchet.** The generated TS client keeps a monotonic max (`token = max(token, response)`) and attaches it
  to every request automatically; the TanStack adapter therefore makes the standard mutate → invalidate →
  refetch cycle causally safe with zero app code. Third-party callers can propagate the header by hand.
- **Enforce.** When a standby-intent scope opens a connection and a token is present, a server-side wait blocks
  until the standby has replayed past the LSN, then the query proceeds; timed out (one `ReplayWaitTimeout`
  knob) → discard and reopen on the primary view. No token → serve eventual. The wait runs **as its own
  top-level statement at connection acquisition, before the query's transaction/snapshot** — not inside it —
  which is both correct (a held snapshot would block the very replay we wait for; PostgreSQL forbids the call
  under an active snapshot for exactly that self-deadlock reason) and the reason the gate is a distinct step,
  not a query wrapper. The primitive is **version-selected within the supported range**: PostgreSQL 17–18 use
  the `pg_wal_replay_wait(lsn, timeout)` procedure; PostgreSQL 19 uses the successor `WAIT FOR LSN` command
  (same author/semantics, promoted from procedure to command, with a `NO_THROW` status return that turns the
  timeout → primary-fallback branch into a plain result check instead of a caught exception). The floor is
  **PostgreSQL 17+** — below 17 is unsupported (no pre-17 `pg_last_wal_replay_lsn()` poll path is maintained).
- **Where the wait happens (adaptive).** A behind replica offers three places to absorb the lag, cheapest
  first: **(a) inline server wait** — `WAIT FOR LSN` blocks the connection up to a *short* budget
  (`InlineWaitBudget`, e.g. tens of ms); **(b) client retry** — if still behind, the server returns fast with a
  "not yet consistent, retry after ~N ms" hint (the observed shortfall) and the generated client re-issues with
  bounded backoff, so the wait costs a cheap round-trip instead of a held DB connection and *never* touches the
  primary; **(c) primary fallback** — only after the client's retry budget is spent (or `[RequireImmediateConsistency]`
  demanded it up front). "Depending on how long it takes" *is* this ladder: brief lag is swallowed inline, a
  little more hands the wait back to the client, and only persistent lag pays the primary. The generated client
  makes (b) viable the same way it makes the token ratchet viable — Elarion owns both ends. Default budgets are
  tuned for the sync-replication posture below (lag is normally sub-millisecond, so inline almost always wins).
- **Primary-outage posture (fail-open).** The fallback above assumes the primary is reachable — but during a
  primary outage that is exactly what's gone, and erroring the read would forfeit the read-availability this
  feature is partly *for*. So a standby read whose gate cannot be satisfied because the primary is
  unreachable **serves stale from the standby** rather than failing (an `OutageReadConsistency` posture,
  default fail-open). Read-your-writes cannot hold while the primary is down anyway — the writer's LSN either
  never committed or never shipped — so the honest degraded contract is "eventual reads keep working, causal
  reads relax until the primary returns." A caller that would rather 503 than read stale sets the posture to
  fail-closed (or carries `[RequireImmediateConsistency]`, which is primary-pinned and simply becomes
  unavailable during the outage — correct for those reads).
- **Weak tier.** The token is opaque per the strongest-impl rule: the PostgreSQL implementation carries an LSN
  and gates on it. A non-Postgres backend or the in-memory/test tier may instead carry a timestamp and
  implement the Rails/GitLab-style fixed time window, documenting the reduced guarantee — the weak tier exists
  for *other providers*, not for old PostgreSQL.

**6. Escape hatches.** Per-call override is a rail item set before dispatch (options-bag style), not another
attribute — with automatic tokens, caller-dependent consistency needs mostly evaporate.
`[RequireImmediateConsistency]` on a non-`IQuery` request is a warning (`ELREP001`): commands are always
primary, the attribute is a no-op there.

**7. Observability.** The routing decision surfaces as a trace tag (`db.elarion.route = primary|standby`) via
the `IHandlerContextEnricher` seam plus a replication-lag gauge and health-check contribution from the
package, so "the replica is 40 s behind" is an alarm, not a bug hunt.

**8. Phasing.** Phase 1: routing + attribute + hygiene list. Phase 2: server-side token capture and gating.
Phase 3: TS client ratchet + TanStack integration. Phases 2–3 are the payoff; fixing the header, token
encoding, and rail shapes in this ADR is what keeps them from becoming breaking changes later.

## Options considered

- **A second read context type (`AppReadDbContext`).** Explicit but viral — every handler constructor chooses,
  registrations and generator output double. Remains possible app-side with zero framework help; not the
  recommended path (one designated default).
- **A repository/`IReadStore` seam.** Contradicts the deliberate no-repository stance; routing is a connection
  concern, not a data-API concern.
- **Opt-in polarity (`[AllowReplicaRead]`).** Safe-looking but noisy on the ~90 % of queries that tolerate
  replicas, and forfeits the install-package-and-reads-scale story. Tokens make opt-out safe, so opt-out wins.
- **Time-window stickiness as *the* mechanism (Rails).** Guesses lag and needs per-user state; survives only
  as the weak-tier token implementation.
- **Synchronous replication *as the consistency mechanism*.** Synchronous-to-*all* `remote_apply` would make
  every replica readable-consistent, but taxes every write on the slowest standby and stalls writes when one is
  down — unacceptable. Quorum sync (the recommended posture above) is a *complement*, not a substitute: it buys
  durability and makes the gate cheap but, applying to only a quorum member, cannot guarantee the specific
  replica a read lands on — so tokens remain the per-read mechanism. Elarion never sets `synchronous_commit`
  itself; it's a deployment (CloudNativePG) setting.
- **Proxy-level routing (pgpool/pgcat/RDS Proxy).** An infrastructure dependency whose statement-level
  heuristics can split a transaction and which can never see handler intent or
  `[RequireImmediateConsistency]`. Compatible if deployed, but not the mechanism.

## Consequences

- No package installed → byte-identical behavior; the attribute is inert. With the package on a single-node
  topology, `PreferStandby` degrades to the primary — dev/test setups keep working unchanged.
- Read-your-writes holds automatically only for callers that round-trip `Elarion-Consistency-Token`; the
  generated TS client does, third-party callers get eventual reads on queries unless they propagate the
  header (documented). Cross-user lag is accepted by default; `[RequireImmediateConsistency]` is the
  linearizable-read escape per handler or per module.
- Cookie-based Identity hosts lose some offload: auth middleware may resolve the scoped context before intent
  seeding, and such requests fall back to the primary (safe degradation; JWT hosts are unaffected). Documented.
- ADR-0027 composes untouched: Tier-2 invariant checks run in the handler, inside the transaction, on the
  primary — a replica can never participate in a uniqueness or business-rule decision. `[Cacheable]` also
  composes: a cache hit never consults the token gate; caching already declares staleness tolerance.
- **The package requires PostgreSQL 17+** (for `pg_wal_replay_wait`) — an accepted, deliberate constraint, not
  a fallback matrix: PostgreSQL below 17 is unsupported rather than carried on a second, racier replay-poll
  path. PG17 shipped 2024-09. Teams on older PostgreSQL keep the single-Postgres default (this package simply
  isn't for them yet); non-Postgres providers use the weak-tier time window.
- Costs, only with the package active: one `SELECT pg_current_wal_insert_lsn()` per request that committed a
  unit of work; one bounded server-side replay wait (`pg_wal_replay_wait` / `WAIT FOR LSN`) per gated standby
  read.
- Testing: the intent rule is pure over rail + metadata (unit tests); the gate sits behind a fakeable seam;
  a Docker-gated Testcontainers primary+standby fixture covers replication end-to-end
  (`[Trait("Category", "Integration")]`, skipped without Docker).
- **Availability: the framework routes, the operator does HA.** The framework's own contribution is read
  scale-out plus continuous stale reads through a primary loss. Write HA — promoting a replica so writes resume
  — belongs to the deployment: in the reference stack CloudNativePG does it (lease-based election, quorum,
  `failoverDelay`), turning a primary loss into a seconds-long write pause rather than downtime; on bare
  PostgreSQL it's Patroni/repmgr/a managed service. Either way Elarion **consumes** the promotion (via the
  `-rw`/`-ro` Services or `TargetSessionAttributes`) and never performs one. So the combined system can be
  genuinely HA; the *package* is not what makes it so — don't credit the framework with HA it doesn't
  implement.
- Non-goals: a replica is not a read model (no projection/CQRS-view framework), no sharding or multi-primary,
  **no automatic failover/promotion in the framework** (delegated to the operator, e.g. CloudNativePG), no
  proxy requirement, no S3-style wire compatibility concerns.
- Open questions deferred to implementation: the default `InlineWaitBudget` / client-retry budget that split
  inline-wait vs client-retry vs primary-fallback (tuned for the quorum-`remote_apply` posture, but the numbers
  want real lag data); whether query responses also return an observed-LSN token (full monotonic reads rather
  than write-only ratcheting); the default for the grants pin (currently: documented staleness, pin opt-in);
  SSR token scoping in the TanStack adapter (per-request store vs module-global).

## References

Prior art for the consistency model:

- Rails — [Activating Automatic Role Switching][rails-switching] (Active Record Multiple Databases guide): the
  `DatabaseSelector::Resolver` 2-second time-window heuristic and the opt-out polarity this ADR adopts (and
  improves on with causal tokens). Basis for the weak-tier fallback and the replica-by-default decision.
- ProxySQL — [GTID consistent reads][proxysql-gtid]: the position-tracking, causal-routing model this ADR
  mirrors, with a MySQL GTID where Elarion uses a PostgreSQL LSN. Its binlog reader tracks per-server GTIDs
  and routes each causal read to a replica that has applied it — proxy-side, without an app-carried token.
- Vitess — [RFC: Read After Write][vitess-raw]: the same causal-read goal at the routing layer, and a source
  of the cross-user-lag caveat (its query consolidator can lose read-your-write between concurrent users —
  the anomaly this ADR classifies as inherently eventual).
- GitLab — [Database load balancing][gitlab-lb]: the closest PostgreSQL-LSN prior art. On a write it stores the
  primary's WAL position, keyed per user, and only routes that user's later reads to a secondary once the
  secondary's replay LSN has passed it (else sticks to the primary for ~30 s). ADR-0037 is the same causal
  idea with the pointer carried in a client-ratcheted token instead of server-side Redis, and the replay wait
  pushed into the database (`pg_wal_replay_wait`) instead of a client poll.
- Franck Pachot — [Read-your-writes on replicas: PostgreSQL WAIT FOR LSN][pachot-waitlsn]: the PG17
  `pg_wal_replay_wait` technique this ADR standardizes on, contrasted with MongoDB causal consistency.

Primitives the machinery is built on:
- PostgreSQL 17–18 — [`pg_wal_replay_wait`][pg-replay-wait] procedure, plus the WAL-position function
  [`pg_current_wal_insert_lsn`][pg-wal-functions]: the causal-read primitives the token gate uses today.
- PostgreSQL 19 — the [`WAIT FOR LSN`][pg19-waitfor] command ([commit `447aae13b`][pg19-commit], Korotkov,
  2025-11): the procedure promoted to a top-level statement with extra wait modes and a `NO_THROW` status
  return; the gate prefers it once the server is 19+.
- Npgsql — [`TargetSessionAttributes` / multi-host data sources][npgsql-multihost]: the
  `Primary` / `PreferStandby` routing views (the provider-agnostic wiring).

Reference deployment:
- [CloudNativePG][cnpg] — the recommended operator: [default Services][cnpg-svc] (`-rw`/`-ro`/`-r`) supply the
  two routing endpoints, and [automated failover][cnpg-failover] (lease-based election, quorum, `failoverDelay`)
  supplies the write-HA the framework does not.

[rails-switching]: https://guides.rubyonrails.org/active_record_multiple_databases.html#activating-automatic-role-switching
[proxysql-gtid]: https://proxysql.com/blog/proxysql-gtid-causal-reads/
[vitess-raw]: https://github.com/vitessio/vitess/issues/6843
[gitlab-lb]: https://docs.gitlab.com/development/database/load_balancing/
[pachot-waitlsn]: https://dev.to/franckpachot/read-your-writes-on-replicas-postgresql-wait-for-lsn-and-mongodb-causal-consistency-4he2
[pg-replay-wait]: https://www.postgresql.org/docs/current/functions-admin.html#FUNCTIONS-RECOVERY-CONTROL
[pg-wal-functions]: https://www.postgresql.org/docs/current/functions-admin.html#FUNCTIONS-ADMIN-BACKUP
[pg19-waitfor]: https://www.postgresql.org/docs/19/sql-wait-for.html
[pg19-commit]: https://git.postgresql.org/gitweb/?p=postgresql.git;a=commitdiff;h=447aae13b
[cnpg]: https://cloudnative-pg.io
[cnpg-svc]: https://cloudnative-pg.io/docs/devel/service_management/
[cnpg-failover]: https://cloudnative-pg.io/docs/devel/failover/
[npgsql-multihost]: https://www.npgsql.org/doc/failover-and-load-balancing.html

# ADR-0075: Ambient tenant scoping — a model-level filter and a write-time stamp, not a per-call-site rule

- Status: Accepted
- Date: 2026-09-20
- Related: [ADR-0013](0013-resource-and-data-level-authorization.md) (the opt-in point check and list filter
  this sits beside), [ADR-0009](0009-authorization-building-blocks.md) (the handler-pipeline gate),
  [ADR-0007](0007-data-is-platform-module-as-plugin.md) (handlers use the `DbContext` directly),
  [ADR-0017](0017-seam-in-core-implementation-in-sibling.md) (seam/implementation split), the
  [multi-tenancy](../capabilities/multi-tenancy.mdx) capability page, and the
  [archive/restore](../capabilities/archive-restore.mdx) recipe, whose argument against global query filters
  this decision is the stated exception to.

## Context

[ADR-0013](0013-resource-and-data-level-authorization.md) splits data-level security into a point check
(`[RequireResource]`) and a list filter (`[ResourceFilter]` + `WhereAuthorized`), both **opt-in per call
site**. That shape is right for *sharing*: a row some users may see is a per-feature decision, and writing the
decision where the query is written keeps it visible.

It leaves *tenancy* unserved. Under tenancy every row of every table belongs to exactly one tenant, and
cross-tenant visibility is never legitimate. Four things follow that the opt-in shape cannot give:

1. **A missed read filter is a data leak, not a widened list.** `WhereAuthorized` has to be written at every
   read site, and the concept doc says so outright — "omitting it returns every row". For sharing that
   over-shares one feature's list; for tenancy it is a silent cross-tenant read, one forgotten `.Where` away,
   with nothing to catch it at build time.
2. **There is no write leg at all.** ADR-0013 makes write enforcement handler-owned *deliberately, over an
   automatic interceptor*. Under tenancy that means every `db.X.Add(...)` must remember to stamp the tenant.
   Forgetting writes the row into the wrong tenant — or into none, where it then fails the read filter
   forever and is invisible to the person who created it.
3. **"Spans every tenant" cannot be written down.** A job that legitimately crosses tenants opts out by *not
   calling* `WhereAuthorized`, which is textually indistinguishable from having forgotten it.
4. **The attribute was in the wrong package.** `[ResourceFilter]` lived in `Elarion.Paging` — "keyset and
   offset pagination primitives" — while `IQueryAuthorizer<T>` was already in `Elarion.Abstractions`. An
   application needing data-level authorization but no pagination took the pagination package for an
   attribute.

Downstream applications were therefore each rebuilding the same two blocks: a `HasQueryFilter` loop in
`OnModelCreating` and a `SaveChanges` override that stamps the tenant id. That is the framework's job.

## Decision

Ship **ambient tenant scoping** as an opt-in capability with two legs driven from one marker, and move the
resource-filter contracts next to the interface they implement.

### The marker is the declaration

An entity implements `ITenantScoped<TTenantId>` (`Guid`, `string`, `int`, or `long` — the key types
`[ResourceFilter]`'s tenant rule already accepts). Nothing else is declared per entity, per query, or per
write. `Elarion.Abstractions` owns the contract; there is no attribute, because tenancy is a property of the
row's identity rather than a policy attached to it.

### Leg A — a model-level named query filter

`modelBuilder.ApplyElarionTenantScoping(this)` — emitted by `[GenerateElarionTenantScoping]` into the existing
per-feature model-configuration seam — walks the built model and attaches
`e => IsSystemScope(ctx) || e.TenantId == As{Key}(CurrentTenantId(ctx))` to every tenant-scoped root entity.

This is the repository's **first and only** global query filter, and it is the exception
[archive-restore](../capabilities/archive-restore.mdx) already named: *"A global filter earns its keep when
the predicate is a security boundary that must hold even when a developer forgets — multi-tenancy is the
classic case."* The objections raised there still apply and are accepted: a query stops saying exactly what
SQL runs, and raw SQL and bulk COPY do not see the filter. What changes the balance is that the cost of
forgetting is a cross-tenant read rather than a wrong screen.

Three details carry weight:

- **Nullable comparison, so it fails closed.** The conversion returns `Guid?`/`int?`/`long?`/`string?`. An
  unresolved tenant compares against SQL `NULL` and matches nothing. Comparing against `default(TKey)` instead
  would quietly expose every row whose tenant column happens to be `Guid.Empty` or `0`.
- **Named, so it composes.** EF Core 10 named filters mean an application's own filters on the same entity
  survive, and `IgnoreQueryFilters([ElarionTenantScoping.QueryFilterKey])` drops this one without dropping
  theirs.
- **Discovery at model-build time, not in the generator.** The generator emits one call; which entities are
  tenant-scoped is decided from the finished model. Deciding it in the generator would mean re-deriving EF's
  own entity discovery and silently missing navigation-discovered children — exactly where the bug would hurt.

### Leg B — a `SaveChanges` interceptor that stamps and guards

Insert stamps the tenant; a hand-set tenant is *verified* rather than trusted; update and delete refuse a row
whose original tenant is not the one in scope; and changing the tenant of an existing row is refused outright.
Violations throw `TenantScopeViolationException` — a fault, not an `AppError`, because it means the isolation
was bypassed and no caller should handle it.

This is a deliberate departure from ADR-0013's "handler-owned writes, not an interceptor". That decision was
made for *sharing*, where the write rule varies per operation and belongs where the operation is written. A
tenant stamp is the same rule for every entity and every write, so the one place every write passes through is
where it belongs. The two coexist: `[ResourceFilter]` still expresses sharing within a tenant.

The model pass records the tenant property as an entity-type annotation, so the interceptor costs a metadata
lookup per changed entry and no reflection.

### A declared system scope

`using var _ = tenant.SystemScope();` satisfies the read filter unconditionally and turns the write leg off
for the block. It exists so that "spans every tenant" is greppable, and so a reviewer can tell it apart from
a missing call. `tenant.Scope(tenantId)` enters one tenant explicitly — the entry point for asynchronous
resolution, and for a worker iterating tenants.

### Scoped state, reached through the context's options

The model is a process-wide cached artifact, so a filter baked into it cannot close over a scoped service —
it would pin the first scope's tenant for the life of the process. What a filter *can* reference is the
executing `DbContext`, which EF Core substitutes per query. The scoped `ITenantContext` therefore rides on the
context's options, attached per scope by an `IDbContextOptionsConfiguration<TContext>` — the same seam the
audit interceptor already uses.

The consequence is that tenant scoping requires `AddDbContext`, not `AddDbContextPool`: a pooled context
builds its options once, which would serve every scope the first one's tenant. That is a silent cross-tenant
leak, so it is documented as unsupported rather than merely discouraged.

State lives on the scoped instance, not in an `AsyncLocal`. A scope is a unit of work, which is the right
lifetime for "which tenant is this work for", and it matches how every other per-call value travels
(`ICurrentUser`, the dispatch scope).

### Resolution is a seam, and synchronous

`ITenantResolver` produces the id; the shipped `ClaimsTenantResolver` reads a configurable claim from
`ICurrentUser` and is transport-neutral. It is **synchronous** because a query filter is evaluated during
query translation and cannot await. An application whose tenancy comes from a membership table resolves where
awaiting is legal and calls `Scope(tenantId)`; pinning the contract to a claim would have made that
application synthesize a fake claim, which is what the reporting application had to do.

Two tenant claims resolve to `null` rather than the first: picking one would make an isolation boundary depend
on claim ordering.

### `[ResourceFilter]` moves to `Elarion.Abstractions`

`ResourceFilterAttribute<T>`, `WhereAuthorized`, and `IQueryAuthorizer<T>.Matches` move from `Elarion.Paging`
to `Elarion.Abstractions.Authorization`, beside `IQueryAuthorizer<T>`. None of them touch EF Core or
pagination. Breaking, and pre-1.0: the namespace is part of type identity, so no type-forward helps.

## Alternatives considered

- **Leave the opt-in model and add an analyzer** that flags a query rooted at an entity with a tenant rule but
  no `WhereAuthorized` in the chain. Rejected: catching it needs dataflow across arbitrary LINQ chains, locals,
  and method boundaries, so the honest version either misses most cases or false-positives on legitimate
  system queries — and once leg A exists there is nothing left for it to catch.
- **`IGlobalAuthorizationRule`** (ADR-0074's sibling, shipped for the handler tier). It is the right shape for
  a *decision* like "is this tenant suspended", but it cannot scope rows, so the data-level half would stay
  per-call-site.
- **PostgreSQL row-level security** as a backstop. Out of scope per ADR-0013 — and the motivating application
  runs on SQLite, so a Postgres-only guarantee would not be the framework's guarantee.
- **An `AsyncLocal` ambient scope**, which would avoid the options plumbing and work with pooled contexts.
  Rejected for consistency: the framework carries per-call state on the scope, and a process-wide mutable
  static is harder to reason about at exactly the moment correctness matters most.
- **Stamping in a `DbContext.SaveChanges` override**, as applications write by hand today. Rejected: it
  competes with the application's own override, and an interceptor composes with the audit and event-dispatch
  interceptors already attached the same way.

## Consequences

**Positive**

- Per-tenant isolation is a property of the scope. A query that says nothing about tenancy is still filtered,
  and an insert that says nothing about tenancy is still stamped.
- The two failure modes that the opt-in model leaves open — a forgotten filter and a forgotten stamp — become
  structurally impossible on the EF path.
- "Spans every tenant" is a declaration in the source rather than the absence of a call.
- Data-level authorization no longer pulls in the pagination package.

**Negative / accepted**

- A tenant-scoped query no longer states its full predicate at the call site. This is the cost
  [archive-restore](../capabilities/archive-restore.mdx) argues against, accepted here alone.
- Raw SQL, `FromSql`, and bulk COPY bypass both legs, as they bypass `[ResourceFilter]` today. The AOT SQL
  tier is unserved and must scope its own statements.
- `ExecuteUpdate`/`ExecuteDelete` run against the filtered query (so they cannot reach another tenant) but are
  not stamped or guarded, because they do not go through `SaveChanges`.
- `AddDbContextPool` is unsupported with tenant scoping.
- The filter's `IsSystemScope ||` disjunction is a parameter in the emitted SQL, which can discourage index
  use on a generic plan. The alternative — requiring `IgnoreQueryFilters` per query for system work — trades
  that for the forgettable opt-out this decision exists to remove.

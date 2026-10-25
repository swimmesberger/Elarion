# ADR-0090: References between records stay in the database — entity lifecycle events are removed

- Status: Accepted
- Date: 2026-10-25
- Supersedes: [ADR-0088](0088-entity-lifecycle-events-for-soft-references.md) (deletions of marked entities as domain
  events, published with their database cascades).
- Related: [ADR-0007](0007-data-is-platform-module-as-plugin.md) (one shared schema per context),
  [ADR-0008](0008-bounded-contexts-and-the-graduation-path.md) (bounded contexts),
  [ADR-0087](0087-transaction-scoped-locks-and-one-advisory-key-scheme.md) (row locks).

## Context

ADR-0088 shipped in 0.3.0. `[PublishLifecycleEvents]` marked an entity, and `AddElarionEntityLifecycleEvents<T>()`
attached an EF Core interceptor. On every save that deleted marked rows, tracked or by a database cascade, the
interceptor published `EntitiesDeleted<T>` inline. Its purpose was cleanup of **soft references**: notes and files
that point at records of many kinds through an `(OwnerKind, OwnerId)` pair, with no foreign key.

Reviewing that purpose showed the pair to be the **polymorphic association**, a documented relational anti-pattern:
- Karwin, *SQL Antipatterns*, chapter "Polymorphic Associations";
- GitLab's database guide: "always use separate tables instead of polymorphic associations";
- comparisons of the alternatives (Hashrocket), which find its only advantage is ORM convenience.

Rails (`dependent: :destroy`) and Django (`GenericRelation`) ship the same application-level cascade. Those are
general-purpose ORMs for which polymorphic association is a modeling feature. EF Core deliberately has none.

Each reason to keep the pair was tested against Elarion's own design:

- **Module independence.** ADR-0007 gives all modules of a context one shared schema. Module boundaries are enforced
  in code, so a foreign key to another module's table breaks none.
- **Open-ended owner kinds.** A new owner kind always arrives with a migration. A shared identity table also covers
  any number of kinds without touching the attached data's table.
- **Owners in another context or database.** No foreign key is possible there, but an inline domain event cannot
  cross a context either. That case needs an integration event.
- **Side effects outside the database (blob content).** These belong in the same transaction (the PostgreSQL blob
  store shares it) or after commit. An inline event before the `DELETE` is the wrong place for them.
- **Convenience.** This one is real, and it is the only one.

Against that convenience, the feature gave weaker guarantees than a foreign key:
- inserts stayed unchecked;
- cleanup raced concurrent inserts unless the application added row locks;
- raw SQL bypassed it.

It also cost a large surface built on EF Core internals:
- `ExecuteDelete` matched by name in an `IQueryExpressionInterceptor`;
- a static interceptor that reached the request scope through `CoreOptionsExtension.ApplicationServiceProvider`;
- per-context state shared across nested saves;
- cascade dependents queried before the `DELETE`;
- interceptor order detected by type name;
- reflection to bind publishers per entity type.

Its review found a blocker (unbounded recursion when a consumer saved) and restrictions it had to fail closed on:
inheritance hierarchies, alternate keys, and pooled or factory contexts. Every EF Core upgrade puts that surface at
risk.

## Decision

- Remove `[PublishLifecycleEvents]`, `EntitiesDeleted<TEntity>` and `AddElarionEntityLifecycleEvents<TDbContext>()`
  before 1.0. Elarion is pre-1.0, so breaking changes are allowed, and no consumer had adopted the feature.
- References between records stay in the database. The docs page
  [Polymorphic references](../concepts/polymorphic-references.mdx) is the guidance:

  | Relationship | Pattern |
  | --- | --- |
  | one owner kind | a foreign key |
  | a few known kinds | an exclusive arc: one nullable foreign key per kind plus `CHECK (num_nonnulls(...) = 1)` |
  | kinds with different shapes | a table per kind, unified with TPC |
  | many uniform kinds | a shared identity table |
  | an existing `(kind, id)` table | per-owner delete triggers while it is migrated |
  | another context | an integration event |

- Elarion does not ship an application-level cascade. If demand returns, the candidate is a model helper that
  configures an exclusive arc (the foreign keys and the CHECK) through public EF Core APIs. It makes the strong
  pattern as cheap as the weak one, without owning interception.

## Consequences

- Applications with `(kind, id)` attachments migrate toward database integrity. They get checks on insert and delete,
  no races, and safety from raw SQL, with no interceptor ordering or context-lifetime rules.
- The guidance replaces code. The framework's EF surface shrinks to stable model APIs, with less to break on an EF
  Core upgrade.
- The EF Core page keeps its interceptor-order table for tenant scoping and auditing.
- A closed generic domain event (`Archived<TEntity>`) is still a normal application event. The generator test that
  used `EntitiesDeleted<T>` now uses such an application event.

## Rejected alternatives

- **Keep it as a documented convenience.** The framework would maintain an anti-pattern with weaker guarantees than
  one foreign key, on internals that change with EF Core. The docs would then recommend against the framework's own
  feature.
- **Keep it, marked experimental.** That postpones the decision while a consumer adopts it, and the maintenance cost
  is the same.
- **Ship the exclusive-arc helper now.** No consumer needs more than the documented configuration yet. It can follow
  on demand.

# ADR-0013: Resource-based and data-level authorization

- Status: Accepted
- Date: 2026-06-29
- Related: [ADR-0009](0009-authorization-building-blocks.md) (the handler-pipeline authorization this extends),
  [ADR-0012](0012-dynamic-variable-references.md) (how the per-resource id is referenced — typed path, not a
  string expression), [ADR-0003](0003-decorator-attachment-predicates.md) (logic graduates to generator-wired
  code), [ADR-0006](0006-incremental-source-generator-conventions.md) (the generators follow these conventions),
  [ADR-0007](0007-data-is-platform-module-as-plugin.md) (handlers use the `DbContext` directly), and the
  [resource authorization](../concepts/resource-authorization.mdx) concept doc.

## Context

[ADR-0009](0009-authorization-building-blocks.md) gives Elarion a declarative, transport-neutral
authorization gate, but it decides on the **whole operation** against `ICurrentUser` — it has no notion of
*which* resource is being touched. A recurring downstream need is **data-level security**: read/write access
to *specific* resources, where access can be granted to a **user or a role** (ReBAC-style sharing — a single
contact shared with the "Hausmeister" role is visible to every user in that role).

The decisive insight (industry-wide: Google Zanzibar/OpenFGA, AuthZed/SpiceDB, Oso) is that this is **two
problems, not one**:

1. **Point check** — "may user U do action A on resource R?" A per-object boolean (OWASP's #1 API risk, BOLA).
2. **List authorization** — "which resources may U read?" A filtering/enumeration problem.

They need different machinery. Solving listing by running the point check over a result set (Spring's
`@PostFilter` shape) is the documented anti-pattern: it breaks pagination, corrupts total counts, leaks
cardinality, and scales O(rows). Efficient list authorization means **pushing the rule into the SQL
`WHERE`/`EXISTS`** so the database filters during query planning.

## Decision

Two complementary, opt-in legs, driven from one declarative source, with a DB-native sharing backend. The
database is the only external system — no relationship/policy-engine dependency (OpenFGA/SpiceDB/OPA/Cedar);
the seams are shaped so such an engine *could* back them later, but none ships.

### Leg A — the per-resource point check

A handler declares `[RequireResource(typeof(Contact), Operation = "read", Id = nameof(GetContactQuery.Id))]`.
Per [ADR-0012](0012-dynamic-variable-references.md), the resource id is a **compile-checked path**, never a
`#id` string: `HandlerRegistrationGenerator` validates the path against the request type and emits a
zero-reflection typed accessor (`r => r.Id`) as a `ResourceRequirementBinding<TRequest>`, supplied to the
existing `AuthorizationDecorator` the way `HandlerMetadata` is. An unresolvable path is `ELAUTH002`. The
decorator resolves the id per call, and `ClaimsAuthorizer` calls the `IResourceAuthorizer` seam (mapping deny →
`AppError.Forbidden`, unauthenticated → `AppError.Unauthorized`, reusing the `ELAUTH001` `IResultFailureFactory`
guard). The shipped default authorizes from the grants table (a share with the caller's user or roles); when no
implementation is registered the framework **fails closed** (a logged 403). Owner-based point checks are the
handler's concern via the **escape hatch** — inject `IResourceAuthorizer`, or load the entity and call
`IQueryAuthorizer<T>.Matches`, before a write — which is also the deliberate alternative to a framework-owned
write-side `SaveChangesInterceptor`.

### Leg B — the list/filter predicate (efficient DB-level filtering)

A `[ResourceFilter<TEntity>]` partial class is completed by the generator into an `IQueryAuthorizer<TEntity>`
whose `GetFilter` returns an `Expression<Func<TEntity,bool>>` composed as `AND(scope rules) AND OR(grant
rules)`. A handler calls `source.WhereAuthorized(spec, currentUser)` **before** `ToKeysetPageAsync`/
`ToOffsetPageAsync`, so the predicate is folded into the single SQL statement — pagination and counts stay
correct and the database never returns unauthorized rows. The conventional rules are `OwnerProperty` (a grant,
column equality), `TenantProperty` (a scope), and `Shared` (a grant: a correlated `EXISTS` over the grants
table for the caller's user id **or any of their roles** — the bounded principal set, the opposite of a ReBAC
`ListObjects` that materializes the unbounded resource set). Anything richer is a hand-written
`IQueryAuthorizer<T>` — logic lives in generator-wired code, never a string (ADR-0003/ADR-0012).

### Grants — DB-native sharing, auth-provider-neutral

`Elarion.Authorization.EntityFrameworkCore` ships a generic `ResourceGrant` table keyed by `(resourceType,
resourceId, principalKind, principalId, operation)`, an `IResourceGrantStore`, and the grants-backed
`IResourceAuthorizer`. It is **deliberately separate from `Elarion.AspNetCore.Identity`**: data-level authz
must stay ASP.NET-free (ADR-0009) and work for Entra ID / OIDC / JWT, and grants key off principal *strings*
from `ICurrentUser` (user id + role names), never the Identity tables. It composes with Identity the same way
Identity composes onto a plain context (`ApplyElarionResourceGrants` beside `ApplyElarionIdentity`).
`ResourceOperation` and `ResourcePrincipal` are **open values** (a string `Name`/`Kind` + well-known statics),
mirroring `SettingsScope`, so new operations or principal kinds (group, tenant) add no contract change.

### Composition consistency

The feature composes like every other Elarion table feature: `AddElarionResourceAuthorization<TDbContext>`
(naming parity with `AddElarionIdentity`/`AddElarionOutbox`/`AddElarionSettings`),
`ApplyElarionResourceGrants(ModelBuilder)` for hand-written mapping, and the attribute-driven
`[GenerateElarionResourceGrants]` (parity with `[GenerateElarionIdentity]`, `ELRG001`) that emits the
`DbSet<ResourceGrantEntity>` and applies the model config. To let multiple optional table features compose on
one `[GenerateDbSets]` context, the EF `DbContextGenerator`'s model-configuration seam is generalized from a
single `OnEntitiesConfigured` to **per-feature hooks** discovered from `[GenerateElarion*]` attributes, so
Identity and grants coexist without colliding (Identity unchanged).

## Consequences

**Positive**

- Data-level filtering is **pushed into SQL** with correct pagination/counts; the in-memory `@PostFilter`
  shape is structurally impossible (there is no ID-list overload, and `WhereAuthorized` runs before paging).
- Role sharing is an **indexed `EXISTS` over the caller's bounded principals** — it scales where ReBAC
  `ListObjects → IN(...)` does not.
- One declarative `[ResourceFilter]` source feeds the list filter, the point check, and the in-memory `Matches`
  escape hatch, so they cannot drift for the conventional rules; a tested consistency helper covers bespoke ones.
- **AOT/trim-clean**: predicates and the id selector are static expression trees / typed member access, no
  reflection on the hot path; the generators follow ADR-0006.
- No external authorization engine and no new dependency; the database is the only external system.

**Negative / accepted**

- The shipped point-check default authorizes from **grants only**; owner-based point checks are the handler's
  job (escape hatch) or are modeled as a user grant. A per-entity point check that reuses `IQueryAuthorizer<T>`
  is a possible future enhancement.
- `[ResourceFilter]` predicates are a **read** filter; write enforcement is handler-owned via the escape hatch
  (deliberately, over an automatic interceptor). `FromSqlRaw`/`IgnoreQueryFilters` bypass them — accepted, with
  no Postgres RLS backstop in scope (we trust application code).
- Role hierarchies are out of scope (flat roles via `ICurrentUser.Roles`).
- Generated filter specs are auto-registered, module-feature-gated, by the host bootstrapper via the assembly
  manifest (see [ADR-0014](0014-cross-assembly-generator-composition.md), the cross-assembly generator-composition
  pattern this feature first drove); a per-spec `Register` helper remains for manual/standalone wiring.

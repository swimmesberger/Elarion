# ADR-0089: Resource authorization composes — hidden-as-missing, owner-derived filters, combinators

- Status: Proposed
- Date: 2026-10-25
- Related: [ADR-0013](0013-resource-and-data-level-authorization.md) (the two legs: list filter and point check).

## Context

A consuming application evaluated `[ResourceFilter]`/`[RequireResource]` for notes, documents and mentions and stayed
on hand-written row visibility, for concrete reasons: every consumer filters a **referencing** row (a note's owner id)
while `WhereAuthorized` filters the entity itself; the generated filter cannot express "holds `X.manage`, sees all"
or sharing through an application-owned membership table; `[RequireResource]` answers `403` where hidden records must
answer `404` like missing ones; and authorizers are scoped services that are hard to combine. Mentions also need the
inverse question — who may see record X.

## Decision

- `[RequireResource(DenyAs = ResourceDenial.NotFound)]` answers a denial as `AppError.NotFound` with
  `AuthorizationOptions.ResourceNotFoundMessage`, carried on the generated binding and `ResourceRequirement`; the
  generator adds `not_found` to the operation's error contract.
- `[ResourceFilter(BypassPermission = "…", PermissionClaimType = "permission")]` lifts the grant rules (owner,
  sharing) for holders of that permission claim. The scope rules are resolved first and stay: a tenant-scoped
  administrator sees every row of their own tenant, and a caller without a tenant claim still sees nothing. Only a
  filter without scope rules becomes unrestricted. A bypass that crossed tenants would turn a per-tenant role into a
  cross-tenant leak.
- `WhereAuthorizedVia(ownerKey, owners, ownerId, authorizer, user)` filters referencing rows by their owner's
  visibility with an `IN (subquery)` in the same statement; it is built from a compiler-generated template, so it stays
  trimming- and AOT-safe in `Elarion.Abstractions`.
- `QueryAuthorizers.AnyOf`/`AllOf`/`From` compose authorizers into one predicate (a `null` filter — unrestricted — wins
  an OR and is neutral in an AND), so an application rule is added to the generated filter instead of replacing it.
  They live in `Elarion.Abstractions` next to `IQueryAuthorizer<T>`: they only rewrite expression trees, need no
  runtime service, and a contract-only assembly can compose its own filters. An empty `AllOf` is refused because an
  AND of nothing restricts nothing.
- The inverse query is **not** a framework seam yet: Elarion owns only the grants (`IResourceGrantStore.GetGrantsAsync`
  already lists them); owners, tenant membership, permission holders and membership tables are application data. It is
  documented as a composition of the same rules.

## Consequences

- Applications move their hand-written visibility onto the declared filter plus a composed rule, and filter
  attachments, notes and mentions by their owner without copying the owner's predicate.
- A `NotFound` denial is only indistinguishable from a missing record if the handler's own not-found answer uses the
  same message; the docs say so.
- The default `[RequireResource]` point check still reads only the grants table, so it does not see the owner rule or
  the bypass the list filter applies. Until a filter-backed `IResourceAuthorizer` exists, the docs direct handlers to
  check a single record by the same filter (`WhereAuthorized(…).AnyAsync(…)`).
- The inverse question stays application code until a second consumer and a user-directory seam (a permission-holder
  query next to the role-claim storage) make a framework shape clear.

## Rejected alternatives

- **Membership-table sharing as attribute properties.** A two-hop join (shares → group members) generalizes poorly
  into attribute strings; composition keeps it ordinary, typed C#.
- **A generated `IResourcePrincipals<T>` now.** It would need the application's user directory to expand roles,
  tenants and permission holders into users, and would return a partial answer that looks complete.

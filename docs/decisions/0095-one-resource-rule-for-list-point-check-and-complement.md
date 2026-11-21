---
type: Architecture Decision Record
title: "ADR-0095: One resource rule for the list, the point check and the complement — and what the inverse needs"
description: "One resource rule for the list, the point check and the complement — and what the inverse needs."
status: draft
---
# ADR-0095: One resource rule for the list, the point check and the complement — and what the inverse needs

- Status: Proposed
- Date: 2026-11-21
- Related: [ADR-0013](0013-resource-and-data-level-authorization.md) (the two legs),
  [ADR-0089](0089-resource-authorization-composes.md) (composition, hidden-as-missing; this ADR closes the gaps it
  listed as consequences), [ADR-0075](0075-ambient-tenant-scoping.md) (tenancy is a separate, unconditional
  mechanism), [ADR-0012](0012-dynamic-variable-references.md) (attributes reference, never compute).

## Context

A consuming application moved a visibility rule onto ADR-0089's composition and then changed it ("records are public
unless restricted, to everyone who may read them"). The forward filter held: the rule changed in one place and about
25 `WhereAuthorized` call sites needed no change. Around it, the application still carried hand-written code that
only tests kept in agreement with the rule:

1. **Registration.** The generated `[ResourceFilter]` registered itself as `IQueryAuthorizer<T>` *after* every module's
   `ConfigureServices`, so a composed rule had to be registered again in the host after `AddElarion`. Without that
   line an injected `IQueryAuthorizer<T>` was one building block of the rule. Two filters for one entity resolved to
   an arbitrary one, without a diagnostic.
2. **Point check.** `[RequireResource]` read only the grants table (ADR-0089, consequences), so the application never
   used it and wrote `WhereAuthorized(…).FirstOrDefaultAsync(c => c.Id == id)` in every point read and write.
3. **Complement.** "Rows hidden from the caller" was built by negating the filter's expression tree by hand, with a
   special case for the unrestricted (`null`) filter.
4. **Building blocks.** Every hand-written alternative repeated the fail-closed preamble
   (`user.IsAuthenticated && Guid.TryParse(user.UserId, out var id)`); "holders of permission P see the rows matching
   a predicate" and a role bypass had no declarative form. `QueryAuthorizers.From` hands the caller to a delegate
   unchecked, so a forgotten preamble serves unauthenticated callers.
5. **Operations.** `GetFilter` takes a `ResourceOperation`, but nothing helped a rule say "this alternative grants
   reading only", and a delegate that ignores the argument grants every operation.
6. **Inverse.** "Who may see record X" (mention candidates) was a hand-written mirror of the rule, alternative by
   alternative.

The audit of the same area found further weaknesses: `AnyOf(tenantScopedFilter, extraRule)` lets the extra rule (and
any unrestricted alternative) cross the scope; `Matches` silently mis-evaluates a rule that reads a navigation or
another table; the grants point check formatted a numeric id with the current culture.

## Decision

**Registration fills a gap, and a building block says so.**

- The host bootstrapper registers a generated filter with `TryAdd`. An `IQueryAuthorizer<T>` the application registers
  itself — in a module's `ConfigureServices`, or in the host before or after `AddElarion` — is the one that resolves.
- `[ResourceFilter<T>(Register = false)]` marks a filter as a building block: it is not published in the assembly
  manifest and not registered. It still generates the same class (`Specification`, or the scoped constructor).
- Two *registered* filters for one entity are reported (`ELMOD007`, warning) in the host compilation, which is the only
  place that sees every assembly's filters.

**The point check asks the entity's `IQueryAuthorizer<T>`.**

- `AddElarionResourceFilterCheck<TDbContext, TEntity, TKey>(e => e.Id)` (`Elarion.Authorization.EntityFrameworkCore`)
  registers a check for one resource type: authorized means the row with that key is among the rows the *registered*
  authorizer lets the caller access for the requirement's operation — one `EXISTS` over the predicate the list uses.
  It needs no grants table.
- A per-type check is a keyed `IResourceAuthorizer` whose service key is the resource `Type` (the shape `IHandlerGate`
  already uses). The shipped backends — the fail-closed default and the grants backend — route to it first
  (`ResourceTypeAuthorizer.Find`); every other type is answered as before. Routing lives in the backends rather than
  in `ClaimsAuthorizer`, so a handler that injects `IResourceAuthorizer` gets it too, and no registration order
  matters.
- A row that does not exist is denied, also for an unrestricted caller. That is what makes `DenyAs = NotFound`
  indistinguishable from a missing record, and it keeps "authorized" from being true of an id that names nothing.
- The context's query filters apply by default (a soft-deleted row is denied, ambient tenant scoping holds); `rows`
  shapes the set for an entity whose hidden rows must stay authorized.
- The registration is explicit and typed (`TEntity`, `TKey`, the key expression), so the check contains no reflection
  and no model lookup by name.

**Composition has fail-closed building blocks** (`QueryAuthorizers`, `Elarion.Abstractions`).

- Leaves: `Rows(predicate)`, `Unrestricted()`, `ForUser<TEntity, TKey>((row, userId) => …)`.
- Gates: `When(condition, then)`, `WhenPermission(permission, then, claimType)`, `WhenRole(role, then)`,
  `ForOperations(authorizer, operations…)`.
- Every one denies an unauthenticated caller before the rule is consulted; `ForUser` also denies a caller whose id does
  not parse as `TKey`. `From` stays the unchecked escape hatch and says so.
- `ForUser` takes the rule as a **relation between a row and a principal id** — one expression with two parameters —
  instead of a delegate that builds a predicate per caller. The id is substituted as a captured value, so it reaches
  the database as a parameter. The relation form is also what an inverse query can be derived from (below).
- `ForOperations` denies every operation it does not list, which is the fail-safe reading of an open
  `ResourceOperation`.
- `AnyOf` leaves out alternatives that deny the caller and `AllOf` short-circuits on one, so the SQL holds only the
  live rules.
- `[ResourceFilter(BypassRole = "…")]` is `BypassPermission` for hosts whose administrators are a role; the scope
  rules stay, as for the permission.
- `AuthorizationOptions.DefaultPermissionClaimType` names the one default that the attribute and `WhenPermission`
  cannot read from the options instance.

**The complement is a query operator.** `source.WhereNotAuthorized(authorizer, user, operation)` returns the rows the
caller may not access: nothing for an unrestricted caller, `source` unchanged for a caller denied everything,
`NOT (filter)` otherwise. It is exact wherever a predicate is two-valued — in memory and under EF Core's default C#
null semantics.

**A scope stays outside the alternatives.** `AnyOf` is an OR; it cannot keep a scope that one of its arguments carries.
The documented shape is `AllOf(scope, AnyOf(alternatives…))`, in which an unrestricted alternative is neutral. Tenancy
itself belongs to ambient tenant scoping (ADR-0075), which no composition can lift.

**The inverse is still not a seam; its shape is now known.** ADR-0089 rejected a generated `IResourcePrincipals<T>`
because it "would return a partial answer that looks complete". A shape that cannot do that:

- Every alternative is either *invertible* or not. A relation (`ForUser`) inverts mechanically: for a row set `R`, the
  principals are `candidates.Where(u => R.Any(row => relation(row, u)))`. A gate inverts through a **principal
  directory**: `WhenPermission(P, then)` is `directory.Holding(P) ∩ inverse(then)`, `WhenRole` likewise;
  `Rows(predicate)` is "every candidate, if the row matches"; the generated filter contributes its owner column, its
  user and role grants and its bypass.
- A composition is invertible only when **all** its parts are. `From(delegate)` and a hand-written
  `IQueryAuthorizer<T>` are opaque, so a rule containing one has no inverse and asking for it fails with the name of
  the opaque part — the answer is complete or refused, never partial.
- The directory is the missing seam: the stored mirror of how a principal gets its roles and claims
  (`IQueryable<(userId, role)>`, `IQueryable<(userId, claimType, value)>`, and the candidate set itself).
  `Elarion.EntityFrameworkCore.Identity` can supply a default over the role-claim tables.

It is not shipped because three questions are open, and each decides the contract:

1. **Who owns the candidate set?** "Every account" is rarely what a caller wants (mention candidates are active staff,
   not tenant-portal accounts). Is it a parameter of the query, or part of the directory?
2. **How is agreement with the live principal guaranteed?** A host may add claims per request (an instance owner who
   is given the administrator role on every request). The directory must mirror that, and only the application can.
   A conformance test helper may be the honest answer, not a framework guarantee.
3. **What is an unrestricted alternative's inverse under a tenant scope** — every candidate, or every candidate of the
   row's tenant? It follows from whether scopes become first-class in the composition.

Until a second consumer answers them, the application composes the inverse itself. `ForUser` is shipped in the
relation form now so that rules written today need no change when the inverse arrives.

## Consequences

- One `IQueryAuthorizer<T>` per entity is the whole rule, and the list (`WhereAuthorized`), referencing rows
  (`WhereAuthorizedVia`), the point check (`[RequireResource]`) and the complement (`WhereNotAuthorized`) all read it.
- The filter-backed check costs one `EXISTS` before the handler. A handler that loads the row through
  `WhereAuthorized(…).FirstOrDefaultAsync(…)` anyway does both in one statement; that form stays valid and documented.
- The filter check is registered per entity. A `[RequireResource]` on a type with a filter but no registered check
  still goes to the grants backend or the fail-closed default — it denies, it does not widen. Wiring the check from
  the generated filter was left out: the host bootstrapper does not know the `DbContext`, and the composed rule — the
  case that needs it most — is not a generated filter at all.
- `WhereAuthorized` still defaults to `Read`. A write handler that omits the operation asks the read rule; with a rule
  that is wider for reading this over-admits. `[RequireResource(Operation = "update")]` passes the operation without
  the handler repeating it; making the argument mandatory would break every existing call site.
- `Matches` stays, documented as valid for scalar-field rules only; it cannot detect a rule that needs the database.
- **Behavior change.** The generated registration no longer replaces an `IQueryAuthorizer<T>` registered before it. An
  application that registered its own authorizer before `AddElarion` and relied on the generated filter winning now
  gets its own registration.

## Rejected alternatives

- **A router as the default `IResourceAuthorizer`, with the backend as a second seam.** It needs a fallback service
  identity that the grants package registers, and whichever of the two packages registers first decides whether
  routing happens — the registration-order footgun this ADR removes elsewhere.
- **Resolving the entity's authorizer from the runtime `Type`** (`MakeGenericType`) so no per-entity registration is
  needed. It puts reflection on the authorization path of a package that otherwise has none.
- **Skipping the existence query for an unrestricted caller.** It saves a round trip for administrators and makes
  "authorized" true of an id that names no row, so a `[RequireResource]` on a parent id would let a child insert reach
  the database with a dangling reference.
- **Ignoring query filters in the point check by default.** Authorization is not existence, but denying is the
  fail-closed default and the exception (a restore command) is visible in the first test.
- **A complement authorizer (`QueryAuthorizers.Not`).** Its `null` would mean "every row is hidden", the opposite of
  what `null` means everywhere else; passed to `WhereAuthorized` by mistake it returns exactly the hidden rows.
- **A permission-conditional predicate as attribute properties.** An attribute cannot hold a predicate (ADR-0012), and
  a property-name pair ("visible when this boolean is false") generalizes no better than membership tables did in
  ADR-0089.
- **Making `WhereAuthorized`'s operation mandatory.** It is the right default-free API and a source break for every
  call site; pre-1.0 allows it, and it is worth doing together with a second breaking change in this area.
- **Shipping the inverse for invertible parts only.** That is the partial answer ADR-0089 rejected.

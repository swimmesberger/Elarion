---
type: Architecture Decision Record
title: "ADR-0095: One resource rule for the list, the point check, the complement and the inverse"
description: "One resource rule for the list, the point check, the complement and the inverse."
status: draft
---
# ADR-0095: One resource rule for the list, the point check, the complement and the inverse

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
  the database as a parameter. The relation is also what the inverse reads (below).
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

**The inverse reads the same rule, and answers completely or not at all.** ADR-0089 rejected a generated
`IResourcePrincipals<T>` because it "would return a partial answer that looks complete". The shape that cannot do that:

- A rule is a **relation** between a row and a principal id. `IPrincipalQueryAuthorizer<TEntity>.GetPrincipalRelation`
  returns `(row, principalId) => …`, true exactly when that principal may access the row. The forward filter is the
  relation with the principal bound; the inverse is the relation with the row set bound:
  `principals.WhereAuthorizedFor(rows, authorizer, directory)` is
  `principals.Where(p => rows.Any(row => relation(row, p)))`, one statement.
- Relations compose at the relation level — `AnyOf` is OR, `AllOf` is AND, over one pair of parameters — not by
  uniting or intersecting principal sets. Set algebra is only right for a single row (for several rows "may access one
  of them **and** is in scope for another" would pass an `AllOf`).
- **Complete or refused.** Every `QueryAuthorizers` building block and the generated `[ResourceFilter]` implement the
  interface. `From` and `When` hold an opaque delegate over the live principal and do not, nor does a hand-written
  `IQueryAuthorizer<T>` unless it implements it. A composition asks every part, so a rule with one opaque part throws
  `NotSupportedException` naming that part instead of answering for the rest. A generated filter with a
  `TenantProperty` also refuses: the forward filter *parses* the tenant claim, a stored claim cannot be compared with
  the column the same way in SQL, and a relation that dropped the scope would list principals of every tenant.
- Facts the forward direction reads from `ICurrentUser` — roles, permission claims — come from an
  **`IPrincipalDirectory<TKey>`**: two queries, `Roles` (principal, role) and `Claims` (principal, type, value;
  *effective* claims, the claims of a principal's roles included). They are relations rather than
  `Holding(permission)`-style lookups because a role *grant* correlates the role name with a grants row, which a
  lookup taking a constant cannot express.

The three questions that kept this from being a seam, and the answers taken. None is irreversible; each is the
simplest one that makes the contract coherent.

1. **Who owns the candidate set? The caller.** `WhereAuthorizedFor` filters a set of principal ids the caller passes
   and returns a subset of it. "Every account" is rarely what is wanted (mention candidates are the staff who can
   still sign in), eligibility is application data, and a parameter keeps the directory to the two facts a rule reads.
2. **How does the directory agree with the live principal? By contract, with a default that agrees with the stock
   host.** The directory must list exactly what `IsInRole`/`HasClaim` answer for a principal that signs in.
   `IdentityPrincipalDirectory` (`Elarion.EntityFrameworkCore.Identity`) reads `user_roles`, `user_claims` and
   `role_claims`, which is what Identity's default claims factory puts on a principal. A host that adds roles or
   claims while it builds the principal registers its own directory (the registration is a `TryAdd`), composing the
   stored queries with what it adds. The framework cannot verify this — only the application knows how its principals
   are built — so the contract asks for a test that builds a principal and compares.
3. **What is an unrestricted alternative under a scope? Every candidate, and the scope narrows it.** `Unrestricted` is
   the constant `true`; in `AllOf(scope, AnyOf(…))` the AND with the scope's relation applies as it does forward. The
   tenant scope of a *generated* filter is the one scope with no relation (above); tenancy belongs to ambient tenant
   scoping, which already narrows the candidates and the rows the caller passes.

## Consequences

- One `IQueryAuthorizer<T>` per entity is the whole rule, and the list (`WhereAuthorized`), referencing rows
  (`WhereAuthorizedVia`), the point check (`[RequireResource]`), the complement (`WhereNotAuthorized`) and the inverse
  (`WhereAuthorizedFor`) all read it.
- The inverse is as right as the directory. A directory that omits what the host adds to a principal under-reports
  (an administrator by a per-request role is not listed); one that lists stale facts over-reports. The forward filter
  stays the authority: a principal the inverse lists is still checked when it actually reads the row.
- A rule that uses `From`, `When` or a hand-written authorizer has no inverse until that part is restated with the
  building blocks or implements `IPrincipalQueryAuthorizer<T>`. That is a visible exception at the call, by design.
- `Elarion.EntityFrameworkCore.Identity` now references `Elarion.Abstractions` (contracts only; it stays web-free).
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

## Amendment (2026-11-23): a rule through an owner

An application's e-mail feature states visibility as "readable when it is assigned to a record the caller may read",
across 22 record kinds, one of which (contacts) has a composed rule of its own. `WhereAuthorizedVia` expresses that for
one query, but it is not a building block: it cannot be an alternative of the e-mail type's rule, and the inverse cannot
read it. The application wrote the inverse by hand and kept it in step with scenario tests — the mirror this ADR set out
to remove.

**`QueryAuthorizers.Via` lifts an owner type's rule to the rows that point at its owners.** Its relation is
`(row, principal) => owners(row).Any(o => ownerRelation(o, principal))`, its filter the same with the owner's filter in
place of the relation, so both directions read one rule and the owner's rule is used as it stands. Two forms:

- `Via(row => owners, ownerRule)`: the owners are an expression over the row — a collection navigation, a join through
  a link table. Context-free, so the rule can be a static field.
- `Via(ownerSet, (row, owner) => joins, ownerRule)`: the owners are a set the row is joined to — a `(kind, id)` pointer
  with no navigation. The set becomes part of every query the rule filters, so a rule over a `DbContext` set is composed
  per scope.

Decisions taken, each the simplest coherent one and revisable:

1. **A row needs an owner.** An unrestricted owner rule admits the rows that *have* an owner, not every row: the
   relation is an existence, and "assigned to a record the caller may read" is false of an unassigned row. This differs
   from `WhereAuthorizedVia`, which keeps every row for an unrestricted caller; an unassigned row that should be visible
   gets an alternative of its own.
2. **The owner is asked about the row's operation** unless `ownerOperation` names another one, so "whoever may read the
   record may change its notes" is one argument, and the default never widens a write to the owner's read rule.
3. **It denies an unauthenticated caller itself**, like every other building block, instead of relying on the owner's
   rule (which may be `From`).
4. **An opaque owner rule refuses the inverse**, through the same `RelationOf` every composition uses.

Rejected: **a `WhereAuthorizedVia` overload that returns an authorizer** (it would still need the caller's set and
user at build time, so it could not be a static or composed rule); **an owner-set provider delegate** resolved per
query (an `IQueryable` cannot be obtained inside an expression without a context, and a delegate is opaque to the
inverse); **a navigation-only form** (a `(kind, id)` pointer has no navigation, and it is the shape that needed this).

## Amendment (2026-11-23): who may access every row

The same application assigns a selection of e-mails to a colleague, who must be able to read every one of them.
`WhereAuthorizedFor` answers "may access at least one", so the application wrote "all of them" by hand.

**`principals.WhereAuthorizedForAll(rows, authorizer, directory, operation)`** returns the candidates related to
**every** row: `principal => rows.Any() && rows.All(row => relation(row, principal))`, one statement, from the same
relation. Decisions:

1. **An empty selection admits nobody.** "Every row of none" is vacuously everyone; the inverse of `WhereAuthorizedFor`
   returns nobody for no rows, and an assignment dialog offered every account for an empty selection would be the
   fail-open reading.
2. **A separate operator, not a quantifier argument.** `WhereAuthorizedFor` keeps its signature, and the name says at
   the call site which question is asked.
3. **"All" is computed at the relation level** (`NOT EXISTS` a row the principal is not related to), never by
   intersecting per-row answers in memory, so the answer stays one statement for any selection size. Like
   `WhereNotAuthorized`, it is exact under two-valued predicates; under `UseRelationalNulls` a relation that is SQL
   `NULL` for a row does not exclude the principal, which the forward filter still catches when the principal reads.

## Amendment (2026-11-23): a negated claim gate

The same application lets an administrator view the application as another user. One rule should then drop its
administrator alternative, but the gates could only add alternatives for holders of a role or claim.

**`QueryAuthorizers.UnlessClaim(claimType, then)`** (any value) and **`UnlessClaim(claimType, value, then)`** grant
`then` to an authenticated caller who does *not* hold the claim. Its relation is `!holders.Contains(principal) AND
then`, read from the principal directory like the positive gates. Decisions:

1. **A claim, not a role or a delegate.** A session marker is a claim the host adds while it builds the principal; a
   delegate gate (`When`) already exists and has no inverse.
2. **It still denies an unauthenticated caller**, like every gate, so negation never reaches an anonymous request.
3. **The inverse answers for ordinary sessions.** A session marker is no stored fact of a principal, so the directory
   lists it for nobody, and "who may access this row" is asked about principals, not sessions. A claim the directory
   does store (a suspension) is honoured in both directions.

The page documents the hazard: a negated gate grants when the claim is missing, so it may withdraw an alternative but
must not stand alone between a caller and the rows.

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
- **Answering the inverse for the invertible parts of a rule.** That is the partial answer ADR-0089 rejected.
- **A directory of lookups** (`InRole(role)`, `Holding(permission)` returning id sets). Simpler to implement, and it
  cannot express a role grant, where the role name comes from a grants row.
- **The directory owning the candidate set** (`Principals`). It would make every inverse answer "all accounts" by
  default, which is the wrong default for the consumers known so far.
- **Inverting a tenant-scoped generated filter by comparing the claim text with the column's text.** It agrees with
  the forward filter only while every stored tenant claim is formatted exactly as the database renders the column; a
  differently cased claim would silently drop principals.

---
type: Architecture Decision Record
title: "ADR-0102: A rule is one expression, composed into queries and compiled for memory"
description: "A Rule<TInput, TResult> holds one expression; Invoke marks a use inside another expression, Expand inlines it, Captured parameterizes a value, and aggregates used several times are joined once."
status: stable
---
# ADR-0102: A rule is one expression, composed into queries and compiled for memory

- Status: Accepted (implemented 2026-11-22)
- Date: 2026-11-21
- Related: [ADR-0095](0095-one-resource-rule-for-list-point-check-and-complement.md) (one resource rule for the list
  and the point check)

## Context

Rich domains define rules once and need them in two places: inside EF queries, translated to SQL, and in memory, on a
loaded entity. "Is this invoice settled", "the open amount", "which period a contract is in" are each one expression.
Using one inside another needs expression-tree surgery, so applications write it by hand:

- a visitor that substitutes a lambda's parameters to inline it into another expression, and a helper that turns a
  value into a captured closure field so EF sends it as a parameter instead of a constant (one application had three
  variants of this visitor before merging them);
- projections built with `Expression.New(typeof(Row).GetConstructor(…))`, because a composed predicate cannot be
  written as a normal lambda;
- a correlated subquery (an open amount: gross minus the sum of payments) inlined at every use, so one predicate that
  uses the open amount five times makes the database evaluate the subquery five times per row.

## Decision

- **`Rule<TInput, TResult>`** in `Elarion` core (`Elarion.Expressions`; System.Linq.Expressions only, no EF
  dependency) holds one `Expression<Func<TInput, TResult>>` and a delegate compiled on first use and cached
  (`rule.Compiled`): `rule.Invoke(entity)` runs in memory, `rule.Expression` goes into a query. `Rule.Create(…)` infers
  the types from the lambda. **`Rule<TInput1, TInput2, TResult>`** is the same over two inputs, for a value the rule
  cannot reach from the entity: a date, a threshold, a joined aggregate.
- **Composition is written as a call.** Inside another expression, `OpenAmount.Invoke(invoice) > 0` is a marker;
  `Expand()` on an expression, or on an `IQueryable` (`query.Expand()`, which expands every expression the query is
  built from), inlines the rule's body with the arguments substituted, recursively. Projections become normal lambdas:
  `invoice => new Row(invoice.Id, FirstFailed.Invoke(invoice))`. An `Invoke` extension on `Expression<Func<…>>` is a
  marker the same way, for an expression kept without a rule (outside a tree it compiles once per instance and runs).
- **Expansion reads the rule out of the tree without compiling.** It follows constants and field or property accesses
  (a static field, a captured local, a field of a captured object) with reflection over the members the tree names.
  Any other shape, such as a method call or a lambda parameter, throws a `NotSupportedException` naming it. A rule that
  invokes itself throws an `InvalidOperationException` instead of overflowing the stack.
- **In memory a rule compiles its own, unexpanded expression**: a nested `Invoke` calls the other rule's cached
  delegate, so in-memory use never depends on whether the tree could be expanded.
- **`Expr.Captured(value)`** wraps a value as a closure field, so the provider parameterizes it (one plan, no
  constant baked into the SQL). **`Expr.Inline(lambda, arguments)`** inlines a lambda into a tree assembled from
  `Expression` factories. **`Expr.AnyOf` / `Expr.AllOf`** join predicates over one row, rebound to one parameter,
  with OR / AND; no predicates give the identity of the operator (always false / always true), so no alternatives match
  no row and no conditions restrict nothing. **`Expr.Not`** negates.
- **An aggregate used several times is joined once.** The docs give the recipe: project the aggregate in a grouped
  subquery and left-join it once per row (`invoices.LeftJoin(paidByInvoice, …)`), then apply rules that take the
  joined value as their second input (`Rule<Invoice, decimal, bool>`), instead of a correlated subquery at every use.
  No SQL `LATERAL`/CTE construct is added: EF inlines projections, and only the join shape gives every provider one
  evaluation.
- **AOT**: compiling uses the expression interpreter where dynamic code is unavailable; composition is tree rewriting
  plus reading the values of fields and properties the tree already names, which needs neither dynamic code nor a
  trimming annotation.

### Changes from the proposal

- The proposal's rule over the joined shape was `Rule<(Invoice Invoice, decimal Paid), bool>`. C# expression trees
  cannot contain tuple literals, so `IsPartiallyPaid.Invoke((row.Invoice, row.Paid))` does not compile inside a query.
  A rule over two inputs says the same and composes, so the second arity was added and the tuple form dropped.
- `Expr.Inline`, `Expr.AnyOf`, `Expr.AllOf` and `Expr.Not` were added: they are the rest of what applications wrote by
  hand next to the substitution visitor (one application OR'ed one date window per bank account).

## Consequences

- One definition per rule, used in SQL and memory, with no hand-written visitor in applications.
- `Expand()` must run before EF sees the tree, after the last operator that invokes a rule. A forgotten call fails at
  translation with EF's "could not be translated" naming `Invoke` in a filter, an ordering or a join, and in the final
  projection when the rule is in a static field (EF refuses to capture the rule instance as a constant). For a rule in a
  captured local, EF evaluates the final projection on the client, on an entity without the navigations SQL would have
  joined, and the result is silently wrong; the docs point this out and recommend static fields.
- Performance of a rule-heavy predicate depends on the join recipe, not on the helper; the helper makes the recipe
  writable.
- In memory a rule sees only what is loaded: a rule over a navigation needs it included.

## Rejected alternatives

- **Depending on LINQKit.** It covers `Invoke`/`Expand`, but adds a third-party runtime dependency to core and an
  `AsExpandable()` query wrapper, and does not cover captured values or the aggregate recipe.
- **An EF-only helper in `Elarion.EntityFrameworkCore`.** The composition is LINQ, not EF; in-memory use must not need
  EF. EF-specific conveniences can wrap it there later.
- **Database views or computed columns for derived values.** Right for some values, but they move a domain rule into
  the schema and cannot take a parameter such as today's date.
- **A rule over a tuple for the joined shape**, as first proposed; see "Changes from the proposal".
- **Compiling the expanded expression for memory.** Expansion is stricter about where a rule may live than
  compilation is; compiling the rule's own expression keeps in-memory use working for every rule and still runs each
  nested rule's cached delegate.
- **Throwing on an empty `AnyOf`/`AllOf`.** A filter built from a list (the windows of the accounts a user may see)
  is often empty; the identity element gives the answer the filter means, where an exception would push a special
  case into every caller.

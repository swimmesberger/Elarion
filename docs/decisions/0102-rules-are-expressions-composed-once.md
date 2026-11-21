---
type: Architecture Decision Record
title: "ADR-0102: A rule is one expression, composed into queries and compiled for memory"
description: "A Rule<TInput, TResult> holds one expression; Invoke marks a use inside another expression, Expand inlines it, Captured parameterizes a value, and aggregates used several times are joined once."
status: draft
---
# ADR-0102: A rule is one expression, composed into queries and compiled for memory

- Status: Proposed
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

## Decision (proposed)

- **`Rule<TInput, TResult>`** in `Elarion` core (`Elarion.Expressions`; System.Linq.Expressions only, no EF
  dependency) holds one `Expression<Func<TInput, TResult>>` and a lazily compiled delegate: `rule.Invoke(entity)` runs
  in memory, `rule.Expression` goes into a query.
- **Composition is written as a call.** Inside another expression, `OpenAmount.Invoke(invoice) > 0` is a marker;
  `Expand()` on an expression, or on an `IQueryable` (`query.Expand()`, which expands every expression the query is
  built from), inlines the rule's body with the arguments substituted. Projections become normal lambdas:
  `invoice => new Row(invoice.Id, FirstFailed.Invoke(invoice))`.
- **`Expr.Captured(value)`** wraps a value as a closure field, so the provider parameterizes it (one plan, no
  constant baked into the SQL).
- **An aggregate used several times is joined once.** The docs give the recipe: project the aggregate in a grouped
  subquery and join it once per row (`from i in invoices join p in paidByInvoice on …`), then apply the rules to the
  joined value, instead of a correlated subquery at every use. A rule over the joined shape
  (`Rule<(Invoice Invoice, decimal Paid), bool>`) keeps one definition. No SQL `LATERAL`/CTE construct is added: EF
  inlines projections, and only the join shape gives every provider one evaluation.
- **AOT**: compiling uses the expression interpreter where dynamic code is unavailable; composition itself is
  reflection-free tree rewriting.

## Consequences

- One definition per rule, used in SQL and memory, with no hand-written visitor in applications.
- `Expand()` must run before EF sees the tree; a forgotten call fails at translation with EF's "could not be
  translated" naming `Invoke`, which the docs point out.
- Performance of a rule-heavy predicate depends on the join recipe, not on the helper; the helper makes the recipe
  writable.

## Rejected alternatives

- **Depending on LINQKit.** It covers `Invoke`/`Expand`, but adds a third-party runtime dependency to core and an
  `AsExpandable()` query wrapper, and does not cover captured values or the aggregate recipe.
- **An EF-only helper in `Elarion.EntityFrameworkCore`.** The composition is LINQ, not EF; in-memory use must not need
  EF. EF-specific conveniences can wrap it there later.
- **Database views or computed columns for derived values.** Right for some values, but they move a domain rule into
  the schema and cannot take a parameter such as today's date.

---
type: Architecture Decision Record
title: "ADR-0107: A shared entity may name the one module that writes it"
description: "Proposal: a shared-kernel entity names its owning module, an analyzer next to ELMOD002 reports writes to it from other modules, and an optional SaveChanges guard catches the writes the analyzer cannot see."
status: draft
---
# ADR-0107: A shared entity may name the one module that writes it

- Status: Proposed (evaluation; not implemented)
- Date: 2026-11-23
- Related: [ADR-0002](0002-cross-module-communication.md) (`ELMOD002`, the location-based boundary analyzer),
  [ADR-0007](0007-data-is-platform-module-as-plugin.md) (feature modules over a shared data layer),
  [ADR-0008](0008-bounded-contexts-and-the-graduation-path.md) (the mini bounded context)

## Context

Elarion knows two kinds of entity ownership, both by location (`ELMOD002`):

- an entity **outside every module** (the shared kernel) is shared: every module may read and write it;
- an entity **inside a module** is that module's: another module may not depend on it at all, and reaches the data
  through a `[ModuleContract]` (the mini bounded context of ADR-0008).

An application with feature modules over one shared data layer wants a third: **every module may read a table, one
module writes it.** Its modules join across tables freely (an e-mail is visible through the contact it is assigned to,
which is a read of the contacts table by the mail module), but "one writer per table" keeps invariants in one place.
It enforces that today with tests that scan the source with regular expressions for writes to each entity outside its
module. Those tests are brittle: they miss an object initializer spread over lines, a `with` expression, an
`ExecuteUpdate`, and they break on formatting.

Moving the entities into their modules does not fit: every cross-module read would then need a contract, and the
queries that join tables (authorization through an owner, `WhereAuthorizedVia`, `QueryAuthorizers.Via`) would have to
be rewritten as contract calls that cannot join.

## Proposal

1. **`[WrittenBy("Contacts")]` on a shared-kernel entity** (attribute in `Elarion.Abstractions.Modules`) names the
   module that writes it, by module name as `[AppModule]` declares it. Only an entity outside every module may carry
   it; on a module-internal entity it is redundant and reported.
2. **`ELMOD008` (Warning)** — a new `DiagnosticAnalyzer` beside `ModuleBoundaryAnalyzer`, using the same rule for
   "which module is this code in" (the namespace of the containing type). In code inside another module it reports:
   - an assignment to a settable property or field of the entity (also in an object initializer, a `with`
     expression, `+=` and the like), so creating the entity counts as writing it;
   - `DbContext`/`DbSet<T>` `Add`, `AddRange`, `Attach`, `Update`, `Remove`, `RemoveRange`, and setting
     `EntityEntry.State`, for the entity type;
   - `ExecuteUpdate`/`ExecuteDelete` over an `IQueryable<T>` of the entity, and Elarion's bulk insert APIs for it.

   Code outside every module (shared-kernel helpers) is not reported, consistent with `ELMOD002`'s location rule.
3. **An optional runtime guard** for what the analyzer cannot see: an EF Core `SaveChanges` interceptor
   (`UseElarionWriterCheck()`, test and development hosts) that compares the entity types the change tracker holds as
   added, modified or deleted with the module of the handler that runs, which the generated registration knows. It
   catches mutation through domain methods and through entities that flowed in from elsewhere.

## Consequences (if accepted)

- A shared data layer gets "one writer per table" from the framework, at build time, without regular expressions.
- **The analyzer is incomplete by design.** It cannot tell a mutating domain method (`contact.Rename(…)`) from a
  query method, so an entity whose state changes only through its own methods is protected by the runtime guard
  only. Raw SQL (`ExecuteSql`, `FromSql`) and shared-kernel helpers that write on a module's behalf pass both checks.
- The analyzer works on operations (`IOperation`), not on the dependency surface `ELMOD002` reads, so it costs more
  per compilation; it runs only when some entity carries the attribute.
- The runtime guard needs "the module of the running handler" as an ambient value in the pipeline, which Elarion does
  not expose today.

## Alternatives

- **Keep it in the application** (source-scan tests). Works, and is what the application does; brittle, and each
  application re-derives it.
- **Module-internal entities with contracts for reads** (ADR-0008's mini bounded context). The sanctioned way to own
  data, but it gives up cross-module joins, which this application's authorization rules rely on.
- **Only the runtime guard.** Complete for EF change tracking and cheap to build, but it reports at run time, in the
  tests that happen to exercise the write.
- **Widen `ELMOD002`** so module-internal entities may be read across modules but not written. It changes the meaning of
  an existing diagnostic for every application, and "may another module read this" would no longer have one answer.

## Recommendation

Fits the module model as an opt-in refinement of the shared-data-layer tier, between "shared" and "module-internal",
and it is domain-neutral. Implement the analyzer (2) first, with the attribute (1); add the runtime guard (3) only if
an application needs domain-method coverage, since it needs a new ambient value. Not implemented until the maintainer
decides.

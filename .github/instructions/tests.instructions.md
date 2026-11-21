---
description: 'Rules for tests and benchmarks'
applyTo: 'tests/**'
---

# Testing

The rules for everything under `tests/`. This file is their source; `AGENTS.md` points here.

- Add a regression test for every bug fix. Follow nearby naming/capitalization and avoid
  `Arrange`/`Act`/`Assert` comments.
- Keep tests deterministic. Timing-sensitive tests are appropriate only when concurrency, scheduling, or a
  timing contract is itself under test.
- Generator changes require generator tests, inspectable deterministic output, and the incremental cache test
  described above. Run the affected generator suite after each change.
- Test database behavior against **real PostgreSQL through Testcontainers**, never EF Core InMemory. InMemory
  diverges from PostgreSQL semantics and produces false confidence. Use Docker-gated fixtures: start a real
  container when Docker is available and **skip**, rather than fail, when it is not. Tag integration tests
  `[Trait("Category", "Integration")]`. `Microsoft.EntityFrameworkCore.InMemory` is deliberately absent from
  `Directory.Packages.props`; do not add it.
- Hot-path changes need the relevant benchmark in `tests/Elarion.Benchmarks`; connection, actor, SQL-mapping,
  and bulk-operation claims require measured evidence, not an allocation intuition. TypeScript generator
  changes also require generating a representative schema and NodeNext type-checking emitted client files.

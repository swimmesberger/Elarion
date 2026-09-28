# ADR-0077: SQL array parameters are an explicit wrapper, backed by a build-time diagnostic

- Status: Accepted
- Date: 2026-09-28
- Related: [ADR-0058](0058-aot-sql-row-mapping.md) (the interpolation contract this extends),
  [ADR-0017](0017-dependency-light-core.md) (the neutral core stays driver-free),
  and the [SQL mapping](../capabilities/sql-mapping.mdx) capability page.

## Context

The SQL interpolation handler (ADR-0058) gives a collection hole exactly one meaning: it expands to a
parenthesized parameter list for `IN`. That is the right default, but it is the wrong shape wherever SQL expects
a single **array value** — PostgreSQL's `= ANY(…)`, `<> ALL(…)`, and the array operators `@>`, `<@`, `&&`:

```csharp
long[] ids = [1, 2, 3];
$"SELECT … WHERE id = ANY({ids})"   // renders … = ANY((@p0, @p1, @p2)) — a row, not an array
```

The mistake compiles, passes statement-rendering tests, and is rejected by the database only at run time
(`op ANY/ALL (array) requires array on right side`). It is also a natural thing to write: `= ANY(@p)` with one
array parameter is the idiomatic PostgreSQL form — one parameter and one cached plan whatever the length, and
an empty array keeps both `= ANY` and `<> ALL` correct, which no `IN` spelling can. The tier had no way to
express it.

## Decision

1. **An explicit wrapper, `SqlArray.Of(collection)`**, binds the whole collection as one `@pN` parameter whose
   value is a typed `T[]`. It follows the existing value-type vocabulary of the tier (`SqlStatement`,
   `SqlWhere`): a named type the handler recognises by overload, not a string convention. It is a
   `readonly struct` that holds only the array, so wrapping allocates nothing beyond the array itself (an
   existing `T[]` is bound by reference; any other sequence is copied once when wrapped). `byte` elements are
   rejected, because every ADO.NET provider binds `byte[]` as a scalar binary value.

2. **Provider support is checked at bind time with an allow-list.** The neutral `Elarion.Sql` takes no driver
   dependency, so `SqlStatement.ApplyTo` recognises Npgsql structurally (the same namespace test the unit of
   work already uses for `lock_timeout`) and throws `NotSupportedException` for any other provider before the
   command is touched, naming the `IN {collection}` alternative. An allow-list fails closed: an unknown provider
   gets an actionable message instead of a driver type-mapping error or a silently different binding.
   The check is a type test per parameter value, not a per-statement flag, so statements that do not use arrays
   pay no extra allocation.

3. **The PostgreSQL provider enables array mappings.** `AddElarionPostgreSql(connectionString)` builds its
   source with `NpgsqlSlimDataSourceBuilder`, which omits array type mappings unless `EnableArrays()` is called;
   the registration now calls it. A host that passes its own slim-built `NpgsqlDataSource` enables them itself.

4. **`ELSQL012` reports the mistake at build time.** An analyzer shipped with `Elarion.Sql` flags an
   interpolation hole whose static type the handler would expand (the handler's own rule: `IEnumerable`, except
   `string` and `byte[]`) when the literal SQL immediately around it is an array position: directly inside
   `ANY(` / `ALL(` / `SOME(` (any case, optional whitespace, on a word boundary), or next to `@>`, `<@`, `&&`.
   The message names both correct spellings. Severity is Warning — enforced under `TreatWarningsAsErrors`,
   suppressible with a justification for the rare false positive of a text heuristic.

## Options considered

- **A format specifier (`{ids:array}`).** Rejected: it is stringly typed (a typo silently falls back to the
  `IN` expansion), and it would add a second meaning to a hole's type that the analyzer and readers must
  decode from a format string.
- **Infer array binding from the surrounding text at run time** (bind an array when the literal before the hole
  ends in `ANY(`). Rejected: implicit, dependent on run-time text scanning, and it would change the meaning of
  statements that already exist. The same text test is acceptable as a *diagnostic* because it only asks the
  author to state intent; it never changes what a statement does.
- **Only document the `IN` form.** Rejected: correct, but the failure mode stays silent until run time, and
  long lists keep producing one parameter per element.
- **A provider capability seam (`ISqlDatabase` reporting array support).** Rejected for now: statements are
  also bound on caller-owned connections (`AsSqlSession`) where no database handle exists, so the command is
  the only object every bind path has.

## Consequences

- `= ANY({SqlArray.Of(ids)})` works on PostgreSQL with one parameter and correct empty-set semantics; the
  collection hole keeps its `IN` meaning unchanged.
- The allow-list is Npgsql only. A command wrapped by a profiling/instrumenting provider (whose command type
  is not in the `Npgsql` namespace) is rejected even when the underlying driver supports arrays; widening the
  list is an additive change when a real second provider needs it.
- `ELSQL012` sees only the literal text adjacent to the hole within one interpolated string (including
  `$"…" + $"…"`). SQL assembled across separate fragments, holes typed as `object`, and other array-valued
  positions (`unnest(…)`, `ARRAY[…]`, function arguments) are not inspected.

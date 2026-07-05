# ADR-0038: Client-assigned entity identity — UUIDv7, a truthful key model, and ELID001

- Status: Accepted
- Date: 2026-07-06
- Related: [ADR-0006](0006-incremental-source-generator-conventions.md) (generator conventions),
  [ADR-0018](0018-generated-infrastructure-is-framework-named.md) (generated infrastructure is
  framework-named), [ADR-0025](0025-distributed-scheduler-coordination.md) (scale positioning: shipped
  defaults serve ~1–10 nodes on the one PostgreSQL the app already runs)

## Context

Elarion's documented idiom assigns entity identity in code — `new Client { Id = …, … }` in the docs, the
tutorial, and the Billing sample. EF Core's default convention makes the *model* claim the opposite: a
`Guid` primary key is declared `ValueGeneratedOnAdd` (with the client-side `GuidValueGenerator`), and EF's
insert-vs-update heuristic is defined on that claim — **a set value on a "generated" key means the row
already exists**.

The two contracts disagree, and the disagreement detonates in exactly one spot: a new child added to a
**tracked** parent's collection navigation — the natural "replace the children" update:

```csharp
var tenant = await db.Tenants.Include(t => t.Contacts).SingleAsync(t => t.Id == id, ct);
tenant.Contacts.Clear();
tenant.Contacts.Add(new Contact { Id = Guid.CreateVersion7(), … });   // never inserted before
await db.SaveChangesAsync(ct);   // UPDATE … WHERE id = <new-guid> → 0 rows → DbUpdateConcurrencyException
```

`DetectChanges` discovers the child, sees a set "generated" key, tracks it as `Modified`, and `SaveChanges`
issues an `UPDATE` that affects zero rows (dotnet/efcore#35090, #26830). Three things keep this invisible
until production:

- `context.Add(graph)` forces the whole graph `Added`, so **creates always work** — only replace-style
  updates hit the heuristic.
- The **EF InMemory provider skips the affected-rows check**, so unit tests stay green. SQLite does not
  enforce it either. Only a real database (a Testcontainers PostgreSQL test) catches the bug.
- The failure needs a *tracked* parent, so most one-entity tests never construct the shape.

A downstream Elarion adopter shipped exactly this bug in a replace-children handler; a Postgres integration
test caught it, and their root-cause fix (declare `ValueGeneratedNever` for the domain's Guid PKs, verified
schema-neutral with an empty `Up()`) is what this ADR promotes into the framework.

There is a second, independent pressure on id choice: **random v4 Guids scatter primary-key b-tree
inserts** across the whole index, while UUIDv7's time-ordered prefix keeps inserts append-mostly. At the
tier Elarion targets (ADR-0025 — everything on the one PostgreSQL the app already runs), index health on
the hottest tables is worth having by default.

## Decision

**The application owns entity identity.** It mints ids in code at the creation site with
`Guid.CreateVersion7()`; the database never generates domain keys; the model says so.

Concretely, five moves:

1. **A truthful model, generated.** The `[GenerateDbSets]` `ConfigureEntities` now ends with a generated
   `ApplyElarionClientAssignedGuidKeys(modelBuilder)` pass that declares single-property `Guid` primary
   keys `ValueGenerated.Never`. Scoping is by the **assemblies of the discovered `[EntityConfiguration]`
   entity types** — not the type list itself — so navigation-discovered children (exactly where the
   heuristic detonates) are covered, while Identity, DataProtection, and Elarion feature tables in other
   assemblies are never touched. The pass runs **last** (after the `OnEntitiesConfigured` seams) and only
   overrides EF's *convention* claim: explicit or data-annotation `ValueGenerated` configuration, a custom
   value generator, and store defaults (`HasDefaultValueSql`/`HasDefaultValue`/computed columns) all win.
   There is deliberately **no knob** — explicit per-entity configuration is the opt-out, so the taught
   idiom and the model agree by default (a flag would make the correct model opt-in and the landmine the
   default).
2. **Identity's packaged contract is declared, not implied.** `ApplyElarionIdentity` now sets
   `ValueGeneratedOnAdd()` explicitly for a `Guid` `TKey` on `TUser`/`TRole` (Identity's `UserManager`
   never assigns the key; it relies on EF's client-side generator). `TUser`/`TRole` are app-owned CLR
   types that may live beside domain entities, and no app-level key convention may reinterpret them.
3. **UUIDv7 at the creation site**: `Id = Guid.CreateVersion7()`. The BCL method *is* the seam — a
   framework wrapper (`EntityId.New()` / `ElarionIds.NewV7()`) was considered and rejected: a one-line
   wrapper adds no invariant, no type, and no validation, and once the analyzer owns the rule it buys
   nothing.
4. **An analyzer instead of a hard ban.** `ELID001` (warning, in the `Elarion` package's analyzer bundle)
   flags every `Guid.NewGuid()` invocation — "random (v4) Guid: prefer `Guid.CreateVersion7()` for entity
   keys (time-ordered, index-friendly)" — with a code fix that rewrites the call (the fix lives in a
   Workspaces-layer sibling assembly, `Elarion.Generators.CodeFixes`, bundled in the same
   `analyzers/dotnet/cs` folder; the compiler must not load Workspaces dependencies, RS1038). Flagging
   *every* `NewGuid()` — not just "entity id" positions — is deliberate: detecting "is this a key?"
   without the EF model is heuristic and brittle, and v7 is the better default for nearly every use.
   Warning severity is "noting", not banning: under `TreatWarningsAsErrors` it enforces, apps that want it
   advisory dial it down in `.editorconfig`, and the rare legitimate v4 use (an id that must be
   unpredictable) becomes a visible, suppressed-with-justification site. The rule stays silent on target
   frameworks without `Guid.CreateVersion7()` (&lt; .NET 9).
5. **The framework practices the doctrine.** Framework-minted ids that land in database keys or
   time-ordered streams (outbox message ids, staged-upload session ids, blob storage names, event/message
   ids, scheduler run ids) are minted with `Guid.CreateVersion7()`; framework tables already declared
   their keys `ValueGeneratedNever`.

Two caveats are part of the contract and documented rather than hidden: a v7 id **embeds its creation
instant** (visible wherever the id is, e.g. in URLs — acceptable, but a conscious choice), and ids created
within the same millisecond are **mutually unordered** — the id is never a business sort key (`CreatedAt`
is).

## Rejected alternatives

- **Database-side generation** (PostgreSQL 18 ships native `uuidv7()`): ids would not exist until
  `INSERT`, breaking pre-save id use (FKs, soft references, links assembled inside one unit of work);
  tests on non-Postgres providers cannot run SQL defaults; and store-generated keys re-introduce exactly
  the insert-vs-update heuristic this ADR removes.
- **An EF `ValueGenerator`** (centralize v7 in the model): keeps `ValueGeneratedOnAdd` semantics, so any
  explicitly-set id — imports, tests, seeding — walks straight back into the misclassification trap, and
  it makes identity an infrastructure concern instead of a domain one.
- **A `Guid` wrapper API** (`EntityId.New()`): no invariant, no type, no validation; the analyzer owns the
  rule (see Decision 3/4).
- **`BannedApiAnalyzers` + `BannedSymbols.txt`** (the adopter's interim solution): app-side plumbing, a
  hard ban with no code fix and no TFM awareness. The framework analyzer is the right home.
- **An opt-in flag** (`[GenerateDbSets(ClientAssignedGuidKeys = true)]` or
  `UseClientAssignedGuidKeys()`): contradicts the happy-path convention — the framework must not teach an
  idiom whose default model misdeclares it.

## Consequences

- The natural replace-children update pattern works on a real database; a Testcontainers pin test proves
  both directions (inserts under `Never`; `DbUpdateConcurrencyException` under the convention's `OnAdd`
  claim — if EF ever fixes the heuristic, that contrast test fails and tells us to revisit).
- Pre-save id use (FKs, soft references, returning the id from a create handler) is safe by construction.
- **Migration impact: schema-neutral.** The model change produces an *empty* migration whose only purpose
  is re-syncing the snapshot (EF 9+ refuses to `Migrate()` with pending model changes). The Billing sample
  carries the proof (`ClientAssignedGuidKeys` migration, empty `Up()`).
- **Behavior change:** an app that left `Id` unset and relied on EF's client-side generator now inserts
  `Guid.Empty` (loudly failing on the second row). Set ids in code (recommended) or configure
  `ValueGeneratedOnAdd()` explicitly on that entity — explicit configuration always wins.
- The InMemory provider cannot catch misclassified writes; replace-children behavior needs a real-database
  integration test. Documented on the entity-authoring page; the framework's own pin lives in
  `ClientAssignedGuidKeyTests`.
- ELID001 fires on every `Guid.NewGuid()` in analyzer-covered code, including tests compiled with the
  bundle — by design (v7 is the better default there too); `.editorconfig` scoping is the escape hatch.

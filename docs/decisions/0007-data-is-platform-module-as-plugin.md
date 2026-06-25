# ADR-0007: The data layer is application logic — modules as plugins over it

- Status: Accepted
- Date: 2026-06-25
- Related: [ADR-0002](0002-cross-module-communication.md) (cross-module contracts),
  [ADR-0006](0006-incremental-source-generator-conventions.md) (generator conventions),
  [ADR-0008](0008-bounded-contexts-and-the-graduation-path.md) (bounded contexts — the data-separation future),
  [solution structure](../concepts/solution-structure.mdx), `ELMOD002`.

## Context

Replacing `[DbEntity]` with `[EntityConfiguration]` made the *configuration* the source of an
entity's `DbSet` (the marker now sits on the `IEntityTypeConfiguration<T>`, not the entity). That
forced a question the old model let us dodge: **where does configuration live, and what is a "module"
relative to the data?**

A single-assembly modular monolith has, by default, **one `DbContext` over one shared schema** — and may
partition into several via scopes (`[GenerateDbSets("ctx")]` / `[EntityConfiguration("ctx")]`). A scope is
an **application-layer** partition: a separate `DbContext` that owns a subset of the model in *code*, over
(by default) the **same database and schema**. It scopes which entities a context sees, not where the data
physically lives — Elarion neither requires nor recommends a per-scope schema (a scoped `DbContext` *could*
target one, but that is the user's choice, not the framework's). So a scope is a *logical* boundary; the
*physical* data separation (its own schema and/or database) is what a **bounded context** adds — see
[ADR-0008](0008-bounded-contexts-and-the-graduation-path.md). "Shared data" holds **within a context**.
There are two coupling surfaces, and they are *not* the same boundary:

- **Code** — handlers, services, the schema-mapping classes. Module-private; cross-module use goes
  through a published `[ModuleContract]` (ADR-0002), enforced by `ELMOD002`.
- **Data** — the entities and the one relational model they form. Reached by every module through the
  shared `DbContext` **by design**.

This rests on a deliberate upstream choice: Elarion does **not** use repositories — and **not even a
context interface**. The concrete `DbContext` *is* the unit-of-work/repository, and handlers work directly
against it — `DbSet<T>`, LINQ, raw SQL, and provider-specific functions — with no wrapping abstraction (an
`IAppDbContext`-style interface would only re-introduce one, and would need a leaky `AsDbContext()` escape
hatch the moment a handler needs raw SQL). So the persistence model, including its physical characteristics
(indexes, columns, provider features), is *intentionally* an Application-facing capability, not a hidden
Infrastructure secret. That premise is what determines where configuration belongs.

The general rule behind this — the line between **application logic** and **infrastructure** — is *not*
data-vs-code; it is whether the application depends on a dependency's **specifics** or only on its
**intent**:

- **Intent-only → infrastructure (a port + adapter).** The app states *what* it wants — "send this
  email", "store these bytes", "publish this event" — and the mechanism is interchangeable. A narrow,
  stable interface captures the intent without leaking specifics; swapping the adapter changes nothing
  about the app's meaning. `IEmailSender`, `IBlobStore`, `IIntegrationEventBus`, caching, and
  `ICurrentUser` are all this.
- **Specifics-are-the-logic → application.** A unique constraint *is* a business invariant; an index
  exists for a specific feature's query; the queries (LINQ, raw SQL, provider functions) encode business
  rules. The app reasons about these particulars directly and cannot swap the engine without rewriting
  logic. The relational model is therefore application logic, not a hidden detail.

The same technology splits by *relationship*: Postgres-as-a-blob-store is a port (`IBlobStore` — you only
ever say "store/return bytes"), while Postgres-as-the-domain-model is logic (you write business queries
against it).

### "Platform" is the host; the data layer is the application

The word **"platform"** is already taken, and an earlier "data is platform" framing wrongly borrowed it.
Across Elarion, **platform** (or *platform capabilities*) means what the **host / infrastructure** provides
at runtime — and nothing else:

- **The host / platform.** Provides capabilities *at runtime*: the DI container, the transports
  (HTTP/JSON-RPC/MCP), the *connection* to a database (which PostgreSQL instance, the connection string,
  `UseNpgsql`), and the *implementations* of intent-only ports (SMTP for `IEmailSender`, …). The platform
  is **agnostic to the domain**: the application assumes a stable PostgreSQL connection, but the host
  neither knows nor cares which database it is or what tables live in it.

The **data layer** is a *different thing, and it is not platform.* It is what *lives in* the database —
entities, configuration, the `DbContext`, migrations — plus the PostgreSQL-specific queries written
against them. This is **application logic**: the application owns its schema and its dialect. The host
supplies the *connection*; the application supplies the *content*.

The two get confused because the data layer is **viewpoint-relative**: from a **feature module's** vantage
it is the shared substrate it is *given* to build on; from the **host's** vantage it is an *application
concern*, not part of the runtime it provides. So the data layer is application logic that a feature builds
on — which is why "data is platform" was wrong (it lent the host's word to an application concern). "The
database is part of the application" therefore means its **content and dialect** (application logic,
coupled to PostgreSQL on purpose), **not** the **connection to a specific instance** (host platform).
Hence persistence is tested against a **real PostgreSQL (Testcontainers)**, never an in-memory or SQLite
provider — provider-specific SQL must run on the real engine — which also removes the usual "abstract the
database for testability" argument.

So throughout this ADR: **platform = host capabilities; the *data layer* = the application's shared data
that modules build on.** Conflating them (treating the data layer as host/infrastructure, or an entity as
"owned" by the feature that happens to use it) is what produced the original confusion.

## Decision

1. **Modules are *feature* separation, not *data* separation.** In a single-assembly system, assume
   data is shared. Every module may query the whole database through the shared `DbContext`; that is
   deliberate. Real data isolation is a separate `DbContext` / bounded context — see ADR-0008.

2. **A feature/module is a *plugin* over the application's data layer.** A feature builds on the shared
   **data layer** — `Domain` (entities) + `Persistence` (configuration + the `DbContext`), which is
   application logic — and consumes **host platform capabilities** through service ports. It asks "what can
   I build with what this offers?" and composes it (touching as many entities as it needs) into behavior.
   The governing rule is the **dependency arrow**:

   ```
   feature ──▶ shared data layer    (freely — use any entity)
   feature ──▶ feature              (only via a [ModuleContract])
   shared data layer ──▶ feature    (NEVER — the data layer must not know its plugins)
   ```

   Touching many *entities* is using the data layer; depending on another *feature* is the thing to
   avoid. A "configured entity is a discovered entity," but no feature *owns* an entity — entities are
   shared data the whole application draws on.

3. **Entity configuration belongs to the application's data layer, not a feature.** `[EntityConfiguration]`
   classes live in a shared **`Persistence`** layer (a sibling of `Domain`), **not** inside a feature
   module. The decisive reason is the arrow: putting a config in a feature folder *inverts* it — a piece
   of the shared data layer would live inside a plugin, so removing the plugin would break the data layer.
   (Secondary: under one `DbContext` the configs are one coupled model — shared conventions, cross-entity
   relationships, an index budget — so "which feature owns this config" is malformed.)

4. **Directory structure mirrors *enforced* boundaries — don't signal a boundary you don't enforce.**
   One folder per `DbContext`:
   - Single `DbContext` → a **flat** `Persistence/` (one file per `IEntityTypeConfiguration<T>` +
     shared conventions). Sub-foldering by feature would imply per-feature data ownership the runtime
     does not honor.
   - Scopes (multiple `DbContext`s in one assembly) → **one folder per scope**, because each scope is
     a real second model. The urge to partition the flat folder is the urge to introduce a real
     boundary — satisfy it with a scope, not a cosmetic subfolder.

5. **Reference other aggregates by ID, not by navigation property.** A cross-aggregate `Guid`
   (`Invoice.ClientId`) keeps the entities decoupled at the model level and needs no cross-module
   relationship configuration. A cross-aggregate *navigation* creates an EF relationship that no
   config placement can cleanly sever — and it is what makes a future context split expensive (see
   ADR-0008). Compose across aggregates by ID + `[ModuleContract]`, not by object graph.

6. **`ELMOD002` flags module-internal *code* — a `[Service]`, a handler, or an
   `[EntityConfiguration]` — never an entity.** Entities are shared data; flagging a reference to one
   would contradict "every module reaches the whole database by design." (Entities also carry no
   marker, so there is nothing to flag.)

## Consequences

- **The delete-the-module test holds for behavior.** Deleting a *pure-behavior* feature folder
  (handlers/jobs/endpoints over the existing data layer) leaves every other feature compiling and the
  data model untouched — the plugin promise. `[EntityConfiguration]` is discovered structurally
  (never referenced by name), so removing a config never breaks another module's *code*.

- **New data has a footprint on the data layer, on purpose.** A feature that introduces *new* data (an
  entity, index, or column) extends the shared data layer; that data lives in `Persistence` and does
  **not** vanish when the feature folder is deleted — its removal is a deliberate schema/migration step,
  because data is durable and shared. Putting such config in `Persistence` (not the feature folder) makes
  the footprint explicit and prevents an accidental, silent `DbSet`/table drop. *How* a plugin should own
  new typed data while keeping the "rip it out cleanly" property is an open question, deferred to ADR-0008.

- **The data layer is application logic — it is not `Infrastructure`.** Because the app depends on the
  database's specifics (constraints, indexes, queries), the whole persistence concern —
  `[EntityConfiguration]`, the concrete `DbContext`, and migrations — lives in the application as one
  `Persistence` layer. The host provides only the **connection**: provider registration
  (`UseNpgsql(...)` + the connection string). `Infrastructure` is reserved for intent-only mechanism
  adapters (the SMTP `IEmailSender`, external API clients). This keeps a schema change a single-layer edit
  — config and migration together — and, since config and `DbContext` share an assembly, removes the need
  for the cross-assembly configuration manifest.

- **Placement is a convention, not enforced.** The generator discovers `[EntityConfiguration]`
  anywhere, so points 3–5 are conventions this ADR establishes; the enforced parts are the generator
  behavior and `ELMOD002`. Implementation status: `samples/Billing` follows this ADR — entities in
  `Billing.Application.Domain`, the persistence layer (configuration, `BillingDbContext`, and migrations)
  in `Billing.Application.Persistence`, the SMTP adapter in `Billing.Infrastructure`, and provider
  registration in the `Billing.Api` host.

## Alternatives considered

- **Config in the shared kernel, next to the entity POCOs.** Rejected: when the kernel is (or may
  become) a pure assembly it drags EF Core into the most-depended-on layer; even where the kernel is
  an EF-aware namespace it centralizes into the most-shared files and re-opens ownership ambiguity.
  `Persistence` is the honest home — EF-aware, shared, but not the domain.

- **Config co-located in the stewarding feature module.** Rejected as the default: it inverts the
  dependency arrow, and for a shared entity whose `DbSet` is queried from other modules, deleting the
  steward removes that `DbSet` and breaks them. Co-location is only correct for genuinely
  *module-private* data — which is the bounded-context case (ADR-0008), not a feature.

- **Configuration (and the `DbContext`) in `Infrastructure`.** This is the orthodox Clean-Architecture
  placement — persistence as an outer detail — *and it would be the sounder choice if `Application` were
  persistence-ignorant behind repositories.* It is not: Elarion exposes `DbSet<T>`, LINQ, raw SQL, and
  provider functions to handlers by design (the `DbContext`-as-repository stance, with no context
  interface), so the data layer is already application logic — handlers reason about indexes, columns, and
  provider features directly. Moving it to `Infrastructure` would treat application logic as a host concern
  and scatter one cohesive vertical-slice concern across the central boundary for **no** isolation gain.
  Rejected on those grounds. (It becomes the sounder layout only under a repository / persistence-ignorant
  `Application` — which Elarion rejects. The host still owns the *connection*; that is the only piece that
  is genuinely infrastructure.)

- **Full centralization for governance.** A strong DBA / data-governance gate (schema reviewed as a
  single artifact, regulated change control) is the condition under which one reviewed schema surface
  outweighs the data-layer-as-application model. Absent that, this model wins.

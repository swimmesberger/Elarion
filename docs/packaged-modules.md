---
type: Concept
title: "Packaged modules: one module, several applications"
description: "Proposed direction: a module that owns its data and ships as versioned NuGet and npm packages from a module suite, so several applications on the same stack compose it in their own look; the layers, how modules meet, the user interface on Elarion's UI components, what Elarion has today, what it must add and in which order."
generated: { by: claude-code/claude-opus-5-5, at: 2026-11-26T12:00:00Z }
status: draft
stale_after: 2027-05-31
---

# Packaged modules: one module, several applications

Today an Elarion module lives in the source tree of one application. This page proposes how a module can
live in a **module suite** instead and be composed by several applications that share a stack: Elarion and
PostgreSQL on the backend, React and Tailwind CSS on the frontend, one infrastructure tool that deploys them.
Time tracking is the running example: written once, used by every application that needs it.

Nothing on this page is built unless section 12 says so. It is a concept: each of its decisions becomes an ADR
and each piece of Elarion work a spec, in the order of section 13. It draws on the module system of a large
modular-monolith consuming application, where layers, contracts, extension points and a deletion test work at
scale. What carries over is taken; the names follow `GLOSSARY.md` and the vocabularies of domain-driven design
and hexagonal architecture (section 4).

**In one paragraph.** A packaged module is a part of a product that a user would name (time tracking,
documents, contacts), shipped as a few versioned packages: a contract, the module, optional providers and a UI
package. It owns its data in a schema of its own and meets the rest of the application only through its
contract: what it offers (module contracts, integration events), where others may add to it (extension points)
and what the host must give it (required ports). Its user interface is built from Elarion's UI components,
talks only to its own backend through a client generated from its own operations, and takes each application's
look from that application's design tokens and shell. Packaged modules live together in one module suite,
released as one version, whose reference host composes each module alone (the install test) and all of them
together. Elarion supplies the mechanisms, the UI components and the shell contract; the modules belong to the
suite, and the look to each application.

## 1. The goal, and what it is not

**The goal.** A module written for one application is used by a second one without a fork. The second
application adds one NuGet and one npm package, implements the module's required ports, and gets the module's
pages in its own look, the module's tables in its own database, and the module's fixes by raising a version.

**Same UX, not the same UI.** The module behaves the same in every application: a list filters and pages the
same way, a form reports errors the same way, a destructive action asks the same way. It looks like the
application it is in: its colours, type, radius and density, the shell around it, its wording.

**Not the goal:**

- **Runtime plugins.** Modules are composed at build time. Nothing loads an assembly or a remote bundle at run
  time; ADR-0032's non-goal (no micro-frontends, no module federation) stands.
- **Separate deployment.** A packaged module adds no process and no service. It runs in the application's one
  host against the application's one PostgreSQL, inside the scale tier.
- **Packaging everything.** The application's core domain, the reason the application exists, stays in the
  application as ordinary modules over its shared data layer
  ([ADR-0007](decisions/0007-data-is-platform-module-as-plugin.md)). Packaged modules hold what several
  applications need in the same way.
- **A look in Elarion.** Elarion ships components with neutral defaults; the brand, the values of the design
  tokens and the shell's layout stay in each application (section 8.4).

## 2. Where a packaged module sits

Domain-driven design sorts a product's subdomains into the core domain (what sets the product apart), supporting
subdomains (needed, specific to the business) and generic subdomains (needed, the same everywhere). Packaged
modules hold generic subdomains (time tracking, documents, contacts, notifications) and the supporting
subdomains that several applications share. The core domain stays in the application.

An application composed from a suite has five layers. A layer references only the layers above it.

| Layer | Holds | Lives in |
|---|---|---|
| Framework | Elarion's packages: handlers, modules, transports, persistence tiers, events, the contribution kit, the UI components and the shell contract. Domain-blind | Elarion, on NuGet and npm |
| Suite foundation | the suite's shared kernel (value types every module may use), components several of its modules share | the module suite |
| Packaged modules | generic and supporting subdomains, each a contract and its implementation | the module suite |
| Application modules | the application's core domain, over the application's shared data layer (ADR-0007 unchanged) | the application |
| Host | the composition root: the ASP.NET Core host, the web application's shell, the implementations of required ports, the theme | the application |

Between packaged modules, the direction is that of domain-driven design's context map. A module others depend on
is **upstream**: downstream modules reference its contract package. A module nothing depends on is a **leaf**.
Two leaves never reference each other, and a packaged module never references an application module: the
application is downstream of every packaged module it composes.

## 3. What a packaged module is

- A part of a product that a user would name, with pages or data of its own, that an application could leave
  out. Anything smaller is a folder inside a module, or a component.
- It owns, under one name: its contract, its handlers, its domain model, its schema and migrations, its jobs
  and consumers, its permissions, flags and settings, its user interface with its labels, and its tests.
- It holds one or more `[AppModule]`s, all feature modules, each gated by `Modules:{Name}:Enabled` as today. It
  never declares a core module: which modules are always on is the application's decision.
- It knows no application. It names nothing but the framework, the suite's foundation and the contracts of its
  upstream modules.
- It owns its data (section 5).

## 4. A module's packages and folder

The packages of a module mirror Elarion's own package shape, so one mental model serves both levels:

| Package | Holds | Mirrors | References |
|---|---|---|---|
| `Acme.TimeTracking.Contract` | every type that crosses the module's boundary: module contracts, integration events, the IDs and value types they carry, the module's extension points and required ports | `Elarion.Abstractions` | `Elarion.Abstractions`, the shared kernel, upstream contracts |
| `Acme.TimeTracking` | the module: `[AppModule]`, handlers, domain model, persistence, jobs, consumers | `Elarion` (core) | its contract, Elarion core and one persistence tier, upstream contracts |
| `Acme.TimeTracking.<Technology>` | a provider for a technology the module needs (an export format, an outside API), only when the host should be able to choose it or leave it out | the provider packages (`Elarion.Caching.PostgreSql`) | the module |
| `Acme.TimeTracking.Testing` | fakes of its module contracts, for the tests of downstream modules and applications | | its contract |
| `@acme/time-tracking` | the UI module: manifest, routes, components, labels, the client of its own operations | `@swimmesberger/elarion-contributions` | peers only (section 8.1) |

`Acme` stands for the suite's prefix. A project exists only where a dependency needs one: the contract so that
others can reference the module without its implementation, a provider so the host can choose a technology.
Everything else is a folder. So there is no `.Domain`, `.Application` or `.Infrastructure` project per module;
the layers are folders inside the module. A module that needs hand-written ASP.NET Core endpoints adds
`Acme.TimeTracking.AspNetCore` with a `[ModuleEndpoints]` class, as a web-free module does today.

One folder holds all of a module, so it has one owner, one review and one line in the code owners file:

```
modules/time-tracking/
  src/Acme.TimeTracking.Contract/
    ITimeTrackingQueries.cs          [ModuleContract]: what downstream modules may ask
    TimeBooked.cs                    integration event
    IBookingTargetSource.cs          extension point
    IWorkerDirectory.cs              required port
  src/Acme.TimeTracking/
    TimeTrackingModule.cs            [AppModule("TimeTracking")]
    Domain/                          aggregates, entities, value objects; module-internal
    Features/                        handlers, one folder per use case
    Persistence/                     its DbContext, entity configurations, migrations (schema time_tracking)
  src/Acme.TimeTracking.<Technology>/
  web/                               @acme/time-tracking
    src/index.ts                     public entry: the module, its extension points, its UI entry
    src/generated/                   its client, generated from its own operations
    src/i18n/
  tests/Acme.TimeTracking.Tests/     backend tests, against the reference host
  README.md                          what it does; its required ports, extension points and settings
```

**One idea, three vocabularies.** The names above, translated:

| Here | Domain-driven design | Hexagonal architecture | Clean Architecture |
|---|---|---|---|
| Packaged module | a bounded context for a generic or supporting subdomain | one application with its ports | one set of use cases with its entities |
| Contract package | the published language | the ports, both sides | the boundary interfaces |
| Module contract, integration event | open host service, an event between contexts | driving (primary) port | input boundary |
| Required port | where the module conforms; the host's implementation is an anticorruption layer | driven (secondary) port | output boundary, gateway |
| Handler | application service | the use case behind a driving port | interactor |
| `Domain/` | aggregates, entities, value objects | the application core | entities |
| Provider package, the host's implementations of ports | | driven adapters | frameworks and drivers |
| Transports, the UI module | | driving adapters | interface adapters |
| The suite's shared kernel | shared kernel | | |
| Host | the context map, made executable | the configurator | the composition root |

One deliberate difference: the module's database is not behind a port. As ADR-0007 decides for the application,
the module's handlers work against its `DbContext` directly, because the module depends on its database's
specifics (constraints, indexes, queries); a repository layer would only hide them. The boundary is the module's
schema, not an interface around its queries.

## 5. Data

A packaged module owns its data. It is the mini bounded context of
[ADR-0008](decisions/0008-bounded-contexts-and-the-graduation-path.md), packaged in its own assemblies (that
ADR's stage 4). The application's shared data layer cannot be used: a package written without knowledge of an
application cannot reference that application's entities, and the module's tables must not depend on them.

- Its entities are module-internal; its `DbContext` covers only them; its tables are in a schema named after
  the module (`time_tracking`).
- Its migrations are its own: their own version sequence and history, kept in the module's schema. The host's
  migration plan runs the migrations of every composed module at start, under the plan's one lock. Upgrading
  the module is raising its package version; its migrations come with it.
- It works on the application's connection, so a handler that calls a module contract writes both modules'
  rows in one transaction ([ADR-0015](decisions/0015-ef-core-transaction-participation.md)). The schema is
  therefore part of the module's model, not of the connection's search path.
- References across the boundary are by ID: no foreign key, no navigation, no join. A module that needs
  another's data asks that module's contract; a page that shows both composes two parts (section 8).
- A module that points at records of kinds it does not know (time booked on a project, a ticket, a building)
  stores the kind and the ID. Inside one context that pair is the last resort; between contexts it is the only
  option ([polymorphic references](concepts/polymorphic-references.mdx)). The owner's delete reaches the module
  as an integration event.
- Tenancy and identity come from the framework: the module uses ambient tenant scoping and the current user like
  any other module, so one package works in a single-tenant and a multi-tenant host. It never models tenants or
  users of its own.
- **Presence changes visibility, never data.** Disabling a module hides it and keeps its data. Removing its
  packages leaves its schema untouched; dropping the schema is a deliberate step of an operator.

## 6. How modules meet

A packaged module meets its surroundings in four ways, each declared in its contract package:

| Way | Direction | Backend | Frontend | When nothing is there |
|---|---|---|---|---|
| Module contract | others call it | a `[ModuleContract]` interface, implemented inside the module | its UI entry: components others may render, from its public entry | the caller requires the module (below) |
| Integration event | it tells others | an `IIntegrationEvent` through the outbox | a client event topic | nobody listens; nothing breaks |
| Extension point | others add to it | a contract the module consumes as a set | a token from `defineExtensionPoint` | a designed empty state |
| Required port | the host gives it | an interface with exactly one implementation | a service of the shell contract (section 8.5) | the host refuses to start and names the module and the port |

The contract of time tracking, with the two attributes this concept proposes:

```csharp
namespace Acme.TimeTracking;

// Exactly one implementation, supplied by the application.
[RequiredPort]
public interface IWorkerDirectory {
    ValueTask<Worker?> FindAsync(WorkerId id, CancellationToken ct = default);
    ValueTask<Worker?> FindForCurrentUserAsync(CancellationToken ct = default);
}

// Any number of implementations, from application modules or other packaged modules.
[ExtensionPoint]
public interface IBookingTargetSource {
    string Kind { get; }   // "project", "ticket": stable, chosen by the contributor
    ValueTask<IReadOnlyList<BookingTarget>> SearchAsync(string text, CancellationToken ct = default);
}
```

The application contributes from one of its own modules, which is how its core domain plugs into a generic
module:

```csharp
namespace MyApp.Application.Modules.Projects;

[Service]
internal sealed class ProjectsAsBookingTargets(AppDbContext db) : IBookingTargetSource {
    public string Kind => "project";

    public async ValueTask<IReadOnlyList<BookingTarget>> SearchAsync(string text, CancellationToken ct = default) =>
        await db.Projects
            .Where(p => EF.Functions.ILike(p.Name, $"%{text}%"))
            .OrderBy(p => p.Name)
            .Take(20)
            .Select(p => new BookingTarget(Kind, p.Id.ToString(), p.Name))
            .ToListAsync(ct);
}
```

The rules of extension points, on both sides, as the field system learned them:

- **The owner renders, the contributor supplies.** The owner fixes the payload type, the order, what happens
  with many and what the place looks like with none. A contributor adds no chrome of its own.
- **The contributor depends on the owner.** It references the owner's contract; the owner never learns who
  contributes.
- **A point follows the user's path:** a section of the navigation, the tabs of a detail page, the actions on a
  record. Never "module X's menu".
- **No contribution is a designed state.** The place disappears or shows its empty state.
- **A contribution says when it applies by a capability:** a permission, a flag, its own module being enabled.
  Never "module Y is there"; no code asks at run time whether another module is composed.

Dependencies between modules come in two kinds:

| Dependency | Onto | How | When the other is absent |
|---|---|---|---|
| Required | an upstream packaged module | a package reference to its contract, and its name in `DependsOn` | the host's build fails and names both modules (proposed) |
| Optional | anything downstream: the application, a leaf | only through an extension point or a required port the module owns | the function is not offered, or the host refuses to start for a required port |

## 7. Composing the backend

The application's host adds the package:

```xml
<PackageReference Include="Acme.TimeTracking" />
```

- **Discovery works today.** A `[GenerateModuleBootstrapper]` host reads the `[AppModule]`s of referenced
  assemblies from their Elarion manifest, so the module's handlers, services, jobs, consumers, permissions, flags
  and settings are registered, mapped on every transport and gated as one unit, with no line per module. Its
  `GetJsonTypeInfoResolver()` brings its JSON metadata.
- **Required ports.** The application implements each one in one of its modules; that implementation translates
  the application's model into the module's. At start the host checks the required ports of every composed
  module and refuses to start with one sentence per gap:
  `Module TimeTracking requires IWorkerDirectory, and no implementation is registered.`
- **Operation names.** A packaged module's JSON-RPC methods and HTTP routes start with its module name
  (`timeTracking.entries.list`; `/time-tracking/…` through `ConfigureEndpointGroup`), so modules written apart
  cannot collide. The composition test holds it (section 10).
- **Authorization.** The module declares permissions on its handlers. The application decides who holds them,
  through its own roles. A module names permissions, never an application's roles.
- **Variation without a fork**, from most to least preferred: configuration (`Modules:TimeTracking:*` options),
  settings the application's administrators edit (`[SettingDefinitions]`), a `[ConfigurationVariant]` or
  `[FeatureVariant]` the module offers, an extension point, a required port. A need beyond these becomes a
  feature of the module in the suite, or the application writes a module of its own.

## 8. Composing the user interface

### 8.1 The UI package

- One npm package per module, ESM with types, built in the suite. It exports the module: a manifest (plain data
  with lazy components, [ADR-0032](decisions/0032-frontend-contribution-model.md)) and a route factory.
- React, the router, TanStack Query and Elarion's npm packages (the UI components and the shell contract among
  them) are **peer** dependencies, so an application holds one copy of each.
- Its public entry exports, besides the module, only its extension points and its UI entry (components another
  module may render, such as a "time booked on this record" panel). `exports` in its `package.json` blocks deep
  imports, the npm counterpart of `ELMOD002`.

### 8.2 Its own client

- **A module's UI calls only its own module's operations.** When it shows another module's data, it renders
  that module's UI entry; when it computes with that data, it calls an operation of its own module, which asks
  the other module's contract on the server. A page makes one request for itself and one per module that
  contributes a part to it.
- So the package ships its own client, generated by `elarion-jsonrpc-client-generator` from a schema of the
  module's operations, which the suite's reference host exports. The client carries the module's vocabulary: its
  module name, permissions, flags, constants and presets.
- The client takes its transport (base URL, credentials, headers, trace propagation) from the shell contract, so
  it calls whichever host it is composed into.
- The module binds the contribution kit to its own vocabulary. The application's vocabulary, generated from the
  composed host, contains the module's, so the application's registry evaluates the module's `when` clauses
  against the same names. Whether the module's manifest type is assignable to the application's registry is to
  be checked when this is built.

```ts
// modules/time-tracking/web/src/index.ts
import { createContributionKit } from "@swimmesberger/elarion-contributions"
import { shellNavigation } from "@swimmesberger/elarion-ui-headless/react"
import { Modules, Permissions, type ModuleName, type PermissionName } from "./generated/session-client"

const { defineModule, contribute } = createContributionKit<{ module: ModuleName; permission: PermissionName }>()

export const manifest = defineModule({
  name: Modules.TimeTracking,
  when: { module: Modules.TimeTracking },
  contributes: [
    contribute(shellNavigation, [{
      id: "time-tracking.my-week", label: "nav.myWeek", icon: Clock, to: "my-week", order: 40,
      when: { permission: Permissions.timeEntries.read },
    }]),
  ],
})

// The application chooses where the module's pages hang and under which path.
export const routes = (parent: AnyRoute, path = "time") => [timeTrackingRoute(parent, path)]
```

### 8.3 Discovery and routes in the application

- **Discovery.** The application's web project finds packaged UI modules from its dependencies, the way the host
  finds packaged backend modules from its references. Proposed: a Vite plugin in Elarion's npm packages reads the
  application's `package.json`, takes every dependency whose own `package.json` marks it as an Elarion UI module,
  and generates a virtual module of static imports, beside the application's own glob over `./modules/*`. The
  same plugin adds those packages to Tailwind's sources (`@source`), so the stylesheet holds every class a
  packaged module writes.
- **Routes.** Today a module imports the application's root route. A packaged module cannot, so it exports a
  route factory that takes the parent route, and the application registers it with one typed line, which keeps
  TanStack Router's inference (ADR-0032, addendum). Inside, the module links relative to its own path; links to
  other modules' pages go through the shell contract's navigation.

### 8.4 Same UX, not the same UI: Elarion's UI components

Elarion ships behaviour and structure; each application owns the look
([ADR-0112](decisions/0112-elarion-ships-ui-components.md)). The split, layer by layer:

| Layer | What Elarion ships | What an application changes |
|---|---|---|
| Design tokens | the names: shadcn/ui's semantic CSS custom properties (`--background`, `--foreground`, `--primary`, `--muted`, `--destructive`, `--radius`, …), Tailwind v4's `--spacing` for density, neutral defaults in `theme.css` | their values: one stylesheet per application, light and dark |
| Primitives | button, input, select, dialog, menus, tabs, table and the rest, on Radix with shadcn/ui's component APIs, in `@swimmesberger/elarion-ui` | which variants it uses, a `className` where one instance must differ |
| Bound components | the behaviour every page shares, bound to the framework's contracts: the data table (server paging, sort keys, preset chips, row actions gated by `canCall`, a query again on client events), the command form (client validation from the generated schema, server errors on their fields, required marks from nullability), confirmation, decision and preview dialogs, error states per `AppError` kind | nothing: this is the shared UX |
| Headless state | the same behaviour without markup, in `@swimmesberger/elarion-ui-headless`, for a page that needs its own markup | |

The binding is Elarion's because only the framework knows its contracts: `Result` and `AppError`, validation
errors, requiredness from nullability
([ADR-0082](decisions/0082-requiredness-comes-from-nullability-in-both-directions.md)), request presets, sort
keys and limits ([ADR-0100](decisions/0100-the-schema-exports-constants-presets-and-limits.md)), decision-required
answers ([ADR-0101](decisions/0101-a-decision-required-answer.md)), previewable commands
([ADR-0104](decisions/0104-previewable-commands.md)), client events ([ADR-0043](decisions/0043-client-events.md))
and method requirements. A new contract reaches every application's pages in the release that adds it.

The rules for a packaged module:

- **It styles only through tokens and components.** Module code uses semantic colours only (`bg-primary`,
  `text-muted-foreground`); a lint rule in the suite rejects palette classes such as `bg-blue-500`. The palette
  itself stays available to an application's own pages, since one stylesheet compiles them all.
- **It adds no global CSS and no fonts.** Global styles are the shell's.
- **It renders Elarion's components, not copies of them.** A fix reaches every application with a version.
  Components several modules of the suite share and the framework has no contract for live in the suite's
  foundation, built from Elarion's primitives.
- **It uses one icon set,** the one Elarion's components use, so a contributed icon fits every shell.

### 8.5 The shell contract

The shell contract is what every application's shell gives a UI module. It is Elarion's, in the `/react` export
of the headless package, and it is the frontend side of the required ports.

- **Extension points the shell owns:** the navigation (sections and items), settings sections, dashboard
  widgets, search providers, quick actions. Their payloads are Elarion's, so one contribution fits every
  application; the navigation item carries a label key, an icon, a target, an order and a phone tab rank (the
  pattern of [one navigation point](concepts/frontend-modules/index.mdx)).
- **Services:** navigation (a link component and `navigate`, so a module never imports the application's
  router), the capability reader, notifications, confirmation, labels and formatting (locale, time zone,
  numbers, dates), the transport for the module's client, and an error boundary.
- The application's shell implements it once (`<ShellProvider value={…}>`); a module calls `useShell()`, and so
  do Elarion's components: a row link navigates through it, a failed command notifies through it, every text
  comes from its labels.

The shell contract fixes what a module can count on, not how the shell looks. Sidebar or top bar, where the user
menu sits, how the phone layout works: those are the application's.

### 8.6 Labels

- A module ships its labels in its UI package, in its own namespace: the texts of its pages and the labels of
  what its backend declares (permissions, error codes, settings, audit actions).
- An application may override any key of a module's namespace, for its own wording, without touching the
  module.
- A suite test holds that every permission, error code and setting a module declares has a label in every
  locale the suite supports.

## 9. The module suite

- **One repository** holds the foundation (the shared kernel, components several of its modules share), every
  packaged module in its folder, a reference host and a reference shell, and the composition tests.
- **One version** for every package of the suite, released together, as Elarion releases its own packages. A
  suite release names the Elarion version it is built and tested on; an application takes the two together.
- **Contracts follow the version.** After 1.0, a breaking change to a module contract, an integration event, an
  extension point, a required port or the shell contract is a major version. Before 1.0 the suite follows
  Elarion's stance: clean contracts over compatibility, callers updated in the same release.
- **The shared kernel stays tiny:** value types every module needs alike (a date range, money). Never an entity
  ([ADR-0008](decisions/0008-bounded-contexts-and-the-graduation-path.md), decision 5). It starts empty.

```
suite/
  foundation/
    src/Acme.SharedKernel/
    web/components/            @acme/components: what several modules share, built from Elarion's primitives
  modules/
    time-tracking/             section 4
    documents/
  hosts/
    Acme.ReferenceHost/        composes any set of modules, with stub implementations of every required port
    reference-shell/           the smallest shell that fulfils Elarion's shell contract, on Elarion's default theme
  tests/                       composition tests over all modules
```

**Deployment.** An application stays one host and one database. The tool that deploys the applications needs to
know what each is composed of. Proposed: the bootstrapper writes a **composition document** at build
(`elarion-composition.json`: modules and versions, schemas, configuration sections, required ports and their
implementations, permissions, scheduled jobs). The deployment tool reads it to create schemas and database
roles, to check configuration before a rollout, and to show which application runs which version of a module.

## 10. What holds it

Rules nobody checks drift. Each check names no module, so a new module is covered without a test edit.

| Check | Holds | Runs in |
|---|---|---|
| Install test | each module, composed alone into the reference host with stub ports, migrates a fresh PostgreSQL (Testcontainers), starts, and passes its own backend and UI tests | the suite's CI, per module |
| Missing-port test | the same with one required port taken away fails at start, with one sentence that names the module and the port | the suite's CI, per module |
| Composition test | all modules together: no two share an operation name, a route, a schema, a permission, a label namespace or an extension point ID | the suite's CI |
| Removal test | the reference host with every module but one leaf still builds and starts | the suite's CI, per leaf |
| Boundaries | a module's assembly references no other module's implementation; `ELMOD002` inside an assembly | the compiler, a proposed analyzer |
| Imports | a UI module imports only its peers and its own files | npm `exports`, lint |
| Tokens | a UI module uses semantic colour classes only, never the palette | lint |
| Labels | every declared permission, error code and setting has a label | a suite test |

The install test replaces the field system's move test (copy a module's folder into a clone and build), which
there remains a thought experiment about boundaries. Here the move is real, made by a package reference, so the
test runs on every change.

## 11. Decisions this concept takes

Each is revisable, and each becomes an ADR when its step in section 13 begins.

1. **A packaged module owns its data:** its schema, its migrations, references by ID. Rejected: the
   application's shared data layer (a package cannot know an application's entities); a generic `jsonb` store per
   module (ADR-0008's option 2: untyped, no constraints).
2. **Versioned packages from one suite with one version.** Rejected: copying a module's source into each
   application (every fix forked); a repository and a version per module (a compatibility matrix the scale tier
   does not need); git submodules; loading modules at run time.
3. **The contract package holds every type that crosses the boundary, in both directions.** Rejected: required
   ports in the implementation package (whoever implements one would reference the implementation).
4. **A module's UI calls only its own operations, through a client that ships with it.** Rejected: one client
   per application (the UI package would depend on the host it lands in, and the suite could not build it).
5. **Elarion ships the UI components, headless and styled, and the shell contract; each application keeps the
   look** ([ADR-0112](decisions/0112-elarion-ships-ui-components.md), which amends ADR-0032). Rejected: a design
   system per suite (this page's first draft: it binds the framework's contracts once per suite and falls behind
   Elarion's releases); headless bindings only (every kit rebuilds the same table and form); each application
   copying components (modules could not rely on them).
6. **Styling only through semantic tokens, with shadcn/ui's names.** Rejected: stylesheets per module,
   CSS-in-JS, raw palette classes (each defeats theming); token names of Elarion's own (every application themed
   for shadcn/ui would have to map them).
7. **Composition at build time, one host per application.** Rejected: runtime plugin loading, module federation,
   a service per module.
8. **Extract on second demand.** A module enters the suite when a second application needs it, taken from the
   application where it proved itself. Rejected: writing suite modules ahead of demand.

## 12. What Elarion has today

| Part | State |
|---|---|
| Modules discovered in referenced assemblies through the Elarion manifest | shipped |
| A module enabled or disabled as one unit, across services and transports | shipped |
| `[ModuleContract]`; `ELMOD002` inside an assembly; `internal` across assemblies | shipped |
| A module owning entities by placing them in its namespace; a scoped `DbContext` per module | shipped |
| A schema per module with its own migration history, on the application's connection | not built: a schema is the connection's search path today, so a second schema means a second connection with its own history and no shared transaction |
| Required ports checked at start | not built |
| Extension points on the backend, declared and checked | partial: a module can consume `IEnumerable<T>` of a contract, and contract-set registration ([ADR-0070](decisions/0070-contract-set-registration.md)) composes a set for host assemblies; nothing declares or checks a point |
| A build error for a `DependsOn` module the host does not compose | to be checked |
| Permissions, flags, settings, constants and presets declared per module | shipped |
| Contribution kit, typed vocabulary, `when` clauses, static capabilities | shipped (ADR-0032) |
| A schema and a client of one module's operations | partial: one schema per host; a host that composes one module comes close, but the export has no module filter |
| Packaged UI modules discovered from dependencies, with their Tailwind sources | not built |
| A route factory convention | not built |
| Headless state and hooks for framework contracts | partial: the generated client has `canCall`, `rpcMethodInfo`, Zod schemas and decision helpers; there are no hooks (ADR-0112, proposed) |
| Styled components, `theme.css`, the shell contract | not built (ADR-0112, proposed); the Billing sample carries hand-copied shadcn/ui components |
| Composition document | not built |
| A template that creates a packaged module | not built |
| Shared kernel, reference host and shell | the suite's; not built |

## 13. The steps ahead, in order

Each Elarion step is one spec, with an ADR where it decides something hard to reverse, and one pull request or a
short stack.

1. **Choose the first module:** the one a second application needs now. Time tracking is the candidate.
2. **Make it a mini bounded context where it lives** (ADR-0008, stage 3): its entities inside it, its own
   schema, references by ID, contracts for what others use, its UI calling only its own operations. This needs
   nothing from Elarion but step 3.
3. **Elarion: a migration stream per module:** the schema in the module's model, the history in the module's
   schema, on the application's connection, ordered within the one migration plan.
4. **Elarion: `[RequiredPort]` and `[ExtensionPoint]`** in Abstractions, the start check that names the module
   and the port, and a host build error for a `DependsOn` module that is not composed.
5. **Elarion: a schema export filtered to one module**, the client generator's output per module (operations and
   vocabulary), and operation names prefixed by module, with a diagnostic when two modules collide.
6. **Elarion npm: the UI packages** ([ADR-0112](decisions/0112-elarion-ships-ui-components.md)), sample-first:
   the headless list and command state with its hooks and the shell contract, then `theme.css` and the
   primitives, then the data table, then the command form and the dialogs. Every application benefits from this
   step, packaged modules or not.
7. **Elarion npm: the Vite plugin** (packaged UI modules and their Tailwind sources) and the route factory
   convention.
8. **Suite: the repository**, the shared kernel, the reference host and shell, and the install test in CI.
9. **Move the module into the suite.** Its first application consumes the packages; the second adopts them.
   Every edit outside the module's folder that either application needs is a finding.
10. **Elarion: the composition document**, a template that creates a module passing the install test, and the
    analyzer that forbids a reference to another module's implementation.
11. **The next modules**, each on second demand.

Steps 3, 4, 5 and 6 are independent of each other. Step 7 needs step 5; step 9 needs steps 3 to 8.

## 14. Open questions

- Whether a module's UI entry is enough for pages that show several modules' data, or some page needs one query
  across modules. Recommended: the UI entry; a page that needs a join belongs to one module.
- How far TanStack Router's route types reach across a package boundary, and whether typed links into another
  module's pages are wanted. Recommended: links between modules through the shell contract, untyped.
- Whether an application overrides labels in its shell at run time or by merging bundles at build time.
- How the suite models people: a required port per module (`IWorkerDirectory`), or an upstream contacts module
  most modules depend on. Recommended: required ports until two modules need the same people, then the upstream
  module.
- Whether integration events between packaged modules need the outbox's target groups
  ([ADR-0062](decisions/0062-role-affine-routing-and-outbox-delivery.md)) or the default fan-out suffices.
- Whether a module's README is generated from its contract: its required ports, extension points, settings and
  permissions.

## Sources

- The module system of a large modular-monolith consuming application, as documented in its repository on
  2026-11-26: its layers, module contracts, extension points and their rules, the deletion test and the proposed
  move test. Not public; described here by its shape.
- [ADR-0007](decisions/0007-data-is-platform-module-as-plugin.md),
  [ADR-0008](decisions/0008-bounded-contexts-and-the-graduation-path.md),
  [ADR-0014](decisions/0014-cross-assembly-generator-composition.md),
  [ADR-0030](decisions/0030-client-capability-bootstrap.md),
  [ADR-0032](decisions/0032-frontend-contribution-model.md),
  [ADR-0070](decisions/0070-contract-set-registration.md),
  [ADR-0081](decisions/0081-one-migration-plan.md),
  [ADR-0112](decisions/0112-elarion-ships-ui-components.md).
- Published pages: [modules](concepts/modules.mdx), [solution structure](concepts/solution-structure.mdx),
  [cross-module communication](concepts/cross-module-communication.mdx),
  [frontend modules](concepts/frontend-modules/index.mdx), [migrations](capabilities/migrations.mdx),
  [source generation](concepts/source-generation.mdx).
- Eric Evans, [Domain-Driven Design Reference](https://www.domainlanguage.com/ddd/reference/): bounded context,
  context map, published language, open host service, anticorruption layer, shared kernel, generic subdomain.
- Alistair Cockburn, [Hexagonal architecture](https://alistair.cockburn.us/hexagonal-architecture/).
- Robert C. Martin, [The Clean Architecture](https://blog.cleancoder.com/uncle-bob/2012/08/13/the-clean-architecture.html).
- shadcn/ui, [Theming](https://ui.shadcn.com/docs/theming): semantic colour tokens as CSS variables.
- Tailwind CSS, [Theme variables](https://tailwindcss.com/docs/theme) and
  [Detecting classes in source files](https://tailwindcss.com/docs/detecting-classes-in-source-files) (`@source`).

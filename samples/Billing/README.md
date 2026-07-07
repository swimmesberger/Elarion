# Billing sample

A complete, **current-pattern** Elarion application — the compiled counterpart of the
[tutorial](../../docs/tutorial), which explains every piece of this code. It builds as part of
`Elarion.slnx` and follows the [recommended solution structure](../../docs/concepts/solution-structure):
a shared-kernel domain namespace, feature modules that own their handlers **and** schema configuration,
infrastructure for platform capabilities, and a thin host — orchestrated by **.NET Aspire** against a
real PostgreSQL database.

## Layout

| Project | Role |
| --- | --- |
| `Billing.Application` | The shared-kernel `Billing.Application.Domain` namespace (the `Client`/`Invoice`/`ActivityEntry` entities + enums, under no `[AppModule]`); the shared `Billing.Application.Persistence` layer — the database is application logic, so it holds each entity's `[EntityConfiguration]`, the concrete `BillingDbContext` (with the EF Core outbox and the framework audit-log table), whose schema is created from the model at startup with `EnsureCreated` — a sample simplification, so there are no migration files (a production app keeps EF migrations; the [tutorial](../../docs/tutorial) shows that setup); the `Core`, `Clients`, and `Invoicing` modules with their handlers, services, jobs, events, and resilience policy — including the Core module's `IActivityLog` `[ModuleContract]` (a domain history `Clients`/`Invoicing` record through, backed by the `ActivityEntry` entity) *and*, alongside it, the framework **audit trail** (`[Auditable]` handlers + `Elarion.Auditing.EntityFrameworkCore`) as the worked example of the two-being-different; the decorator pipeline; and the `[GenerateDbSets]`-annotated `BillingDbContext` (handlers inject it directly — no context interface). |
| `Billing.Infrastructure` | Intent-only mechanism adapters only: the SMTP email sender behind the module's port. The database is **not** here — it is application logic and lives in `Billing.Application.Persistence`. |
| `Billing.Api` | The ASP.NET Core host: `[GenerateModuleBootstrapper]`, JSON-RPC + MCP transports, the scheduler/resilience/cache runtimes, current-user, and OpenTelemetry. |
| `Billing.AppHost` | The .NET Aspire app host: provisions PostgreSQL, runs the API and the web frontend, and wires them together. |
| `Billing.Web` | A Vite + React 19 + Tailwind v4 + shadcn/ui + TanStack Query/Router frontend that calls the API through the **generated** JSON-RPC client (`rpc-schema.json` → `src/generated/`), structured as **frontend modules** with the ADR-0032 contribution model (see below). |

Entities live in a shared-kernel **namespace** and the whole persistence layer (configuration and the
`BillingDbContext`) in a shared `Persistence` namespace (both under no `[AppModule]`), not
separate projects; the Core module publishes its activity-log capability as an `IActivityLog`
`[ModuleContract]` (distinct from the framework audit trail — see below),
while `Infrastructure` holds only the intent-only SMTP adapter. See
[Solution structure](../../docs/concepts/solution-structure) for the reasoning and
for when each would graduate to its own assembly.

## What it demonstrates

- **Recommended structure** — shared-kernel entities reachable by every module without tripping
  ELMOD002, with their `[EntityConfiguration]` schema in a shared `Persistence` layer (configuration is
  part of the shared data layer, not feature-owned — the config drives the entity's `DbSet` and schema,
  and there is no separate entity marker).
- **Two kinds of "audit"** — the Core module's app-owned **activity log** (`IActivityLog`, a queryable
  `[ModuleContract]` domain history) sits beside the framework **audit trail** (`[Auditable]` on
  `CreateClient`/`CreateInvoice` + `[Audited]` on the entities + `Elarion.Auditing.EntityFrameworkCore`),
  which records a compliance entry per invocation — committed atomically, denials included, with automatic
  field capture. The worked example of the split drawn in the [audit-trail concept doc](../../docs/concepts/auditing).
- **Vertical-slice modules** — `Core` (always-on, `ICurrentUser` activity log), `Clients`, and `Invoicing`,
  each auto-registered and feature-gated; no hand-written `Add{Module}…()` calls.
- **The full cross-cutting machinery** — a one-line decorator pipeline (logging → transaction, attached
  by compile-time predicate) with declarative request validation auto-attached from the DataAnnotations
  on request DTOs (ADR-0027 — the same constraints flow into `rpc-schema.json`, OpenAPI, and the Zod
  client), per-user caching with tag invalidation, a durable integration-event outbox, deferred-retry
  background jobs with a resilience policy, and a nightly cron.
- **Every transport from one definition** — `[Handler]` handlers map onto one shared `HandlerDispatcher`,
  with JSON-RPC **and** MCP as thin adapters over it (each surface chosen via the handler's `Transports` flag).
- **Real persistence** — PostgreSQL via EF Core (snake_case tables), schema created on startup with `EnsureCreated`, all provisioned by Aspire.
- **The full chain** — a C# handler becomes a JSON-RPC method, an exported `rpc-schema.json`, a generated
  TypeScript client, and a typed React call, all driven by the same contract.
- **Frontend modules with the contribution model (ADR-0032)** — the review-isolation property extended to
  the web app; see the next section.

## Frontend modules

`Billing.Web/src` mirrors the backend's modular-monolith rule — *a module only touches its own code* — using the
contribution model of
[ADR-0032](../../docs/decisions/0032-frontend-contribution-model.md):

```
src/
  platform/                  # the frontend shared kernel (everything outside modules/ is shareable)
    contributions.ts         # the @swimmesberger/elarion-contributions kernel bound to the generated
                             #   capability vocabulary (typed `when` clauses)
    points.ts                # the shell's own extension points (sidebarItems)
    router.tsx, AppShell.tsx # root route + shell; the sidebar renders contributions, it hard-codes nothing
    session.ts               # fetches the ADR-0030 capability snapshot once at boot
  modules/
    clients/                 # manifest + route subtree + components/hooks; publishes the
                             #   clientRowActions extension point from its index.ts (the frontend [ModuleContract])
    invoicing/               # contributes its sidebar item AND a "new invoice" action into
                             #   clients' row-action point — a cross-module contribution via the token import
  app.tsx                    # the composition root: discovers modules/*/index.ts via import.meta.glob,
                             #   so adding a module is creating its folder — no central edits
```

The contribution machinery itself (`defineExtensionPoint`, `defineModule`, `contribute`, the `when`
evaluator, the registry, and the React `<ExtensionSlot>` bindings) is imported from
[`@swimmesberger/elarion-contributions`](../../src/elarion-contributions) — consumed here as a local
`file:` dependency, so build it once (`npm ci` in `src/elarion-contributions`, which builds via its
`prepare` script) before the web app's first build. What stays app-owned — and is meant to be copied
when starting a new app — is the vocabulary binding, the points, the shell, and the route composition.

- **Adding a module is creating its folder** — `app.tsx` discovers `modules/*/index.ts` with
  `import.meta.glob` (expanded at build time into static imports), so no central file changes. The
  tradeoff, noted in `app.tsx`: the glob-composed route tree types as `AnyRoute[]`, so `Link to` loses
  literal-union checking; a statically listed tuple is the fully-typed alternative.
- **Adding a sidebar item** touches only the owning module's `module.tsx` — the shell never changes.
- **Contributions are declarative manifests** (`defineModule` + `contribute`), filtered by `when` clauses
  (`{ module?, permission?, flag?, role? }`) against the `GET /session` snapshot and deterministically
  ordered. The `when` vocabulary is the generated literal unions, so a typo'd permission fails to compile.
- **Cross-module extension** works by importing the owning module's token from its public entry
  (`index.ts`) — Invoicing contributes a lazily-loaded dialog to the Clients table without either module
  seeing the other's internals. Module boundaries are held by convention here (imports only via
  `modules/{name}/index.ts`); workspace packages with `exports` maps enforce the same rule at scale.
- **Routes are module-owned TanStack Router subtrees** with lazy page components, so each module (and the
  contributed dialog) is its own chunk; the Invoicing route's `beforeLoad` uses the package's
  `redirectUnless` guard with the same `when` clause shape as its sidebar item, so a deep link into a
  hidden module bounces home. Hiding is UX only — every handler still enforces its
  `[RequirePermission]`/`[FeatureGate]` server-side.

The kernel's unit tests live with the package: `npm --prefix src/elarion-contributions test`.

## Run it

Requires a container runtime (Docker or Podman) for PostgreSQL and Node.js for the frontend. Install the
web dependencies once, then run the Aspire app host — it provisions the database, creates the schema,
starts the API and the Vite dev server (injecting the API URL as `VITE_API_URL`), and opens the Aspire
dashboard with traces and metrics:

```bash
npm ci --prefix src/elarion-contributions        # builds the contribution package the web app links
npm install --prefix samples/Billing/Billing.Web
dotnet run --project samples/Billing/Billing.AppHost
```

The dashboard lists the **api** and **web** resources with their URLs. In Development the host stamps a
`dev-user` principal so the endpoints are callable without an external identity provider. Open the web
URL to use the UI, or call a method directly over JSON-RPC (substitute the API URL the dashboard shows):

```bash
curl -s http://localhost:5000/rpc \
  -H 'content-type: application/json' \
  -d '{"jsonrpc":"2.0","id":1,"method":"clients.create",
       "params":{"name":"Acme Inc.","email":"billing@acme.test"}}'
```

A success returns `{ "id": "…", "number": "C-000001" }`; a duplicate email returns the JSON-RPC error
mapped from `AppError.Conflict`. The MCP server is live at `/mcp`, exposing `clients.create`,
`invoices.create`, and the rest as tools.

## Regenerating the contract

`rpc-schema.json` and the TypeScript client under `Billing.Web/src/generated/` are committed. Regenerate them
when a handler's request/response shape changes:

```bash
# 1. Re-export the schema from the built host
dotnet build samples/Billing/Billing.Api
dotnet run --project src/Elarion.AspNetCore.SchemaGeneration.Tool -- \
  --assembly samples/Billing/Billing.Api/bin/Debug/net10.0/Billing.Api.dll \
  --output samples/Billing/rpc-schema.json

# 2. Regenerate the typed client
npm --prefix samples/Billing/Billing.Web run gen:rpc
```

> The tutorial wires this as a build-time step (`Elarion.AspNetCore.SchemaGeneration` on the host); the
> sample commits the generated artifacts and regenerates on demand to keep the solution build fast and
> Docker-free. See [Build the frontend](../../docs/tutorial/frontend).

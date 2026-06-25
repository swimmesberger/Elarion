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
| `Billing.Application` | The shared-kernel `Billing.Application.Domain` namespace (the `Client`/`Invoice` entities + enums, under no `[AppModule]`); the shared `Billing.Application.Persistence` layer — the database is application logic, so it holds each entity's `[EntityConfiguration]`, the concrete `BillingDbContext` (with the EF Core outbox), the design-time factory, and the EF migrations; the `Core`, `Clients`, and `Invoicing` modules with their handlers, validators, services, jobs, events, and resilience policy; the decorator pipeline; and the `[GenerateDbSets] IAppDbContext`. |
| `Billing.Infrastructure` | Intent-only mechanism adapters only: the SMTP email sender behind the module's port. The database is **not** here — it is application logic and lives in `Billing.Application.Persistence`. |
| `Billing.Api` | The ASP.NET Core host: `[GenerateModuleBootstrapper]`, JSON-RPC + MCP transports, the scheduler/resilience/cache runtimes, current-user, and OpenTelemetry. |
| `Billing.AppHost` | The .NET Aspire app host: provisions PostgreSQL, runs the API and the web frontend, and wires them together. |
| `web` | A Vite + React 19 + Tailwind v4 + shadcn/ui + TanStack Query frontend that calls the API through the **generated** JSON-RPC client (`rpc-schema.json` → `src/generated/`). |

Entities live in a shared-kernel **namespace** and the whole persistence layer (configuration, the
`BillingDbContext`, and migrations) in a shared `Persistence` namespace (both under no `[AppModule]`), not
separate projects; `Infrastructure` holds only the intent-only SMTP adapter. See
[Solution structure](../../docs/concepts/solution-structure) for the reasoning and
for when each would graduate to its own assembly.

## What it demonstrates

- **Recommended structure** — shared-kernel entities reachable by every module without tripping
  ELMOD002, with their `[EntityConfiguration]` schema in a shared `Persistence` layer (configuration is
  part of the shared data layer, not feature-owned — the config drives the entity's `DbSet` and schema,
  and there is no separate entity marker).
- **Vertical-slice modules** — `Core` (always-on, `ICurrentUser` audit trail), `Clients`, and `Invoicing`,
  each auto-registered and feature-gated; no hand-written `Add{Module}…()` calls.
- **The full cross-cutting machinery** — a one-line decorator pipeline (logging → validation →
  transaction, attached by compile-time predicate), per-user caching with tag invalidation, a durable
  integration-event outbox, deferred-retry background jobs with a resilience policy, and a nightly cron.
- **Every transport from one definition** — `[RpcMethod]` handlers served over JSON-RPC **and** MCP.
- **Real persistence** — PostgreSQL via EF Core, migrations applied on startup, all provisioned by Aspire.
- **The full chain** — a C# handler becomes a JSON-RPC method, an exported `rpc-schema.json`, a generated
  TypeScript client, and a typed React call, all driven by the same contract.

## Run it

Requires a container runtime (Docker or Podman) for PostgreSQL and Node.js for the frontend. Install the
web dependencies once, then run the Aspire app host — it provisions the database, applies migrations,
starts the API and the Vite dev server (injecting the API URL as `VITE_API_URL`), and opens the Aspire
dashboard with traces and metrics:

```bash
npm install --prefix samples/Billing/web
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

`rpc-schema.json` and the TypeScript client under `web/src/generated/` are committed. Regenerate them
when a handler's request/response shape changes:

```bash
# 1. Re-export the schema from the built host
dotnet build samples/Billing/Billing.Api
dotnet run --project src/Elarion.AspNetCore.SchemaGeneration.Tool -- \
  --assembly samples/Billing/Billing.Api/bin/Debug/net10.0/Billing.Api.dll \
  --output samples/Billing/rpc-schema.json

# 2. Regenerate the typed client
npm --prefix samples/Billing/web run gen:rpc
```

> The tutorial wires this as a build-time step (`Elarion.AspNetCore.SchemaGeneration` on the host); the
> sample commits the generated artifacts and regenerates on demand to keep the solution build fast and
> Docker-free. See [Build the frontend](../../docs/tutorial/frontend).

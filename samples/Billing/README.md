# Billing sample

A small, **current-pattern** end-to-end Elarion app, kept in sync with the framework (it builds as part
of `Elarion.slnx`). It is the canonical reference for how a modern Elarion solution is wired — if you
are looking at an older example (for instance the external `swimmesberger/Swerp` repo, which predates
the auto-registration generator), prefer this.

It mirrors the shape of the [tutorial](../../docs/tutorial) as a compiled slice: a separate domain
assembly, a feature module with handlers, and a JSON-RPC host. It uses the **in-memory** EF Core
provider so it runs with no external database.

## Layout

| Project | Role |
| --- | --- |
| `Billing.Domain` | `[DbEntity]` entities in a **separate assembly**. References only `Elarion.EntityFrameworkCore`; its generator emits the entity manifest the Application context reads cross-assembly. |
| `Billing.Application` | The `Clients` `[AppModule]`, its handlers (`clients.create`, `clients.get`), the `[GenerateDbSets] IAppDbContext`, and the concrete `AppDbContext`. |
| `Billing.Api` | The ASP.NET Core host: `[GenerateModuleBootstrapper]` + JSON-RPC, seeded with one client. |

## What it demonstrates

- **Minimal module classes** — `[AppModule]` + a JSON resolver. No hand-written `AddClientsHandlers()`;
  the generated `ConfigureDefaultServices` registers everything. (Copying the old hand-registration
  pattern onto current packages double-registers.)
- **Cross-assembly `[DbEntity]` discovery** — entities live in `Billing.Domain`, the context in
  `Billing.Application`, and `DbSet<Client>` is generated on `IAppDbContext` from the referenced
  assembly's manifest.
- **Handlers over JSON-RPC** — `[RpcMethod]` handlers returning `Result<T>`, dispatched through the
  generated module bootstrapper.

## Run it

```bash
dotnet run --project samples/Billing/Billing.Api
```

Then call a method (default JSON-RPC endpoint is `/rpc`):

```bash
curl -s http://localhost:5000/rpc \
  -H 'content-type: application/json' \
  -d '{"jsonrpc":"2.0","id":1,"method":"clients.get",
       "params":{"id":"00000000-0000-0000-0000-000000000001"}}'
```

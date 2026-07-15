<div align="center">

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/swimmesberger/Elarion/main/docs/public/brand/elarion-banner-transparent-dark.svg">
  <source media="(prefers-color-scheme: light)" srcset="https://raw.githubusercontent.com/swimmesberger/Elarion/main/docs/public/brand/elarion-banner-transparent-light.svg">
  <img src="https://raw.githubusercontent.com/swimmesberger/Elarion/main/docs/public/brand/elarion-banner-transparent-light.svg" width="640" alt="Elarion — Application framework for .NET">
</picture>

**Module-based handler pipelines, compile-time registration, JSON-RPC hosting, MCP tools for AI agents, and scheduled jobs.**

Declare intent next to your code; let source generators do the wiring. No runtime reflection scanning.

**AI-native by design:** the same handlers that power your API are exposed to AI agents as [MCP](https://modelcontextprotocol.io) tools — generated from your code at compile time, with no separate tool definitions or duplicated schemas.

[![CI](https://github.com/swimmesberger/Elarion/actions/workflows/ci.yml/badge.svg)](https://github.com/swimmesberger/Elarion/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Elarion.svg?logo=nuget&label=NuGet)](https://www.nuget.org/packages/Elarion)
[![npm](https://img.shields.io/npm/v/%40swimmesberger%2Felarion-jsonrpc-client-generator.svg?logo=npm&label=npm)](https://www.npmjs.com/package/@swimmesberger/elarion-jsonrpc-client-generator)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](https://github.com/swimmesberger/Elarion/blob/main/LICENSE)

</div>

---

Elarion's central idea is simple: **your application assemblies define modules and handlers; your
host assembly only wires infrastructure, transport, and deployment.** Everything that can be
discovered from your code — handlers, services, scheduled jobs, RPC methods, validation metadata, EF Core
`DbSet`s — is emitted by source generators at compile time instead of scanned by reflection at
startup.

```csharp
[Handler("clients.get")]
public sealed class GetClient(AppDbContext db)
    : IHandler<GetClient.Query, Result<GetClient.Response>> {
    public sealed record Query(Guid Id);
    public sealed record Response(Guid Id, string Name);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var client = await db.Clients
            .Where(c => c.Id == query.Id)
            .Select(c => new Response(c.Id, c.Name))
            .FirstOrDefaultAsync(ct);

        return client is null
            ? AppError.NotFound($"Client {query.Id} was not found.")
            : client;
    }
}
```

That one class is a use case, a registered service, a JSON-RPC method, an **MCP tool for AI agents**,
and (optionally) a schema-exported TypeScript contract — with **no entry added to any `Program.cs`
registration list.** The operation name is optional: `[Handler]` alone infers `{module}.{operation}` by
convention (here `clients.get`), while an explicit name is recommended for stable public/wire contracts.

## Why Elarion

- **Compile-time, not reflection** — handlers, services, modules, RPC maps, validation metadata, and
  scheduled jobs become ordinary DI code. Startup is deterministic and AOT-friendly; missing wiring
  is a build error, not a runtime surprise.
- **Modules own their surface** — a module is a namespace plus an `[AppModule]` marker. Add a handler
  under it and the module publishes it automatically.
- **Transport-neutral results** — handlers return `Result<T>` with a transport-agnostic `AppError`;
  the host maps failures to JSON-RPC, HTTP, or anything else.
- **One bus, many surfaces** — every handler maps onto a single transport-neutral `HandlerDispatcher`
  (`Elarion.Abstractions.Dispatch`); JSON-RPC and MCP are thin **adapters** over that one bus, each serving
  only the operations flagged for it. Define a handler once and choose its surfaces with the `Transports`
  flag.
- **End-to-end JSON-RPC** — mark a handler with `[Handler]`, export a schema at build time, and
  generate a typed TypeScript + Zod client.
- **Validation that reaches the contract** — DataAnnotations on the request DTO are enforced by a
  generated pipeline decorator under every transport **and** exported to the JSON-RPC schema, OpenAPI,
  MCP tool schemas, and the Zod client (which pre-validates requests). One declaration, four surfaces;
  business rules stay in the handler, inside the transaction (ADR-0027).
- **AI-native, no extra code** — expose the same `[Handler]` handlers to AI agents as an
  [MCP](https://modelcontextprotocol.io) server. MCP is an adapter over the shared `HandlerDispatcher`, so a
  tool is just a handler flagged `HandlerTransports.Mcp`. Tool names, descriptions, and input schemas are
  generated from your handlers and `[Description]` attributes at compile time — no separate tool layer, no
  duplicated schemas, no runtime reflection. Choose a handler's transports with
  `[Handler(Transports = …)]` (JSON-RPC, MCP, or both) and rename a tool with `[McpHandler]`.
- **Feature flags & variants** — gate any handler with `[FeatureGate]`; a generated decorator
  evaluates the flag before the handler runs and a closed gate returns **404 Not Found**, so a
  disabled feature is indistinguishable from one that doesn't exist (the name is never leaked).
  `[FeatureVariant]` swaps a `[Service]` implementation per user behind a flag. Both work identically
  under JSON-RPC, MCP, and HTTP, against any [OpenFeature](https://openfeature.dev) provider.
- **Exactly-once command handlers** — mark a handler `[Idempotent]` and the idempotency key commits
  atomically with the business writes, a retried request replays the stored result, a concurrent
  duplicate returns **409**, and there is no distributed lock.
- **In-process scheduling** — source-generated scheduled jobs with explicit overlap, misfire, and
  resilience policies.
- **Observable by default** — JSON-RPC, scheduling, caching, and resilience emit
  OpenTelemetry-compatible traces and metrics through `System.Diagnostics`, with no SDK dependency
  forced on you, and every handler span and log scope is enriched with the calling user across all
  transports (extensible via `IHandlerContextEnricher`).

## Install

```xml
<!-- Application library — the source generator ships inside the Elarion package -->
<ItemGroup>
  <PackageReference Include="Elarion" Version="0.2.5" />
</ItemGroup>
```

```csharp
// Turn the generators on, once per assembly
[assembly: UseElarion]
```

The [Quickstart](https://elarion.wimmesberger.dev/docs/getting-started/quickstart/) builds a module, a handler, and a working
JSON-RPC endpoint end to end.

## Package families

The README shows the shape of the framework; the
[canonical package reference](https://elarion.wimmesberger.dev/docs/reference/packages/) lists every
public package, grouped by capability, with the reason to add each one.

| Family | Start with | Add when needed |
| --- | --- | --- |
| Application model | `Elarion` | `Validation`, `Resilience`, caching, and feature-flag providers |
| Hosting and transports | `Elarion.JsonRpc`, `Elarion.AspNetCore` | OpenAPI, MCP, schema generation, Identity, or a custom transport |
| Persistence | `Elarion.EntityFrameworkCore` | Unit of work, paging, bulk operations, authorization, idempotency, auditing, scheduling, and coordination |
| NativeAOT SQL | `Elarion.Sql`, `Elarion.Migrations` | PostgreSQL or SQLite migrations for an EF-free host |
| Events and live clients | `Elarion.Messaging.Outbox`, `Elarion.ClientEvents` | PostgreSQL fan-out and SSE transport |
| Live state and device links | `Elarion.Actors`, `Elarion.Connections` | PostgreSQL actor state/home, WebSocket/TCP adapters, simulation, and device identity |
| Blob storage | `Elarion.Blobs` | PostgreSQL or Azure storage plus direct HTTP or tus upload transports |
| Runtime settings | `Elarion.Settings` | EF persistence, configuration reload, and PostgreSQL cross-node notifications |
| Frontend tooling | `@swimmesberger/elarion-jsonrpc-client-generator` | Typed frontend contributions and framework bindings |

Package names follow capability boundaries: neutral contracts and runtimes do not pull provider or host
dependencies; `.PostgreSql`, `.EntityFrameworkCore`, `.AspNetCore`, and other suffixes make those choices
explicit. Reference `Elarion` directly in every assembly that needs source generation because analyzer
assets are not transitive.

<details>
<summary>Where did the old package table go?</summary>

It was a second, shorter copy of the package reference and regularly lagged behind new features. The
grouped documentation page is now the one maintained inventory; this overview stays intentionally small.

</details>


## Documentation

Full guides live at [elarion.wimmesberger.dev](https://elarion.wimmesberger.dev/docs/) and in [`docs/`](https://github.com/swimmesberger/Elarion/tree/main/docs):

- **[Introduction](https://elarion.wimmesberger.dev/docs/)** · **[Why Elarion](https://elarion.wimmesberger.dev/docs/why-elarion/)** · **[Installation](https://elarion.wimmesberger.dev/docs/getting-started/installation/)** · **[Quickstart](https://elarion.wimmesberger.dev/docs/getting-started/quickstart/)**
- **Concepts** — [source generation](https://elarion.wimmesberger.dev/docs/concepts/source-generation/), [handlers](https://elarion.wimmesberger.dev/docs/concepts/handlers/), [results & errors](https://elarion.wimmesberger.dev/docs/concepts/results-and-errors/), [modules](https://elarion.wimmesberger.dev/docs/concepts/modules/), [services](https://elarion.wimmesberger.dev/docs/concepts/services/), [validation](https://elarion.wimmesberger.dev/docs/concepts/validation/), [pipelines](https://elarion.wimmesberger.dev/docs/concepts/decorator-pipelines/), [cross-module communication](https://elarion.wimmesberger.dev/docs/concepts/cross-module-communication/)
- **Capabilities** — [hosting](https://elarion.wimmesberger.dev/docs/capabilities/hosting/), [HTTP endpoints](https://elarion.wimmesberger.dev/docs/capabilities/transports/http-endpoints/), [JSON-RPC](https://elarion.wimmesberger.dev/docs/capabilities/transports/json-rpc/), [MCP server](https://elarion.wimmesberger.dev/docs/capabilities/transports/mcp/), [authorization](https://elarion.wimmesberger.dev/docs/concepts/authorization/), [feature flags](https://elarion.wimmesberger.dev/docs/capabilities/feature-flags/), [identity](https://elarion.wimmesberger.dev/docs/capabilities/identity/), [scheduling](https://elarion.wimmesberger.dev/docs/capabilities/scheduling/), [resilience](https://elarion.wimmesberger.dev/docs/capabilities/resilience/), [events & messaging](https://elarion.wimmesberger.dev/docs/capabilities/events/), [EF Core](https://elarion.wimmesberger.dev/docs/capabilities/entity-framework/), [bulk operations](https://elarion.wimmesberger.dev/docs/capabilities/bulk-operations/), [caching](https://elarion.wimmesberger.dev/docs/capabilities/caching/), [current user](https://elarion.wimmesberger.dev/docs/capabilities/current-user/), [blob storage](https://elarion.wimmesberger.dev/docs/capabilities/blob-storage/), [telemetry](https://elarion.wimmesberger.dev/docs/capabilities/telemetry/)
- **Reference** — [packages](https://elarion.wimmesberger.dev/docs/reference/packages/), [configuration](https://elarion.wimmesberger.dev/docs/reference/configuration/), [troubleshooting](https://elarion.wimmesberger.dev/docs/reference/troubleshooting/)

## Requirements

- .NET 10 SDK or later
- ASP.NET Core for the JSON-RPC HTTP transport
- Node.js 18+ for the TypeScript client generator

## Contributing

Issues and pull requests are welcome. See [CONTRIBUTING.md](https://github.com/swimmesberger/Elarion/blob/main/CONTRIBUTING.md) for the development
workflow, validation commands, architecture boundaries, and the publishing process. By participating
you agree to the [Code of Conduct](https://github.com/swimmesberger/Elarion/blob/main/CODE_OF_CONDUCT.md).

## Security

Please report vulnerabilities privately — see the [security policy](https://github.com/swimmesberger/Elarion/blob/main/SECURITY.md).

## License

Elarion is licensed under the [Apache License 2.0](https://github.com/swimmesberger/Elarion/blob/main/LICENSE).

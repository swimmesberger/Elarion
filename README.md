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
discovered from your code — handlers, validators, services, scheduled jobs, RPC methods, EF Core
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

- **Compile-time, not reflection** — handlers, services, validators, modules, RPC maps, and
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
  forced on you.

## Install

```xml
<!-- Application library — the source generator ships inside the Elarion package -->
<ItemGroup>
  <PackageReference Include="Elarion" Version="0.2.2" />
</ItemGroup>
```

```csharp
// Turn the generators on, once per assembly
[assembly: UseElarion]
```

The [Quickstart](https://elarion.wimmesberger.dev/docs/getting-started/quickstart/) builds a module, a handler, and a working
JSON-RPC endpoint end to end.

## Packages

| Package | Purpose |
| --- | --- |
| [`Elarion.Abstractions`](https://github.com/swimmesberger/Elarion/tree/main/src/Elarion.Abstractions) | Attributes and contracts: `[AppModule]`, `[Service]`, `[ScheduledJob]`, `IHandler<,>`, `Result<T>`, `AppError`. |
| [`Elarion`](https://github.com/swimmesberger/Elarion/tree/main/src/Elarion) | Runtime primitives: decorators, the in-memory scheduler, current-user access, and the default authorizer. Depends only on `Microsoft.Extensions.*` abstractions (ADR-0017) — caching and resilience moved to their own opt-in packages. Bundles the Elarion source generator (handlers, services, validators, modules, RPC/HTTP/MCP maps, resilience policies, scheduled jobs, event consumers). |
| [`Elarion.Caching`](https://github.com/swimmesberger/Elarion/tree/main/src/Elarion.Caching) | `HybridCache`-backed default `IHandlerCache` for handler result caching (`AddElarionHandlerCaching()`). Required by `[Cacheable]`/`[CacheInvalidate]` (extracted from core, ADR-0017). |
| [`Elarion.Caching.PostgreSql`](https://github.com/swimmesberger/Elarion/tree/main/src/Elarion.Caching.PostgreSql) | The recommended L2 distributed cache for most apps: a PostgreSQL `UNLOGGED` table behind `HybridCache` (`AddElarionPostgreSqlHandlerCaching(connectionString)`). Reuse your Postgres instead of operating a separate Redis (Redis still wins for very high-throughput or multi-region caches; ADR-0020). |
| [`Elarion.Resilience`](https://github.com/swimmesberger/Elarion/tree/main/src/Elarion.Resilience) | Microsoft/Polly-backed default `IResiliencePipelineRunner` (`AddMicrosoftResilienceRuntime()`). Required by `[Resilient]` handlers and deferred scheduler retries (extracted from core, ADR-0017). |
| [`Elarion.FeatureFlags.OpenFeature`](https://github.com/swimmesberger/Elarion/tree/main/src/Elarion.FeatureFlags.OpenFeature) | Default `IFeatureFlagService` over OpenFeature for `[FeatureGate]`/`[FeatureVariant]`; maps `ICurrentUser` into the evaluation context off-HTTP (`AddElarionOpenFeature()`). Bring your own OpenFeature provider. |
| [`Elarion.FeatureFlags.FeatureManagement`](https://github.com/swimmesberger/Elarion/tree/main/src/Elarion.FeatureFlags.FeatureManagement) | Batteries-included config-driven flags via the Microsoft.FeatureManagement OpenFeature provider (`AddElarionFeatureManagement(configuration)`). |
| [`Elarion.Blobs`](https://github.com/swimmesberger/Elarion/tree/main/src/Elarion.Blobs) | Provider-neutral blob storage contracts and DTOs. |
| [`Elarion.Blobs.PostgreSql`](https://github.com/swimmesberger/Elarion/tree/main/src/Elarion.Blobs.PostgreSql) | PostgreSQL-backed blob storage with EF Core model configuration and Npgsql content I/O, plus the pending/commit/TTL upload lifecycle and garbage collector. |
| [`Elarion.Blobs.AspNetCore`](https://github.com/swimmesberger/Elarion/tree/main/src/Elarion.Blobs.AspNetCore) | Minimal direct blob-upload endpoint (`MapElarionBlobUploads`) for FilePond and plain `fetch`/`<form>` clients. |
| [`Elarion.Blobs.Tus`](https://github.com/swimmesberger/Elarion/tree/main/src/Elarion.Blobs.Tus) | tus 1.0 resumable-upload transport (`MapElarionTus`) — the open, resumable, large-file protocol supported natively by Uppy and `tus-js-client`. |
| [`Elarion.Blobs.Tus.PostgreSql`](https://github.com/swimmesberger/Elarion/tree/main/src/Elarion.Blobs.Tus.PostgreSql) | Durable PostgreSQL staging for tus uploads so in-progress uploads survive restarts. |
| [`Elarion.Messaging.InMemory`](https://github.com/swimmesberger/Elarion/tree/main/src/Elarion.Messaging.InMemory) | In-memory integration-event bus (best-effort, commit-gated by the EF Core transaction). |
| [`Elarion.Messaging.Outbox`](https://github.com/swimmesberger/Elarion/tree/main/src/Elarion.Messaging.Outbox) | EF Core transactional outbox: a durable, at-least-once integration-event bus with a polling delivery worker. |
| [`Elarion.Paging`](https://github.com/swimmesberger/Elarion/tree/main/src/Elarion.Paging) | Keyset (cursor) and offset pagination primitives, opaque cursor codec, and `IQueryable` paging extensions. |
| [`Elarion.JsonRpc`](https://github.com/swimmesberger/Elarion/tree/main/src/Elarion.JsonRpc) | Transport-neutral JSON-RPC dispatcher, envelopes, telemetry, and schema export. |
| [`Elarion.AspNetCore`](https://github.com/swimmesberger/Elarion/tree/main/src/Elarion.AspNetCore) | ASP.NET Core JSON-RPC endpoint mapping, `[HttpEndpoint]` minimal-API mapping, batch execution, and current-user middleware. |
| [`Elarion.AspNetCore.Mcp`](https://github.com/swimmesberger/Elarion/tree/main/src/Elarion.AspNetCore.Mcp) | Exposes your handlers as a Model Context Protocol (MCP) server for AI agents, over Streamable HTTP — an MCP adapter over the shared `HandlerDispatcher`. |
| [`Elarion.AspNetCore.OpenApi`](https://github.com/swimmesberger/Elarion/tree/main/src/Elarion.AspNetCore.OpenApi) | Opt-in OpenAPI document generation for `[HttpEndpoint]` handlers (`AddElarionOpenApi()`) over `Microsoft.AspNetCore.OpenApi`: canonical-JSON schema wiring, module tags, clean operation ids, and the `Idempotency-Key` contract — REST parity with the JSON-RPC schema and client. |
| [`Elarion.AspNetCore.SchemaGeneration`](https://github.com/swimmesberger/Elarion/tree/main/src/Elarion.AspNetCore.SchemaGeneration) | MSBuild package that exports `rpc-schema.json` during `dotnet build`. |
| [`Elarion.EntityFrameworkCore`](https://github.com/swimmesberger/Elarion/tree/main/src/Elarion.EntityFrameworkCore) | Marker attributes for generated `DbSet`s and entity inclusion. Bundles the EF Core source generator (`DbSet` properties, entity configuration, keyset pagination). |
| [`Elarion.EntityFrameworkCore.Identity`](https://github.com/swimmesberger/Elarion/tree/main/src/Elarion.EntityFrameworkCore.Identity) | The web-free ASP.NET Core Identity *model*: `[GenerateElarionIdentity]` + `ApplyElarionIdentity` compose a snake_case Identity model onto a plain `DbContext` (no ASP.NET `FrameworkReference`). Bundles its model generator. |
| [`Elarion.AspNetCore.Identity`](https://github.com/swimmesberger/Elarion/tree/main/src/Elarion.AspNetCore.Identity) | Optional ASP.NET Core Identity host wiring: `AddElarionIdentity<…>` (`AddIdentity` + EF stores), the Identity `ICurrentUser` claim mapping, and the transport-neutral authorizer. |
| [`Elarion.EntityFrameworkCore.UnitOfWork`](https://github.com/swimmesberger/Elarion/tree/main/src/Elarion.EntityFrameworkCore.UnitOfWork) | Framework-owned EF transaction / unit-of-work boundary: `EfUnitOfWork<TDbContext>` over the EF-free `IUnitOfWork` seam (PostgreSQL `SET LOCAL lock_timeout` + savepoints), `AddElarionUnitOfWork<TDbContext>()`. |
| [`Elarion.Idempotency.EntityFrameworkCore`](https://github.com/swimmesberger/Elarion/tree/main/src/Elarion.Idempotency.EntityFrameworkCore) | Durable exactly-once key store: `EfCoreIdempotencyStore<TDbContext>` (`INSERT … ON CONFLICT DO NOTHING` inside the caller's transaction, 409 on `lock_timeout`, success-only replay) plus a retention purge worker, `AddElarionIdempotencyEntityFrameworkCore<TDbContext>()`. |
| [`Elarion.Authorization.EntityFrameworkCore`](https://github.com/swimmesberger/Elarion/tree/main/src/Elarion.Authorization.EntityFrameworkCore) | Data-level authorization grants backend: a `ResourceGrant` table (user/role shares), `IResourceGrantStore`, and the grants-backed `IResourceAuthorizer`. |
| [`Elarion.Settings`](https://github.com/swimmesberger/Elarion/tree/main/src/Elarion.Settings) | Runtime-changeable key/value settings: the swappable `ISettingsStore` sink (global and per-user scopes, in-process change notification) plus the AOT-clean `ISettingsManager` consumer. |
| [`Elarion.Settings.EntityFrameworkCore`](https://github.com/swimmesberger/Elarion/tree/main/src/Elarion.Settings.EntityFrameworkCore) | EF Core database backend for settings: a relational, provider-neutral `ISettingsStore` with optimistic concurrency. |
| [`Elarion.Settings.Configuration`](https://github.com/swimmesberger/Elarion/tree/main/src/Elarion.Settings.Configuration) | `IConfiguration`/`IOptionsMonitor` adapter over the `Global` settings scope, with `IChangeToken` reload so config consumers pick up runtime changes. |
| [`@swimmesberger/elarion-jsonrpc-client-generator`](https://github.com/swimmesberger/Elarion/tree/main/src/elarion-jsonrpc-client-generator) | TypeScript CLI that turns a schema export into method contracts, Zod schemas, and a fetch client. |

## Documentation

Full guides live at [elarion.wimmesberger.dev](https://elarion.wimmesberger.dev/docs/) and in [`docs/`](https://github.com/swimmesberger/Elarion/tree/main/docs):

- **[Introduction](https://elarion.wimmesberger.dev/docs/)** · **[Why Elarion](https://elarion.wimmesberger.dev/docs/why-elarion/)** · **[Installation](https://elarion.wimmesberger.dev/docs/getting-started/installation/)** · **[Quickstart](https://elarion.wimmesberger.dev/docs/getting-started/quickstart/)**
- **Concepts** — [source generation](https://elarion.wimmesberger.dev/docs/concepts/source-generation/), [handlers](https://elarion.wimmesberger.dev/docs/concepts/handlers/), [results & errors](https://elarion.wimmesberger.dev/docs/concepts/results-and-errors/), [modules](https://elarion.wimmesberger.dev/docs/concepts/modules/), [services](https://elarion.wimmesberger.dev/docs/concepts/services/), [validators](https://elarion.wimmesberger.dev/docs/concepts/validators/), [pipelines](https://elarion.wimmesberger.dev/docs/concepts/decorator-pipelines/), [cross-module communication](https://elarion.wimmesberger.dev/docs/concepts/cross-module-communication/)
- **Capabilities** — [hosting](https://elarion.wimmesberger.dev/docs/capabilities/hosting/), [HTTP endpoints](https://elarion.wimmesberger.dev/docs/capabilities/transports/http-endpoints/), [JSON-RPC](https://elarion.wimmesberger.dev/docs/capabilities/transports/json-rpc/), [MCP server](https://elarion.wimmesberger.dev/docs/capabilities/transports/mcp/), [authorization](https://elarion.wimmesberger.dev/docs/concepts/authorization/), [feature flags](https://elarion.wimmesberger.dev/docs/capabilities/feature-flags/), [identity](https://elarion.wimmesberger.dev/docs/capabilities/identity/), [scheduling](https://elarion.wimmesberger.dev/docs/capabilities/scheduling/), [resilience](https://elarion.wimmesberger.dev/docs/capabilities/resilience/), [events & messaging](https://elarion.wimmesberger.dev/docs/capabilities/events/), [EF Core](https://elarion.wimmesberger.dev/docs/capabilities/entity-framework/), [caching](https://elarion.wimmesberger.dev/docs/capabilities/caching/), [current user](https://elarion.wimmesberger.dev/docs/capabilities/current-user/), [blob storage](https://elarion.wimmesberger.dev/docs/capabilities/blob-storage/), [telemetry](https://elarion.wimmesberger.dev/docs/capabilities/telemetry/)
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

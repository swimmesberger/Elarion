<div align="center">

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/public/brand/elarion-banner-transparent-dark.svg">
  <source media="(prefers-color-scheme: light)" srcset="docs/public/brand/elarion-banner-transparent-light.svg">
  <img src="docs/public/brand/elarion-banner-transparent-light.svg" width="640" alt="Elarion — Application framework for .NET">
</picture>

**Module-based handler pipelines, compile-time registration, JSON-RPC hosting, MCP tools for AI agents, and scheduled jobs.**

Declare intent next to your code; let source generators do the wiring. No runtime reflection scanning.

**AI-native by design:** the same handlers that power your API are exposed to AI agents as [MCP](https://modelcontextprotocol.io) tools — generated from your code at compile time, with no separate tool definitions or duplicated schemas.

[![CI](https://github.com/swimmesberger/Elarion/actions/workflows/ci.yml/badge.svg)](https://github.com/swimmesberger/Elarion/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Elarion.svg?logo=nuget&label=NuGet)](https://www.nuget.org/packages/Elarion)
[![npm](https://img.shields.io/npm/v/%40swimmesberger%2Felarion-jsonrpc-client-generator.svg?logo=npm&label=npm)](https://www.npmjs.com/package/@swimmesberger/elarion-jsonrpc-client-generator)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)

</div>

---

Elarion's central idea is simple: **your application assemblies define modules and handlers; your
host assembly only wires infrastructure, transport, and deployment.** Everything that can be
discovered from your code — handlers, validators, services, scheduled jobs, RPC methods, EF Core
`DbSet`s — is emitted by source generators at compile time instead of scanned by reflection at
startup.

```csharp
[RpcMethod("clients.get")]
public sealed class GetClient(IAppDbContext db)
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
registration list.**

## Why Elarion

- **Compile-time, not reflection** — handlers, services, validators, modules, RPC maps, and
  scheduled jobs become ordinary DI code. Startup is deterministic and AOT-friendly; missing wiring
  is a build error, not a runtime surprise.
- **Modules own their surface** — a module is a namespace plus an `[AppModule]` marker. Add a handler
  under it and the module publishes it automatically.
- **Transport-neutral results** — handlers return `Result<T>` with a transport-agnostic `AppError`;
  the host maps failures to JSON-RPC, HTTP, or anything else.
- **End-to-end JSON-RPC** — mark a handler with `[RpcMethod]`, export a schema at build time, and
  generate a typed TypeScript + Zod client.
- **AI-native, no extra code** — expose the same `[RpcMethod]` handlers to AI agents as an
  [MCP](https://modelcontextprotocol.io) server, an independent transport with its own dispatcher. Tool
  names, descriptions, and input schemas are generated from your handlers and `[Description]` attributes at
  compile time — no separate tool layer, no duplicated schemas, no runtime reflection. Choose a handler's
  transports with `[RpcMethod(Transports = …)]` (JSON-RPC, MCP, or both) and rename a tool with `[McpMethod]`.
- **In-process scheduling** — source-generated scheduled jobs with explicit overlap, misfire, and
  resilience policies.
- **Observable by default** — JSON-RPC, scheduling, caching, and resilience emit
  OpenTelemetry-compatible traces and metrics through `System.Diagnostics`, with no SDK dependency
  forced on you.

## Install

```xml
<!-- Application library -->
<ItemGroup>
  <PackageReference Include="Elarion" Version="0.1.0" />
  <PackageReference Include="Elarion.Generators" Version="0.1.0" PrivateAssets="all" />
</ItemGroup>
```

```csharp
// Turn the generators on, once per assembly
[assembly: UseElarion]
```

The [Quickstart](docs/getting-started/quickstart.mdx) builds a module, a handler, and a working
JSON-RPC endpoint end to end.

## Packages

| Package | Purpose |
| --- | --- |
| [`Elarion.Abstractions`](src/Elarion.Abstractions) | Attributes and contracts: `[AppModule]`, `[Service]`, `[ScheduledJob]`, `IHandler<,>`, `Result<T>`, `AppError`. |
| [`Elarion`](src/Elarion) | Runtime primitives: handler caches, decorators, the in-memory scheduler, resilience runtime, current-user access. |
| [`Elarion.Blobs`](src/Elarion.Blobs) | Provider-neutral blob storage contracts and DTOs. |
| [`Elarion.Blobs.PostgreSql`](src/Elarion.Blobs.PostgreSql) | PostgreSQL-backed blob storage with EF Core model configuration and Npgsql content I/O. |
| [`Elarion.Generators`](src/Elarion.Generators) | Roslyn generators for handlers, services, validators, modules, RPC maps, HTTP endpoint maps, resilience policies, and scheduled jobs. |
| [`Elarion.JsonRpc`](src/Elarion.JsonRpc) | Transport-neutral JSON-RPC dispatcher, envelopes, telemetry, and schema export. |
| [`Elarion.AspNetCore`](src/Elarion.AspNetCore) | ASP.NET Core JSON-RPC endpoint mapping, `[HttpEndpoint]` minimal-API mapping, batch execution, and current-user middleware. |
| [`Elarion.AspNetCore.Mcp`](src/Elarion.AspNetCore.Mcp) | Exposes your JSON-RPC handlers as a Model Context Protocol (MCP) server for AI agents, over Streamable HTTP. |
| [`Elarion.AspNetCore.SchemaGeneration`](src/Elarion.AspNetCore.SchemaGeneration) | MSBuild package that exports `rpc-schema.json` during `dotnet build`. |
| [`Elarion.EntityFrameworkCore`](src/Elarion.EntityFrameworkCore) | Marker attributes for generated `DbSet`s and entity inclusion. |
| [`Elarion.EntityFrameworkCore.Generators`](src/Elarion.EntityFrameworkCore.Generators) | Roslyn generator for `DbSet` properties and entity configuration. |
| [`@swimmesberger/elarion-jsonrpc-client-generator`](src/elarion-jsonrpc-client-generator) | TypeScript CLI that turns a schema export into method contracts, Zod schemas, and a fetch client. |

## Documentation

Full guides live in [`docs/`](docs/index.mdx) and are structured for a documentation site:

- **[Introduction](docs/index.mdx)** · **[Installation](docs/getting-started/installation.mdx)** · **[Quickstart](docs/getting-started/quickstart.mdx)**
- **Core concepts** — [handlers](docs/concepts/handlers.mdx), [results & errors](docs/concepts/results-and-errors.mdx), [modules](docs/concepts/modules.mdx), [services](docs/concepts/services.mdx), [validators](docs/concepts/validators.mdx), [pipelines](docs/concepts/decorator-pipelines.mdx), [caching](docs/concepts/caching.mdx), [current user](docs/concepts/current-user.mdx)
- **Features** — [source generation](docs/source-generation.mdx), [JSON-RPC](docs/json-rpc/index.mdx), [HTTP endpoints](docs/http-endpoints.mdx), [MCP server](docs/json-rpc/mcp.mdx), [scheduling](docs/scheduling/index.mdx), [resilience](docs/resilience.mdx), [EF Core](docs/entity-framework.mdx), [telemetry](docs/telemetry.mdx)
- **Reference** — [packages](docs/reference/packages.mdx), [configuration](docs/reference/configuration.mdx), [vs. ASP.NET Core](docs/reference/comparison.mdx), [troubleshooting](docs/reference/troubleshooting.mdx)

## Requirements

- .NET 10 SDK or later
- ASP.NET Core for the JSON-RPC HTTP transport
- Node.js 18+ for the TypeScript client generator

## Contributing

Issues and pull requests are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) for the development
workflow, validation commands, architecture boundaries, and the publishing process. By participating
you agree to the [Code of Conduct](CODE_OF_CONDUCT.md).

## Security

Please report vulnerabilities privately — see the [security policy](SECURITY.md).

## License

Elarion is licensed under the [Apache License 2.0](LICENSE).

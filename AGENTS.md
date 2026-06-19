# AGENTS.md — Elarion

Canonical agent and contributor guidance for this repository. Tool-specific entry
points point here and must not duplicate content:

- `CLAUDE.md` imports this file (`@AGENTS.md`).
- `.github/copilot-instructions.md` points here for repo-wide Copilot guidance.
- `.github/instructions/csharp.instructions.md` scopes the **C# coding standards**
  section below to `**/*.cs` for Copilot.

Add or change guidance **here**, not in the pointer files.

Elarion is a reusable .NET application framework. Keep it independent from all
downstream applications: do not mention, depend on, or optimize for any consuming
app by name. Application-specific domain code, database conventions, UI frameworks,
and deployment quirks belong in consuming repositories, not here.

## Package layout

- `Elarion.Abstractions` — implementation-neutral attributes, handler contracts, result types, module metadata, scheduling contracts, and source-generation triggers.
- `Elarion` — runtime primitives for handler caches, decorators, modules, resilience policies, current-user access, and the in-memory scheduler.
- `Elarion.Blobs` — implementation-neutral blob storage contracts and DTOs.
- `Elarion.Blobs.PostgreSql` — PostgreSQL-backed blob storage using EF Core model configuration and Npgsql content I/O.
- `Elarion.JsonRpc` — transport-neutral JSON-RPC dispatcher, envelopes, result/error types, telemetry, schema export, and the RPC method-map trigger attribute.
- `Elarion.AspNetCore` — ASP.NET Core JSON-RPC endpoint mapping, batch execution, current-user middleware, HTTP transport support, and minimal-API endpoint mapping for `[HttpEndpoint]` handlers (`HttpAppErrorMapper`, `ElarionHttpResults`, the `[GenerateHttpEndpointMap]` trigger).
- `Elarion.AspNetCore.Mcp` — Model Context Protocol (MCP) server integration: exposes MCP-surfaced `[RpcMethod]` handlers as tools over Streamable HTTP via a dedicated `McpDispatcher`, independent of the JSON-RPC endpoint (`AddElarionMcp`, `MapElarionMcp`). The only package referencing the `ModelContextProtocol` SDK.
- `Elarion.AspNetCore.SchemaGeneration` — MSBuild package and host-launching tool for generating JSON-RPC schemas during build.
- `Elarion.EntityFrameworkCore` — marker attributes for EF Core entity and DbSet generation.
- `Elarion.Generators` — Roslyn source generators for handlers, validators, services, modules, RPC method maps, HTTP endpoint maps, resilience policies, and scheduled jobs.
- `Elarion.EntityFrameworkCore.Generators` — Roslyn generator for DbSet properties and entity configuration application.
- `@swimmesberger/elarion-jsonrpc-client-generator` — TypeScript CLI/library that converts exported Elarion JSON-RPC schemas into method contracts, Zod result schemas, and a portable fetch client. Lives in `src/elarion-jsonrpc-client-generator`.

## Architecture boundaries

- Core framework packages must stay reusable and domain-neutral. Do not add consuming-application names, domain logic, deployment conventions, or app-specific dependencies.
- `Elarion.Abstractions` must not depend on runtime integration packages.
- `Elarion` may depend on abstractions but should avoid ASP.NET Core, EF Core, and transport-specific concerns.
- `Elarion.Blobs` must stay provider-neutral. Provider implementations such as `Elarion.Blobs.PostgreSql` own storage-specific dependencies and schema configuration.
- `Elarion.JsonRpc` owns JSON-RPC runtime contracts, dispatch, telemetry, and schema export, and must stay transport-neutral (no ASP.NET Core dependency).
- `Elarion.AspNetCore` owns HTTP/JSON-RPC endpoint integration and ASP.NET Core-specific behavior. Keep JSON-RPC runtime contracts, telemetry, and schema export in `Elarion.JsonRpc`.
- `Elarion.AspNetCore.Mcp` owns MCP transport integration only; the dedicated-dispatcher wrapper (`McpDispatcher`) lives in `Elarion.JsonRpc`, and the `RpcTransports` flag plus `[McpMethod]`/`[RpcMethod]` attributes live in `Elarion.Abstractions`.
- EF Core packages own only EF-specific marker APIs and source generation.
- Prefer compile-time generation over runtime reflection scanning. Source generators should emit deterministic, inspectable code and fail with diagnostics for unsupported patterns.
- Preserve trimming and AOT friendliness on framework code paths. Avoid hidden runtime discovery and APIs that undermine linker safety.

## JSON-RPC model

JSON-RPC is a first-class optional transport:

1. Application handlers declare `[RpcMethod("module.action")]`. The `Transports` flag (`RpcTransports.JsonRpc`/`Mcp`/`All`, default `All`) selects which dispatcher-based transports expose the handler — JSON-RPC, MCP, or both.
2. `RpcMethodMapGenerator` emits dispatcher registration code.
3. Hosts configure `JsonRpcDispatcher` with the same `JsonSerializerOptions` used at runtime.
4. `Elarion.JsonRpc.JsonRpcSchemaExporter` or `Elarion.AspNetCore.SchemaGeneration` exports `rpc-schema.json` from registered methods.
5. `elarion-jsonrpc-client-generator` emits `rpc-types.ts`, `rpc-schemas.ts`, and `rpc-client.ts`.
6. When a host opts into `[GenerateModuleBootstrapper]`, RPC registration is **module-scoped and feature-flag-gated** — see [Module-aware transport gating](#module-aware-transport-gating).

Generated TypeScript should remain portable across browser and Node.js server
runtimes. Prefer standard `fetch`, injectable transport, `AbortSignal`, and small
common dependencies such as Zod when they materially improve safety.

## HTTP endpoint model

Plain HTTP/REST is a parallel first-class optional transport that maps the same handlers:

1. Application handlers declare `[HttpEndpoint("route")]` (verb inferred: nested `Command` → POST, `Query` → GET) or `[HttpEndpoint(HttpVerb.X, "route")]`.
2. `HttpEndpointMapGenerator` emits a `MapAll(IEndpointRouteBuilder)` body of strongly-typed minimal-API registrations (one `MapGet`/`MapPost`/... per handler), triggered by `[GenerateHttpEndpointMap]` on a host partial class.
3. Each emitted lambda binds the request (`[AsParameters]` for GET/DELETE and opt-in custom-bound requests; JSON body for default POST/PUT/PATCH) and translates `Result<T>` via `ElarionHttpResults` — `200`/`204` on success, RFC 7807 ProblemDetails on failure with the status from `HttpAppErrorMapper`.

Keep the generator emitting concrete-typed lambdas (no open generics, no reflection
binding) so output stays AOT/RDG friendly. The `[HttpEndpoint]` attribute lives in
`Elarion.Abstractions` and must carry no ASP.NET dependency; per-property binding
customization (`[FromRoute]`/`[FromHeader]`/`[FromForm]`/`IFormFile`, etc.) is the
consuming DTO's opt-in via `Microsoft.AspNetCore.Http.Abstractions`, detected by the
generator structurally through the `Microsoft.AspNetCore.Http.Metadata.IFrom*Metadata`
interfaces and `IFormFile` types. When a host opts into `[GenerateModuleBootstrapper]`,
HTTP mapping is **module-scoped and feature-flag-gated** — see
[Module-aware transport gating](#module-aware-transport-gating).

## MCP model

MCP is a parallel first-class optional transport, a peer of JSON-RPC and HTTP, served by `Elarion.AspNetCore.Mcp`:

1. A handler opts into MCP via its `[RpcMethod(Transports = ...)]` flag (default `All` includes MCP). `[McpMethod(ToolName = ...)]` customizes only the tool name — it carries no enable/disable flag (use `Transports` for that).
2. `RpcMethodMapGenerator` emits `RegisterMcpAll(dispatcher)` (the MCP-surfaced registrations) and `McpMetadata()` (the reflection-free tool table), alongside the JSON-RPC `RegisterAll`.
3. MCP owns a **dedicated dispatcher**: `McpDispatcher` (in `Elarion.JsonRpc`) wraps a separate `JsonRpcDispatcher` instance built only from MCP-surfaced methods. An MCP-only handler is therefore genuinely absent from `/rpc` and the exported JSON-RPC schema; a JSON-RPC-only handler is never dispatchable as a tool; a "both" handler is registered in both dispatchers.
4. Hosts call `AddElarionMcp(metadata, serializerOptions, registerMcp, configure)` and `MapElarionMcp()`; `MapJsonRpc()` is never required. When a host opts into `[GenerateModuleBootstrapper]`, MCP registration is **module-scoped and feature-flag-gated** — see [Module-aware transport gating](#module-aware-transport-gating).

REST stays a separate opt-in via `[HttpEndpoint]` (it needs route/verb/param binding that don't fit a flags enum); JSON-RPC and MCP share the single define-once `[RpcMethod]` identity and differ only by the `Transports` flag.

## Module-aware transport gating

All three transports become **module-scoped and feature-flag-gated** when a host opts in with
`[GenerateModuleBootstrapper]`. `AppModuleDiscoveryGenerator` associates each `[RpcMethod]`/`[HttpEndpoint]`
handler with a module by longest-prefix namespace match and emits, on the host bootstrapper partial:

- per-module `Map{Module}Http(IEndpointRouteBuilder)`, `Add{Module}JsonRpc(JsonRpcDispatcher)`, `Add{Module}Mcp(JsonRpcDispatcher)`, and `Get{Module}McpMetadata()`;
- aggregate `MapAllEndpoints(endpoints, configuration)` (module `MapEndpoints` + `[HttpEndpoint]`), `RegisterRpcMethods(dispatcher, configuration)`, `RegisterMcpMethods(dispatcher, configuration)`, and `GetMcpMetadata(configuration)`.

Per-module JSON-RPC/MCP methods are additionally filtered by each handler's `Transports` flag, so the JSON-RPC
dispatcher gets only JSON-RPC-surfaced handlers and the MCP dispatcher only MCP-surfaced ones. Core modules map
unconditionally; feature modules are gated by `Modules:{Name}:Enabled` (via `IsModuleEnabled`), so a disabled
module disappears across services, validators, `MapEndpoints`, `[HttpEndpoint]`, JSON-RPC, and MCP. Handlers
whose namespace is under no module emit a warning (`ELHTTP003`/`ELRPC001`; MCP reuses `ELRPC001` since it is
built on `[RpcMethod]`) and are mapped ungated so they are never silently dropped. The flat
`HttpEndpointMap.MapAll` / `RpcMethodMap.RegisterAll` / `RpcMethodMap.RegisterMcpAll` maps stay the **ungated
non-module path**, and `RegisterAll` remains what schema export reads for the complete contract (feature modules
default to enabled, so the exported schema is complete unless a module is explicitly disabled at build time).

Per-endpoint authorization/conventions are the host's job, via the per-module method on a configured group
(e.g. `MapBillingHttp(app.MapGroup("/billing").RequireAuthorization(policy))`) plus the module
`MapEndpoints` escape hatch — the generator never reads `[Authorize]`/`[AllowAnonymous]`. RPC/MCP gating needs
`IConfiguration` at dispatcher-compose time, so `AddElarionJsonRpcDispatcher` (transport-neutral, via
`IServiceProvider`) and `AddJsonRpc`/`AddElarionMcp` (ASP.NET, via `IConfiguration`) expose config-aware
registration overloads, used as `AddJsonRpc(serializerOptions, ModuleBootstrapper.RegisterRpcMethods)` and
`AddElarionMcp(ModuleBootstrapper.GetMcpMetadata(config), serializerOptions, ModuleBootstrapper.RegisterMcpMethods, configure)`.

## TypeScript client generator

The npm package lives in `src/elarion-jsonrpc-client-generator`.

- Keep generated output deterministic.
- Keep the generated direct API ergonomic, for example `rpc.clients.get(params, options)`.
- Keep the generic transport primitive available for advanced adapters.
- Preserve tuple-aware batch typing through generated `$request` helpers and `$batch`.
- Runtime validation should use generated Zod schemas by default, with opt-outs or transform hooks for consumers that need them.
- Generated code must type-check under modern browser projects and NodeNext projects with Node fetch types.
- Do not import React, TanStack, Vite, or any downstream framework from generated runtime code.

## C# coding standards

Applies to `**/*.cs`. Copilot scopes this section via
`.github/instructions/csharp.instructions.md`; other agents should apply it when
editing C#.

### Style

- Always use the latest C# version supported by the repo, currently C# 14 features.
- All classes should be declared `sealed` unless intentionally designed for inheritance. When a class is intentionally extensible, add XML docs explaining the inheritance contract.
- Prefer immutable records for DTOs, options, and other data containers.
- Prefer nominal, property-based records for public DTOs and API models. Use `required` + `init` for non-nullable members and nullable `init` properties for optional values. Positional records are acceptable for tiny internal helpers or tests when they are clearly more readable.
- Prefer read-only collection types (`IReadOnlyList<T>`, `IReadOnlyCollection<T>`, `ImmutableArray<T>`, etc.) for immutable public surfaces.
- Prefer primary constructors for DI-oriented services when they keep the type concise and readable.

### Naming

- PascalCase for type names, method names, and public members.
- Private instance fields use `_camelCase`.
- Local variables and parameters use `camelCase` without underscores.
- Static readonly fields and constants use PascalCase.
- Prefix interfaces with `I`; prefix type parameters with `T`.

### Formatting

- Follow `.editorconfig` as the formatting source of truth.
- Prefer file-scoped namespaces and single-line using directives.
- Keep opening braces on the same line.
- Prefer early returns over deep nesting.
- Use pattern matching, switch expressions, and `nameof` where they improve clarity.
- Ensure the final return statement of a method is on its own line.

### Comments and public API docs

- Add XML doc comments for public APIs. Include `<example>` and `<code>` where usage is non-obvious.
- Add regular code comments only to clarify intent, constraints, or non-obvious tradeoffs. Do not comment every function or restate obvious code.
- When a public API uses a non-obvious pattern for compatibility, source generation, AOT, or performance reasons, document why.

### Nullable reference types

- Declare values non-nullable by default and validate nullability at entry points.
- Use `is null` / `is not null` instead of `== null` / `!= null`.
- Trust the type system; avoid redundant null checks when annotations already guarantee non-null values.

### Async and background work

- Never introduce unobserved fire-and-forget tasks.
- Pass cancellation tokens through async flows; do not use `CancellationToken.None` unless there is a documented, deliberate reason.
- Long-lived or background work must be owned by a host-managed abstraction (`IHostedService`, scheduler service, explicit queue/loop abstraction, etc.), not hidden inside helper methods.
- Handle `OperationCanceledException` deliberately; do not log expected cancellation as an error.

## Testing

- Always add regression tests when fixing bugs.
- Follow nearby test naming and capitalization conventions.
- Do not emit `Arrange`, `Act`, or `Assert` comments.
- Keep tests deterministic; avoid timing-sensitive behavior unless the test specifically verifies concurrency or scheduling.
- For source generator changes, add or update generator tests and keep emitted output deterministic and inspectable.

## Development and validation

```bash
dotnet restore Elarion.slnx
dotnet build Elarion.slnx --configuration Release
dotnet test --project tests/Elarion.Tests/Elarion.Tests.csproj --configuration Release
dotnet pack Elarion.slnx --configuration Release --no-build

cd src/elarion-jsonrpc-client-generator
npm ci
npm run build
npm test
npm pack --dry-run
```

When changing the TypeScript generator, also generate from a representative
`rpc-schema.json` and type-check the emitted `rpc-types.ts`, `rpc-schemas.ts`, and
`rpc-client.ts` under `moduleResolution: NodeNext`.

## Publishing

Publishing uses GitHub Actions trusted publishing/OIDC:

- NuGet publishing uses `NuGet/login` and the `NUGET_USER` repository variable or secret.
- npm publishing uses trusted publishers for `@swimmesberger/elarion-jsonrpc-client-generator`.
- Pushes to `main` publish preview packages using `VersionPrefix` plus the workflow run identity.
- Published GitHub releases or manual dispatches can publish explicit stable or prerelease SemVer versions.

Keep workflow changes tokenless unless a registry explicitly requires otherwise.

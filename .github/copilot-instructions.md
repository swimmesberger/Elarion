# Elarion - Copilot Instructions

Elarion is a reusable .NET application framework. Keep it independent from all downstream applications: do not mention, depend on, or optimize for any consuming app by name. Application-specific domain code, database conventions, UI frameworks, and deployment quirks belong in consuming repositories, not here.

## Package layout

The repository contains reusable framework packages:

- `Elarion.Abstractions` - implementation-neutral attributes, handler contracts, result types, module metadata, scheduling contracts, and source-generation triggers.
- `Elarion` - runtime primitives for handler caches, decorators, modules, resilience policies, current-user access, and the in-memory scheduler.
- `Elarion.AspNetCore` - ASP.NET Core JSON-RPC dispatcher, endpoint mapping, current-user middleware, telemetry, and schema export support.
- `Elarion.EntityFrameworkCore` - marker attributes for EF Core entity and DbSet generation.
- `Elarion.Generators` - Roslyn source generators for handlers, validators, services, modules, RPC method maps, resilience policies, and scheduled jobs.
- `Elarion.EntityFrameworkCore.Generators` - Roslyn generator for DbSet properties and entity configuration application.
- `@swimmesberger/elarion-jsonrpc-client-generator` - TypeScript CLI/library that converts exported Elarion JSON-RPC schemas into method contracts, Zod result schemas, and a portable fetch client.

## Architecture boundaries

- Core framework packages must stay reusable and domain-neutral.
- `Elarion.Abstractions` must not depend on runtime integration packages.
- `Elarion` may depend on abstractions, but should avoid ASP.NET Core, EF Core, and transport-specific concerns.
- `Elarion.AspNetCore` owns HTTP/JSON-RPC integration and ASP.NET Core-specific behavior.
- EF Core packages own only EF-specific marker APIs and source generation.
- Source generators should emit deterministic, inspectable code and fail with diagnostics for unsupported patterns.
- Do not add runtime reflection scanning where compile-time generation is feasible.

## JSON-RPC model

JSON-RPC is a first-class optional transport:

1. Application handlers declare `[RpcMethod("module.action")]`.
2. `RpcMethodMapGenerator` emits dispatcher registration code.
3. Hosts configure `JsonRpcDispatcher` with the same `JsonSerializerOptions` used at runtime.
4. `JsonRpcSchemaExporter` exports `rpc-schema.json` from registered methods.
5. `elarion-jsonrpc-client-generator` emits `rpc-types.ts`, `rpc-schemas.ts`, and `rpc-client.ts`.

Generated TypeScript should remain portable across browser and Node.js server runtimes. Prefer standard `fetch`, injectable transport, `AbortSignal`, and small common dependencies such as Zod when they materially improve safety.

## TypeScript client generator

The npm package lives in `src/elarion-jsonrpc-client-generator`.

- Keep generated output deterministic.
- Keep the generated direct API ergonomic, for example `rpc.clients.get(params, options)`.
- Keep the generic transport primitive available for advanced adapters.
- Preserve tuple-aware batch typing through generated `$request` helpers and `$batch`.
- Runtime validation should use generated Zod schemas by default, with opt-outs or transform hooks for consumers that need them.
- Generated code must type-check under modern browser projects and NodeNext projects with Node fetch types.
- Do not import React, TanStack, Vite, or any downstream framework from generated runtime code.

## Development and validation

Common validation commands:

```bash
dotnet restore Elarion.slnx
dotnet build Elarion.slnx --configuration Release
dotnet test --project tests/Elarion.Tests/Elarion.Tests.csproj --configuration Release

cd src/elarion-jsonrpc-client-generator
npm ci
npm run build
npm test
npm pack --dry-run
```

When changing the TypeScript generator, also generate from a representative `rpc-schema.json` and type-check the emitted `rpc-types.ts`, `rpc-schemas.ts`, and `rpc-client.ts` under `moduleResolution: NodeNext`.

## Publishing

Publishing uses GitHub Actions trusted publishing/OIDC:

- NuGet publishing uses `NuGet/login` and the `NUGET_USER` repository variable or secret.
- npm publishing uses trusted publishers for `@swimmesberger/elarion-jsonrpc-client-generator`.
- Pushes to `main` publish preview packages using `VersionPrefix` plus the workflow run identity.
- Published GitHub releases or manual dispatches can publish explicit stable or prerelease SemVer versions.

Keep workflow changes tokenless unless a registry explicitly requires otherwise.

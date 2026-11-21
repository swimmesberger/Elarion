---
type: Architecture Decision Record
title: "ADR-0100: The schema exports declared constants, request presets and typed limits"
description: "Modules declare client constants and named request presets once in C#; the schema exports them and the generated client gives them, and every request member's limits, typed accessors."
status: draft
---
# ADR-0100: The schema exports declared constants, request presets and typed limits

- Status: Proposed
- Date: 2026-11-21
- Related: [ADR-0085](0085-handler-declarations-are-projected-not-copied.md) (handler declarations are projected,
  not copied), [ADR-0030](0030-client-capability-bootstrap.md) (the capability vocabulary in the schema)

## Context

The schema chain (`[Handler]` → `rpc-schema.json` → generated TypeScript) carries methods, their errors and
requirements, the capability vocabulary and client events. It carries nothing else a client must agree on with the
server, so applications copy it by hand:

- **Limits.** A `[MaxLength(50)]` on an upload list reaches the client only inside a Zod check chain
  (`json-schema-to-zod.ts`); a page that shows "up to 50 files" or disables the button at 50 declares
  `UPLOAD_LIMIT = 50` itself, with a comment pointing at the attribute.
- **Named presets.** A list's quick filters are server-defined parameter sets ("to transfer" = `{ toTransfer: true }`,
  "not exported" = `{ status: "Approved", exported: false }`) that the server also counts. The client repeats each
  parameter set to open the list the chip counted.
- **Constants.** Sort keys, path segments the server builds links from, a maximum amount a payment format allows.

Each copy drifts silently: the client keeps compiling when the server changes the value. ADR-0085 already settled the
principle for authorization requirements — a declaration is projected into the schema, never copied.

## Decision (proposed)

- **Limits get a typed accessor, from the schema already exported.** The JSON Schema of every method's params already
  holds `maxLength`, `maxItems`, `minimum` and the rest. The TypeScript generator emits `rpcLimits`, typed from the
  same schemas: `rpcLimits['documents.upload'].files.maxItems` is `50` as a literal type, and a nested member is reached
  by its path. No server change; only members with a limit appear.
- **Constants are declared on a module class.** `[ClientConstants]` on a static class in a module's namespace (the
  `[ClientFeatures]` pattern) exports its public `const` and `static readonly` members of exportable types (numbers,
  strings, booleans, enums, arrays of them) as `constants.{module}.{name}`. The generator reads them at compile time
  (a manifest entry, ADR-0006), so nothing is scanned at run time, and an unexportable member is a diagnostic.
- **Presets are request values declared next to the handler.** `[RequestPreset("toTransfer")]` on a `static readonly`
  field holding the handler's request type exports its non-default members as `presets.{method}.{name}`, typed by the
  method's params. The generated client gives `rpc.presets['invoices.list'].toTransfer` as a params object, so a chip
  opens exactly what the server counts with the same instance. The server serializes presets with the canonical JSON
  configuration, so wire names and enum spellings match the calls.
- **Sort keys** of a `SortMap` the handler declares become the first built-in export of the same block, closing the
  export left open when sort-key validation shipped.

## Consequences

- One declaration per fact; a server change reaches the client as a type change, so a stale page fails the build.
- The schema grows by two optional blocks; schemas without declarations stay byte-identical.
- Presets carry only request values, not counts or labels: labels stay in the client's language files.
- A preset of a handler that is not on JSON-RPC has no method to belong to and is a diagnostic.

## Rejected alternatives

- **A per-member `x-elarion-limit` annotation.** The standard keywords already carry the limits; a second spelling
  would be a second source.
- **Exporting every public constant of an application.** It would publish internals by accident; export is a
  declaration.
- **Presets as free-form JSON in configuration.** Untyped on both sides, and not the instance the server counts with.
- **Doing nothing and documenting the copy.** The copies are what drift.

---
type: Architecture Decision Record
title: "ADR-0100: The schema exports declared constants, request presets and typed limits"
description: "Modules declare client constants and handlers named request presets once in C#; the schema exports them, a handler's sort keys and each upload member's declared limits, and the generated client gives them, and every params member's limits, typed accessors."
status: stable
---
# ADR-0100: The schema exports declared constants, request presets and typed limits

- Status: Accepted (implemented 2026-11-22)
- Date: 2026-11-21
- Related: [ADR-0085](0085-handler-declarations-are-projected-not-copied.md) (handler declarations are projected,
  not copied), [ADR-0030](0030-client-capability-bootstrap.md) (the capability vocabulary in the schema),
  [ADR-0105](0105-uploaded-files-are-handler-parameters.md) (uploaded files, whose limits this record declares)

## Context

The schema chain (`[Handler]` → `rpc-schema.json` → generated TypeScript) carries methods, their errors and
requirements, the capability vocabulary and client events. It carries nothing else a client must agree on with the
server, so applications copy it by hand:

- **Limits.** A `[MaxLength(50)]` on an upload list reaches the client only inside a Zod check chain
  (`json-schema-to-zod.ts`); a page that shows "up to 50 files" or disables the button at 50 declares
  `UPLOAD_LIMIT = 50` itself, with a comment pointing at the attribute.
- **Upload limits.** An `UploadedFile` member's size and content-type limits were passed when the handler read the
  file (`IUploadedFiles.OpenAsync(file, new UploadConstraints { … })`, ADR-0105), so neither the schema nor the
  client knew them: a client uploaded a file only to have the call refuse it.
- **Named presets.** A list's quick filters are server-defined parameter sets ("to transfer" = `{ toTransfer: true }`,
  "not exported" = `{ status: "Approved", exported: false }`) that the server also counts. The client repeats each
  parameter set to open the list the chip counted.
- **Constants.** Sort keys, path segments the server builds links from, a maximum amount a payment format allows.

Each copy drifts silently: the client keeps compiling when the server changes the value. ADR-0085 already settled the
principle for authorization requirements — a declaration is projected into the schema, never copied.

## Decision

1. **Limits get a typed accessor, from the schema already exported.** The JSON Schema of every method's params
   already holds the constraint keywords. The TypeScript generator emits `rpcLimits` in `rpc-schemas.ts`, a literal
   (`as const`) object keyed by method and then by member path: `rpcLimits['documents.upload'].receipts.maxItems` is
   `50` as a type, a nested member is reached through its parent (`address.street.maxLength`), and the elements of a
   list through `items`. The keywords are the numeric bounds — `minLength`, `maxLength`, `minimum`,
   `exclusiveMinimum`, `maximum`, `exclusiveMaximum`, `minItems`, `maxItems` — plus an upload member's `maxBytes`
   and `contentTypes`. Patterns, formats and enums stay out: the types and the params schemas carry them, and a
   rewritten (anchored) pattern is no value to show. Only members with a limit appear; union cases have no fixed path
   and are skipped. Object nodes carry no limit keywords, so a member name never collides with one.

2. **Upload limits are declared on the member, and the pipeline enforces them.** `[UploadLimits(MaxBytes = …,
   ContentTypes = […])]` (`Elarion.Abstractions`) goes on an `UploadedFile`, nullable `UploadedFile` or collection
   member of a handler's request (on a positional record, the property or the parameter). The handler generator
   emits a reflection-free function that walks those members and attaches `UploadLimitsDecorator` innermost, which
   declares each file's limits to the scoped `IDeclaredUploadConstraints` before the handler runs. The
   `Elarion.Blobs` reader (`AddElarionUploadedFiles`) is that scope's `IDeclaredUploadConstraints` as well as its
   `IUploadedFiles`, and checks every declared `UploadConstraints` of a file, by blob id, on each `OpenAsync` and
   `CommitAsync`, after the constraints the call passes: declarations only tighten. A handler with declared limits and
   no `IDeclaredUploadConstraints` registered fails when its pipeline is built (fail closed). The exporter writes the
   limits next to the marker, `"x-elarion-upload": true, "x-elarion-max-bytes": …, "x-elarion-content-types": […]`,
   on the member or on the items of a collection, so a generator that predates them still sees an upload. The
   generated params schema refines the `File`/`Blob` arm with the server's messages, comparing media types as the
   server does (case-insensitive, parameters ignored, an untyped blob as `application/octet-stream`), so the client
   rejects a file before uploading it. The OpenAPI document carries the same keywords on the member's own schema. A
   declaration the pipeline would not enforce — on a member that holds no
   uploaded file, on a type that is not a handler's request in the assembly, without a limit — is a build error
   (`ELUPL001`, `ELUPL002`).

3. **Constants are declared on a module class.** `[ClientConstants]` on a static class (not private) under a
   module's namespace exports its public `const` fields, `static readonly` fields and static get-only properties of
   exportable types — numbers, strings, booleans, enums, and arrays, `IReadOnlyList<T>`/`IReadOnlyCollection<T>` or
   `ImmutableArray<T>` of them — as `constants.{module}.{name}`, module and member camel-cased as method names are.
   The generator registers them, per module, in the module's generated default services (a new `AddClientConstants`
   hook, like feature flags), as a `ModuleClientConstants` holding a lazy reader per member; the schema tool resolves
   the enabled modules' sets from the host and the exporter writes each value with the canonical JSON options. No
   manifest entry is needed: the module's own assembly registers them, so an internal class is fine, a disabled
   module exports nothing, and nothing is scanned or read at run time. An unexportable or mutable public member, a
   class its module cannot register, and two members of a module with one exported name are build errors
   (`ELRPC009`–`ELRPC011`). The generated client exports `rpcConstants`, literal types.

4. **Presets are request values declared on the handler.** `[RequestPreset("toTransfer")]` on a `static readonly`
   field of a `[Handler]` class, holding the handler's request type, exports the request as
   `methods.{method}.presets.{name}`. The schema tool hands the exporter each method's handler type (from the
   `HandlerMetadata` every handler registers, keyed by request type), and the build-time exporter reads the fields.
   A preset carries what a client must send for the server to see exactly that instance: every required member, and
   every other member whose wire value differs from what the server assumes when the member is left out — found by
   deserializing the required members alone, so constructor-parameter defaults and initializers apply as for a call
   — with an explicit `null` where the preset clears a non-null default. Values use the canonical options, so wire
   names and enum spellings match calls. A field that is not static readonly, not of the request type, not on a
   handler, a duplicate name, or a handler not on JSON-RPC is a build error (`ELRPC006`–`ELRPC008`). The generated
   client exports `rpcPresets`, each preset typed as the method's params, for `rpc.invoices.list({
   ...rpcPresets['invoices.list'].toTransfer, page: 2 })`.

5. **Sort keys are the first built-in export, on the method.** The keys of the one `SortMap` static field a handler
   declares (`SortMap<T>.Keys`) are exported as `methods.{method}.sortKeys`: each key in declaration order with the
   direction a bare key applies and `default: true` on the default key. A handler with more than one sort map exports
   none and gets a warning (`ELRPC012`), because the schema cannot tell which one its sort parameter uses. The
   generated client exports `rpcSortKeys`.

All four blocks are optional and self-gating: a schema without declarations is byte-identical, and the generated
`rpc-types.ts` and `rpc-client.ts` never change. The new TypeScript exports live at the end of `rpc-schemas.ts`,
which imports the `RpcMethods` type only when it has presets.

## Consequences

- One declaration per fact; a server change reaches the client as a type change, so a stale page fails the build.
- A schema with constraint keywords gains the `rpcLimits` export, even without any new declaration; nothing existing
  changes. Schemas without any limit or declaration produce byte-identical output.
- An upload that breaks a declared limit is refused before it is uploaded, and again by the server; the handler no
  longer repeats the limits at read time (it still may, and both apply).
- Presets carry only request values, not counts or labels: labels stay in the client's language files, and the server
  counts with the same field.
- Presets and sort keys are read by the build-time exporter through reflection on the handler type, like the
  DataAnnotations it already reads; constants are registered by generated code. Neither costs anything at run time
  beyond one registration per module that declares constants.
- The OpenAPI document carries declared upload limits with the same keywords. OpenAPI describes `UploadedFile` as one
  shared component schema, which a per-member limit must never change, so a member with declared limits keeps its own
  inline schema (its component id is dropped before references are made); a list of `UploadedFile` gets its `items`
  back there too. This relies on the metadata key under which `Microsoft.AspNetCore.OpenApi` records a component id,
  which a test pins.
- The schema-generation tool needs the host's services for presets and sort keys (it already loads the host).

## Rejected alternatives

- **A per-member `x-elarion-limit` annotation for ordinary limits.** The standard keywords already carry them; a
  second spelling would be a second source.
- **Upload limits as a vendor object (`"x-elarion-upload": { "maxBytes": … }`).** One keyword, but a generator that
  checks the marker for `true` would stop seeing an upload; sibling keywords keep older clients working.
- **Upload limits as a `ValidationAttribute`.** Size and content type are only known once the bytes are read, and a
  pre-handler asynchronous check is a time-of-check/time-of-use bug; the repository also adds no Elarion validation
  attribute.
- **Binding the limits in the JSON converter** (a constrained `UploadedFile` per member). A converter does not know
  the member it reads, the source-generated serializer rejects derived `JsonConverterAttribute`s, and an
  `UploadedFile` built in code would carry no limits.
- **Limits on the handler call only** (ADR-0105's first form). The client cannot see them, so it uploads a file the
  call refuses.
- **Constants through a manifest entry and a host-side registry.** Needed only to reference another assembly's
  members, which then must be public; registering in the declaring module's own services needs neither and follows
  module gating.
- **Presets in a top-level `presets` block** (as first proposed). A preset is a fact of its method, like its errors
  and tags; it sits on the method, as the sort keys do.
- **Presets read by generated code at startup.** A preset is consumed only by the schema export, which already
  reflects at build time; registering every preset at run time would cost every host for a build artifact.
- **Exporting every public constant of an application.** It would publish internals by accident; export is a
  declaration.
- **Presets as free-form JSON in configuration.** Untyped on both sides, and not the instance the server counts with.
- **Doing nothing and documenting the copy.** The copies are what drift.

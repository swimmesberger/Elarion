---
type: Glossary
title: "Glossary"
description: "Elarion's own terms, opinionated, with the words to avoid."
generated: { by: claude-code/claude-fable-5-2, at: 2026-11-21T11:30:00Z }
status: draft
---
# Elarion

A reusable .NET application framework: an application writes handlers inside modules, source generators emit
the wiring, and opt-in packages project the handlers onto transports and add persistence, events and actors.

This file is the glossary and nothing else: the framework's own terms, one or two sentences each, opinionated,
with the words to avoid. No general programming concepts, project facts, instructions or decisions; those live
in `AGENTS.md`, `docs/`, `specs/` and `docs/decisions/`. A term is defined here in the meaning the guidance and
the published documentation already use; the page that describes its behavior is under `docs/concepts/` or
`docs/capabilities/`. The name `GLOSSARY.md` is the one public domain-modeling skills look for.

## Language

### Application model

**Handler**:
The framework's use-case boundary: one request in, one `Result` out, with no transport concern. Every
transport is a projection of the same handler definition.
_Avoid_: endpoint, controller, command handler, use-case class

**Result**:
The value a handler returns, a success or an `AppError`. Unauthenticated, forbidden, not-found, validation and
conflict outcomes are results, not exceptions.
_Avoid_: response wrapper, envelope, exception (for an expected outcome)

**Handler pipeline**:
The ordered decorators a generated factory wraps around a handler: the framework's own (authorization,
feature gate, validation and others) at fixed positions around the application's `[DecoratorList]`. It is
transport-neutral and lives in core.
_Avoid_: middleware, filter chain, behaviors

**Module**:
An application boundary: an `[AppModule]` class at the root of a namespace that owns the handlers, services,
jobs, consumers, actors and client-event topics declared under it. Everything in it is module-internal.
_Avoid_: bounded context, area, slice, plugin (as a name for the code unit)

**Core module**:
A module that is always enabled and initializes before feature modules; it holds a foundation capability
other modules assume is present.
_Avoid_: base module, system module, kernel module

**Feature module**:
A module that configuration can switch off; when it is off it disappears from registration, validation and
every transport together.
_Avoid_: optional module, plugin module

**Module contract**:
The published `[ModuleContract]` interface through which one module calls another synchronously; the
implementation stays internal to the owning module.
_Avoid_: public API of a module, module interface, facade

**Module API**:
The generated, typed, in-process facade over one module's handlers. It is neither a transport nor a way to
publish a module's internals.
_Avoid_: client, SDK, module contract (a different thing)

**Shared kernel**:
The types declared under no module, entities above all, which every module may reference freely.
_Avoid_: common, shared library, core (which names the `Elarion` package)

**Platform capability**:
A domain-blind mechanism the host offers to all modules through a port declared outside every module.
Domain-aware behavior that is always on is a core module instead.
_Avoid_: foundation capability (which is a core module), infrastructure service, cross-cutting service

**Host**:
The executable that composes modules and packages, authenticates the caller and binds transports to a wire.
_Avoid_: app (ambiguous), server, startup project

**Consuming application**:
An application built on Elarion. The framework never names one and never optimizes for one.
_Avoid_: the app, customer project, downstream (as a noun for a specific application)

### Packages and boundaries

**Abstractions**:
The `Elarion.Abstractions` package: implementation-neutral contracts (interfaces, attributes, markers, data
records) with no runtime-integration dependency.
_Avoid_: contracts package, interfaces project

**Core**:
The `Elarion` package: the dependency-light, transport-neutral runtime behavior over Abstractions.
_Avoid_: kernel, base, the framework (when the one package is meant)

**Seam**:
A replaceable contract with a fixed shape: the contract and its attribute in Abstractions, the
transport-neutral decorator in core, the default implementation in an opt-in package. It is designed for its
strongest intended implementation.
_Avoid_: extension point (a frontend term here), hook, plug-in point, abstraction layer

**Provider**:
A package that implements a seam for one concrete technology. Replacing it changes no handler attribute and no
DTO.
_Avoid_: driver, backend (except in "feature-flag backend"), adapter (which names a connection adapter)

**Opt-in sibling**:
A package next to core that an application adds only when it uses it; core never depends on it.
_Avoid_: plugin, extension package, add-on

**Protocol package**:
A package that defines a wire format and its dispatcher without binding a wire (`Elarion.JsonRpc`,
`Elarion.Grpc`).
_Avoid_: transport package (too broad), wire package

**Host package**:
A package that binds a protocol or a subsystem to ASP.NET Core, sockets or another hosting environment. It may
depend on a protocol package, never the reverse.
_Avoid_: integration package, web package

**Transport**:
A projection of handlers onto a wire: HTTP, JSON-RPC, MCP or gRPC. Transports are parallel and optional, and
none of them carries business behavior.
_Avoid_: API, protocol (which names the format alone), channel

**Host tier**:
One of the two persistence stacks a host chooses between: the EF tier, or the EF-free NativeAOT SQL tier. The
two are not mixed.
_Avoid_: mode, flavor, profile

**Scale tier**:
The size Elarion's shipped defaults are built for: one to ten nodes, vertical first, on the one PostgreSQL the
application already runs. Beyond it a seam is replaced; a default does not grow.
_Avoid_: small scale, enterprise scale, supported scale

### Source generation

**Generator**:
An incremental Roslyn source generator bundled by a public package. It discovers through syntax providers and
emits deterministic, AOT-safe code and compile-time diagnostics.
_Avoid_: codegen, scaffolder, template

**Bootstrapper**:
The registration and transport-mapping code a `[GenerateModuleBootstrapper]` host gets generated; the one
canonical path by which modules reach the container and the transports.
_Avoid_: startup code, registration helper, composition root (for the generated part)

**Manifest**:
The assembly metadata a generator emits so a generator in a referencing assembly can discover what the
assembly declares, without scanning its symbols.
_Avoid_: registry, catalog (which names a runtime list), index

**Schema**:
`rpc-schema.json`, exported from the handlers; the single input of the generated TypeScript client.
_Avoid_: OpenAPI document (a different artefact), contract file, IDL

### Authorization, gating and validation

**Requirement**:
A declarative authorization condition on a handler (`[Require*]`), enforced in the handler pipeline whatever
the transport and whatever the provider.
_Avoid_: policy (an ASP.NET host concept), guard, permission check

**Permission checker**:
The one synchronous decision whether the current caller holds a permission (`IPermissionChecker`). The
`[RequirePermission]` gate, handler code, members visible by permission and the client snapshot all ask it, so a host
changes what a caller holds in one place.
_Avoid_: `HasClaim` on the permission claim type, permission service

**Member visible by permission**:
A response member marked `[VisibleWith]`: the handler fills it for everybody, and the handler pipeline answers it as
null for a caller who does not hold the permission.
_Avoid_: field-level security (a database concept), masked field

**Decision**:
A question a valid command asks its caller before it runs ("import this possible duplicate?"), declared with
`[RequiresDecision]`, asked with `AppError.DecisionRequired` and answered through the request's `Decisions`, once or
per item.
_Avoid_: confirmation flag, confirm conflict

**Feature gate**:
A runtime gate on a handler, keyed by a declared feature flag. A closed gate answers with the generic
not-found outcome so the flag's name is not leaked. It is not module enablement.
_Avoid_: feature toggle, feature switch, module flag

**Feature variant**:
A service implementation selected per request by a feature flag's variant.
_Avoid_: A/B implementation, strategy flag

**Configuration variant**:
A service implementation selected once per process by configuration.
_Avoid_: feature variant (a different thing), environment switch

**Principal directory**:
The stored side of who a principal is: which roles and which effective claims each principal holds, as
queries. It lets a resource rule list the principals a record is visible to, and it must agree with the
principal the host builds at sign-in.
_Avoid_: user directory, permission holders, role store

**Client capability**:
The read-only projection of modules, flags and grants a frontend receives to adapt its user interface. It
hides and adapts; it never enforces.
_Avoid_: permission (on the client), client-side authorization

### Events and live updates

**Domain event**:
An event delivered inline, in the caller's scope and transaction. Its consumers share the transaction, and a
failing consumer fails the command.
_Avoid_: internal event, in-process event, notification

**Integration event**:
An event recorded with the caller's unit of work and delivered after commit in an independent scope; the only
broker-portable plane.
_Avoid_: external event, message (alone), domain event (a different plane)

**Event plane**:
One of the two transaction-defined ways an event travels, domain or integration. An event belongs to exactly
one.
_Avoid_: channel, bus (which names the pub/sub mechanism), tier

**Consumer**:
Code that reacts to an event. The handler form runs the full handler pipeline; the method form is for a
lightweight side effect that does not need it. A consumer never returns a reply.
_Avoid_: subscriber, listener, event handler (which reads as a handler)

**Outbox**:
The store in which integration events are written with the caller's unit of work, from which they are
delivered durably after commit.
_Avoid_: message queue, event log

**Inbox**:
The durable idempotency store that dedupes handler-form integration consumers.
_Avoid_: dedupe table, processed-messages table

**Client event**:
An after-commit, at-most-once, latest-wins hint sent to the browser, telling it to query again. It carries a
light payload and is not a durable event plane.
_Avoid_: push notification (which names Web Push), real-time event, live update, message

**Ordered stream**:
A sequenced stream for a key that has one live sequencer, used only when a lost message makes the consumer
wrong and not merely stale.
_Avoid_: event stream, feed, client event (a different guarantee)

### Actors and connections

**Actor**:
A mailbox-protected, in-memory state machine with a generated typed facade. It is justified only when the
consistency unit is a live in-memory thing, never as a substitute for a table with a version column.
_Avoid_: grain, agent, worker, service

**Placement**:
Where an actor is activated: `Local` (on whichever node asks, the default, so several nodes may host one
key), `SingleHome` (one leased node is authoritative) or `VirtualShards` (each keyed actor belongs to one of a
fixed set of role-lease partitions).
_Avoid_: single-homed flag, clustering, distribution mode

**Role lease**:
A PostgreSQL lease that makes exactly one instance hold a long-lived application role. It is not a
distributed-lock API.
_Avoid_: leader lock, distributed lock, mutex

**Connection**:
A bidirectional client link managed by the connection seam. It is justified only for interactive command
rates, a link that is itself state (a device link), or server-to-client calls.
_Avoid_: socket, session (a per-connection state object), channel

**Adapter**:
The part of a connection that owns framing and the transport lifecycle (WebSocket, TCP, simulated).
_Avoid_: transport (which names a handler projection), driver

**Codec**:
The part of a connection that encodes and decodes its protocol.
_Avoid_: serializer, parser, framer (which is the adapter's job)

### Persistence and files

**Staged upload**:
A blob written in parts through the protocol-neutral staged-upload seam and temporary (pending) until the
caller's transaction commits it.
_Avoid_: multipart upload, tus upload (one protocol over the seam), temp file

**ElarionFile**:
The small, in-memory file payload of a handler. Large exports and uploads use the staged-blob tier instead.
_Avoid_: attachment, stream (for a small file), form file

**Uploaded file**:
A handler parameter (`UploadedFile`) naming a file the caller staged through an upload endpoint; a blob-id string on
the wire. The handler reads, keeps or discards it only through `IUploadedFiles`, which answers the caller's own
pending upload and nothing else.
_Avoid_: blob ref, file id, attachment id (for the parameter), upload token

**Upload limits**:
The size, content-type and file-extension limits an uploaded-file member declares (`[UploadLimits]`). The pipeline
applies them to every read and keep of that file, the schema exports them, and the generated client checks a file
against them before uploading it. A file that breaks one is refused with the limit as typed error data. Constraints
passed when reading the file apply on top. What may be staged at all is the endpoint's, or the upload policy's.
_Avoid_: upload validation, file rules

**Upload policy**:
A named set of staging limits and an optional check that a client names when it stages a file (`?policy=` on the
direct endpoint, the `policy` key of the tus metadata). Its limits replace the endpoint's for that upload, and its
check runs before the body is read. It decides what may be staged; a handler's upload limits still decide what it
accepts.
_Avoid_: upload target (the generated client's method and member pair), upload profile, staging route

**Streamed file**:
The file response of an HTTP-only handler (`ElarionStreamedFile`): content written to the response body after
the handler pipeline has run, never buffered. A deferred streamed file decides after the pipeline, before its first
byte, which file it is or that it is refused. Only HTTP carries it; JSON surfaces use an ElarionFile or a
pending blob.
_Avoid_: file stream, push stream, download result

**Migration plan**:
The one ordered sequence in which SQL scripts, C# code steps and EF migrations run, with one version
sequence, one history and one lock; roll-forward only.
_Avoid_: migration set, changelog, schema history (which is one part of it)

**Rule**:
A domain derivation or condition written once as an expression (`Rule<TInput, TResult>`): invoked inside a
query, where `Expand()` inlines it for the provider to translate, and run in memory, compiled. Business rules
that decide a command still live in the handler; a rule is how the handler and its queries share a definition.
_Avoid_: specification, computed property, predicate helper, expression snippet

**Previewable command**:
A command whose request implements `IPreviewableRequest`: the same handler previews (runs, reports its outcome
stamped with a preview fingerprint, rolls back) and commits, and a commit sent with the fingerprint commits only if
its outcome is unchanged, otherwise it fails with `preview.changed`.
_Avoid_: dry-run handler, preview endpoint, simulate mode, separate preview and commit operations

### Frontend

**Contribution**:
An entry a frontend module adds to a typed extension point (a sidebar item, an action). It composes the user
interface; it is neither a route system nor a security model.
_Avoid_: plugin, widget, slot content

**Extension point**:
A typed place in the frontend shell that modules contribute to.
_Avoid_: slot, seam (a backend term here), hook

**Request preset**:
A named value of a handler's request type (`[RequestPreset]` on a static readonly field of the handler), exported
under its method and offered to the client as typed params: a list's quick filter that the server counts with and the
client opens with the same instance.
_Avoid_: saved filter, quick filter (for the declaration), view, chip (the UI that shows it)

**Client constants**:
The constants a module exports to clients (`[ClientConstants]` on a static class): values both sides read from one
declaration, exported per module and given to the generated client as literal types. Export is a declaration, never
every public constant.
_Avoid_: shared constants, config values, enums (for the export)

### Repository

**Spec**:
The file in `specs/` that states what a feature must do: user scenarios, numbered requirements, success
criteria. The source the code is built from.
_Avoid_: requirements doc, story, ticket

**Plan**:
The `.plan.md` file next to a spec that states how the feature is built.
_Avoid_: design, technical spec

**ADR**:
One record in `docs/decisions/` of one architecturally significant decision. It is rationale, and not
necessarily the current API.
_Avoid_: design doc, RFC, decision log entry

**Published page**:
An `.mdx` file under `docs/` that the documentation website renders; the description of record for shipped
behavior.
_Avoid_: doc (ambiguous), wiki page, article

**Bundle**:
The `.md` pages of `docs/` as an Open Knowledge Format bundle: every page with frontmatter, an index, a log.
_Avoid_: wiki, knowledge base

**Router**:
A `CLAUDE.md` whose only content is `@AGENTS.md`.
_Avoid_: stub, link file

**Pattern rule**:
A file in `.github/instructions/` holding the rules for the files its `applyTo` globs match. It is the source;
the file of the same topic in `.claude/rules/` is its mirror and only points at it.
_Avoid_: instruction file, scoped prompt, rule copy

# ADR-0039: File payloads — the in-memory `ElarionFile` tier and the staged-blob tier

- Status: Accepted
- Date: 2026-07-06
- Related: [ADR-0026](0026-openapi-http-transport.md) (OpenAPI is the REST contract),
  [ADR-0031](0031-imperative-handler-transport-mapping.md) (why HTTP mapping stays concrete),
  [ADR-0035](0035-protocol-neutral-staged-upload-seam.md) (resumable uploads feeding the staging area),
  [ADR-0040](0040-host-declared-module-endpoints.md) (the sibling seam for endpoints a handler cannot express),
  the [http-endpoints](../capabilities/transports/http-endpoints.mdx), [json-rpc](../capabilities/transports/json-rpc.mdx),
  and [blob-storage](../capabilities/blob-storage.mdx) docs.

## Context

Handlers had no way to say "I receive files, I return files". `[HttpEndpoint]` responses were JSON-only
(downloads impossible — the adoption-feedback trigger was an `.xlsx` export that had to move out of its
module into a hand-mapped host route), uploads existed only as the HTTP-specific `IFormFile` binding, and
the name-routed transports (JSON-RPC/MCP) had no file story at all. Whatever fills the gap must keep
handlers web-free, work under the AOT-strict serializer, and not pretend one mechanism suits every file
size — a 50 KB avatar and a 500 MB import are different problems.

## Decision

File handling is **two-tiered by payload size**, and the handler declares intent once; each transport then
carries the payload the way that suits it best.

### Tier 1 — `ElarionFile`: the in-memory payload for small files (rule of thumb: up to ~4 MB)

`ElarionFile` (in `Elarion.Abstractions`, next to `Result<T>`) is deliberately **a pointer to a block of
memory**: content bytes, `ContentType`, optional `FileName` — nothing else. No stream, no disposal, no
conditional-request knobs; a type simple enough to cross every wire. A handler returns
`Result<ElarionFile>` or accepts `ElarionFile` request properties (uploads), and:

- **HTTP** maps a file response through `ElarionHttpResults.ToFileResult` → a real file download
  (`Content-Type`, `Content-Disposition: attachment` when `FileName` is set; RFC 7807 on failure). Request
  properties bind from the JSON body like any other DTO member. The OpenAPI document advertises the
  generic binary content type upgraded to `type: string, format: binary` via the endpoint marker.
- **JSON surfaces** (JSON-RPC params and results, MCP tool results, idempotency/cache replay) carry the
  canonical base64 envelope of `ElarionFileJsonConverter` — `{ "contentType", "fileName"?, "data" }`,
  fixed camelCase names independent of the host's naming policy, unknown properties skipped. The type is
  seeded into `ElarionFrameworkJsonContext`, so it resolves under the AOT-strict default with no app
  registration; request-DTO properties ride the app's own source-gen context, which delegates to the same
  converter.
- **The exported schema marks it as a file** (`x-elarion-file: true` on the envelope schema, `data` as
  `format: "byte"`), and the TypeScript client generator maps it to a **native `File`**: params accept a
  `File` (validated with `z.instanceof(File)`), results materialize one; the client converts to/from the
  envelope at the call boundary (encoding runs even with validation disabled). Wire shape and generated
  ergonomics stay decoupled.

The cutoff intuition: base64 adds ~33%, the payload is buffered end to end, and default request-body
limits sit at tens of MB — all fine at single-digit megabytes, all wrong beyond that. `IFormFile` remains
the HTTP-native escape hatch when multipart binding itself is the requirement.

### Tier 2 — staged blobs: streaming for large files

Large files never enter a DTO — they move through the **pending (staging) blob area**, and handlers see
only a pointer:

- **Import**: the client uploads through the provided endpoints (`MapElarionResumableBlobUploads` for resumable uploads,
  `MapElarionBlobUploads` for direct ones) into the staging container as a **pending, owner-scoped blob**
  and passes the returned reference to the handler (any transport — it's a string). The handler streams
  from `IBlobStore.OpenReadAsync` and processes; a blob it never commits is swept by the blob garbage
  collector after its TTL.
- **Export**: the handler streams the artifact into the store as a **pending blob owned by the current
  user** and returns its `BlobRef`; the client downloads it from the new owner-scoped streaming endpoint
  (`MapElarionBlobDownloads`: `GET {prefix}/{blobId}`, exact-owner check, foreign/missing/unowned → 404,
  the download handle disposed with the response). The pending state doubles as temp-file semantics: an
  export nobody downloads (or commits) expires via GC on its own.

This deliberately reuses the ADR-0035 lifecycle instead of inventing a temp-file subsystem: "staging area
+ TTL + GC" already is one.

## Alternatives considered

- **Keep downloads out of handlers (status quo: `[Service]` + hand-mapped host route).** Rejected — it
  duplicates the feature gate by hand, drops the transport-neutral authorization pipeline, and every
  consuming app rebuilds the same translation. This was the direct adoption-feedback pain.
- **A generic escape hatch (`Result<IResult>` or handler-returned ASP.NET types).** Rejected — it would
  pull the shared framework into handler signatures, breaking the web-free application-assembly split.
- **A streaming `ElarionFile` (stream + owner-disposal support, conditional-request headers).** Built,
  then removed: it blurred the tiers. A stream inside a DTO cannot survive JSON serialization, cannot be
  replayed by the idempotency/cache stores, and invited exactly the large-payload use the type is wrong
  for — while the staged-blob tier streams properly with resumability and GC. Bytes-only keeps the
  interface honest ("a block of memory") and deleted the owner-lifetime machinery.
- **Excluding file responses from JSON-RPC/MCP (a compile-time `ELRPC004` warning).** Rejected after
  adoption feedback: it made file handlers second-class on the bus for no structural reason — the envelope
  costs one converter plus one schema substitution and covers uploads for free. Transports stay symmetric;
  efficiency is the caller's per-payload choice, not a framework prohibition.
- **Native MCP binary content blocks for tool results.** Deferred — it special-cases one transport's
  result shaping; the envelope keeps all JSON surfaces identical. Revisit if agent tooling grows real
  file consumption.
- **Generic multipart binding for `ElarionFile` request properties on HTTP.** Deferred — the JSON envelope
  keeps one client code path across all transports at the sizes this tier targets; multipart stays
  available via `IFormFile`, and the staged-blob tier covers everything larger.

## Consequences

- "I can receive files, I will return files" is one declaration on the handler; HTTP streams downloads,
  JSON surfaces carry base64, the generated TS client speaks native `File` — and the schema documents all
  of it.
- Small-file handlers are trivially testable (bytes in, bytes out) and compose with `[Cacheable]`/
  `[Idempotent]` (the envelope replays like any JSON response).
- Large-file flows get streaming, resumability (tus), ownership scoping, and automatic cleanup from the
  existing blob machinery, plus one new download endpoint — no new subsystem.
- The size cutoff is guidance, not enforcement: request-body limits and the blob upload cap remain the
  hard stops; docs recommend switching tiers around a few megabytes.

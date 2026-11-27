---
type: Architecture Decision Record
title: "ADR-0098: HTTP-only endpoints stream files through ElarionStreamedFile"
description: "An [HttpEndpoint] without [Handler] may respond with ElarionStreamedFile, a file written to the body after the pipeline; JSON surfaces and replaying decorators refuse it at compile time."
status: draft
---
# ADR-0098: HTTP-only endpoints stream files through ElarionStreamedFile

- Status: Proposed
- Date: 2026-11-21
- Amends: [ADR-0039](0039-binary-file-responses.md) (file payloads: the in-memory and the staged-blob tier)
- Related: [ADR-0071](0071-generator-owned-http-endpoint-binding.md) (generated `RequestDelegate` endpoints),
  [ADR-0040](0040-host-declared-module-endpoints.md) (host-declared module endpoints)

## Context

ADR-0039 split file payloads into two tiers: `ElarionFile`, a block of memory every transport carries, and the
staged-blob tier, where a handler returns a pointer and the client downloads the blob from the owner-scoped download
endpoint. A streaming `ElarionFile` had been built and removed, because a stream cannot cross the JSON surfaces or be
replayed by the idempotency and cache stores.

That objection holds for a handler the JSON surfaces carry. It does not hold for a handler mapped with
`[HttpEndpoint]` alone, which no JSON-RPC, MCP or connection client can call. Applications with such handlers still
had no way to stream: a stored document served to its readers (a record-owned file, not the uploader's own pending
blob), or a ZIP of many documents built per request. The staged-blob tier does not fit either — the download endpoint
serves only a blob's exact recorded owner, and an archive would first have to be written as a blob of its own — and
`ElarionFile` buffers the whole file. So applications mapped minimal-API routes in the host. There they lost what the
handler pipeline gives: `[RequirePermission]`, the module's feature gate, validation and the declared error contract
had to be repeated by hand in the host, where an application had to keep a second copy of each document's read rule.

Three more things those routes had to solve each time:

- **Presentation.** "Show" a PDF in a browser tab needs `Content-Disposition: inline` with the file name; a preview tile
  needs an `ETag` and a `Cache-Control`. `ElarionFile` has neither, by ADR-0039's design (no conditional-request
  knobs).
- **Sniffing.** A stored upload served from the application's origin must carry `X-Content-Type-Options: nosniff`,
  or the browser may render a `text/plain` upload as HTML. No framework file response set it, the blob endpoints
  included.
- **ZIP and synchronous I/O.** `ZipArchiveEntry.OpenAsync` returns a stream that does not override `DisposeAsync`;
  `await using` falls back to the synchronous `Dispose`, which writes the entry's trailer synchronously. Kestrel
  refuses synchronous body writes, and by then the `200` is sent, so the download hangs. Applications turned on
  `AllowSynchronousIO` for the response. It is a BCL gap, but every application streaming an archive meets it.

## Decision

- **`ElarionStreamedFile`** (`Elarion.Abstractions`, next to `ElarionFile`) is the response of an HTTP-only handler:
  content type, file name, a `FileDisposition` (`Attachment`, the default, or `Inline`), an entity tag (sent as a
  strong `ETag`), `LastModified` and `CacheControl`. Its content is either a stream (`FromStream`, with an optional
  owner disposed after it, such as the `BlobDownload` whose stream it is) or a writer (`FromWriter`) that receives the
  response body. It owns its content and is `IAsyncDisposable`; the HTTP layer disposes it with the response.
- **The generator** maps `Result<ElarionStreamedFile>` through `ElarionHttpResults.ToStreamedFileResult`, with the same
  binary OpenAPI advertisement as `ElarionFile`. The content runs after the pipeline, in the request's scope, after the
  handler's transaction. It reuses ASP.NET's stream results, so `If-None-Match` and `If-Modified-Since` answer `304`
  without running the content.
- **Only HTTP carries it, enforced at compile time.** A `[Handler]` that responds with it is error `ELRPC005` and no
  operation is registered; an `[Idempotent]` or `[Cacheable]` one is error `ELHTTP007` and the decorator is not
  attached. That keeps ADR-0039's guarantee for every surface the objection applies to.
- **Every framework file response carries `nosniff`**: `ElarionFile` and `ElarionStreamedFile` responses and the blob
  upload, cancel and download endpoints.
- **A streamed file always states a disposition.** The browser never decides by itself to render it; inline is an
  explicit choice, documented as safe only for content types the browser renders safely.
- **The writer's body tolerates small synchronous writes.** It holds them (up to 1 MiB at a time) and sends them with
  the next asynchronous write, so the async `ZipArchive` APIs work without `AllowSynchronousIO`. A writer that writes
  everything synchronously fails at the limit instead of buffering the file.
- **A failure after the first byte aborts the connection**, so a client never takes a truncated file for a complete
  one; before it, the failure is an ordinary unexpected error.

## Consequences

- An application streams stored documents and on-the-fly archives from handlers again, and deletes the host routes
  together with their hand-copied authorization.
- Two transports differ in what a handler can return: a streamed file exists on HTTP only. The diagnostics make that a
  compile-time fact, not a runtime surprise, and the handler's author chooses it by leaving out `[Handler]`.
- The generated TypeScript client does not cover these endpoints (they are not in the JSON-RPC schema); a browser
  downloads them by URL, which is what a file link wants.
- The content runs outside the handler's transaction. A content that needs a consistent snapshot opens what it reads
  in the handler; a blob read opened there streams from the database without buffering.
- The synchronous-write allowance is a workaround for one BCL gap. When `ZipArchiveEntry`'s stream writes its trailer
  in `DisposeAsync`, the allowance costs nothing and can stay.

## Amendment (2026-11-23): a file decided after the pipeline

An application serves the attachments of stored e-mails. Which attachment a request names, and its name and type, are
known only while parsing the stored message. A streamed file had to know its content type in the handler and could
not answer `404` once the pipeline had finished, so the application either parsed the message twice (once in the
handler to decide, once in the writer to send) or kept a host-mapped route.

**`ElarionStreamedFile.Deferred(open)`** is a file whose `open` runs after the pipeline, where the content runs, and
answers `Result<ElarionStreamedFile>`: the file to serve, with its own name, type and caching headers, or an
`AppError`. The HTTP layer opens it before writing anything, so a refusal is the ordinary problem response with its
status and code, and the file is read once. `OpenAsync` gives an in-process caller the same answer; `WriteToAsync` on
an unopened deferred file throws.

Decisions:

1. **The open answers a whole file, not headers and a body.** Name, type, entity tag and the content come from the same
   read, so the response cannot state the metadata of one read and send the bytes of another, and conditional requests
   (`If-None-Match`) work as for any streamed file.
2. **One type, not a new response type.** The generator, the diagnostics (`ELRPC005`, `ELHTTP007`) and the OpenAPI
   mapping stay as they are. The cost: a deferred file is a placeholder whose own presentation properties are ignored,
   which its documentation says.
3. **An open may not answer another deferred file.** One level is what the case needs; a chain would only hide where
   the decision is made.
4. **The open disposes what it opened when it refuses.** The framework sees only the result; a refusal carries no
   resource to dispose.

Rejected: **a writer that may refuse before its first byte** (a callback context with `Refuse(error)` and late
headers) — it mixes deciding with writing, and a refusal after a flush could no longer change the status; **parsing in
the handler** — it runs inside the transaction and holds the database connection for the whole parse, which is what the
post-pipeline content exists to avoid.

## Amendment (2026-11-27): a stated length

A content that cannot seek has no length ASP.NET can read, so the response went out chunked even when the reader knew
the size beforehand (a decoded message part whose size the store recorded), and a browser showed no progress.
`ElarionStreamedFile.Length` states it:

1. **Optional, on the served file.** `with { Length = … }` on `FromStream` or `FromWriter`; a `Deferred` file carries
   none, like its other response properties, and the file its open answers states it.
2. **Sent with the body.** The forward-only content and the writer run inside ASP.NET's push result, which writes them
   only after the conditional checks; `Content-Length` is set there, so a `304` never carries it.
3. **Checked.** The body counts the bytes: a write past the length fails at that write (the excess is never sent), a
   content that ends short fails at its end. Both come after the first byte, so the connection aborts, as for any other
   failure mid-stream; a wrong length is a broken file, not a smaller one.
4. **A seekable stream keeps its own.** It still states its length and serves ranges through ASP.NET's stream result; a
   stated length that disagrees with what is left from its position fails before the first byte.

Rejected: **trusting the length without counting** — Kestrel catches a mismatch, but other servers (the test server,
a proxy) differ, and a short body that completes looks like a complete file; **ranges for forward-only content with a
length** — a range needs to skip, which a forward-only stream can do only by reading, so it stays a seekable stream's
feature.

## Note (2026-11-27): .NET 11 closed the `ZipArchive` gap

Probed on this repository's toolchain: an archive written with `ZipArchive.CreateAsync`, `ZipArchiveEntry.OpenAsync`
and `await using` into a stream that refuses synchronous writes fails on .NET 10.0.15 (the entry stream's
`Dispose` writes the deflate trailer and the data descriptor synchronously, with `Fastest` and `NoCompression`
alike) and completes with only asynchronous writes on .NET 11.0.0. The body that holds synchronous writes stays: the
packages still target `net10.0`, and on `net11.0` it costs nothing when nothing is held. Revisit it when `net10.0`
is dropped.

## Rejected alternatives

- **A stream on `ElarionFile`.** Rejected again, for ADR-0039's reasons: the type crosses every surface, and a stream
  cannot. A separate type keeps `ElarionFile` honest and lets the generator refuse the combination.
- **Host-mapped routes with documented gates.** The status quo. It duplicates authorization and the feature gate per
  route, which is what handlers exist to avoid.
- **`Result<IResult>` or another ASP.NET type in handler signatures.** It would pull the shared framework into
  application assemblies (ADR-0039).
- **Turning on `AllowSynchronousIO` for streamed-file responses.** It would let a writer block a thread for the whole
  file. Holding the few synchronous bytes keeps the response asynchronous and bounds the exception.
- **Opt-in `nosniff`.** A file served with a correct content type loses nothing by it, and the risk it closes is an
  upload rendered as a page of the application. A default that must be remembered is the wrong default.

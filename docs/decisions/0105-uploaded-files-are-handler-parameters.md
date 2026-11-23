---
type: Architecture Decision Record
title: "ADR-0105: An uploaded file is a handler parameter"
description: "A handler takes a staged upload as an UploadedFile, a blob-id string on the wire, and reads, keeps or discards it only through IUploadedFiles, which answers the caller's own pending upload and nothing else."
status: stable
---
# ADR-0105: An uploaded file is a handler parameter

- Status: Accepted (implemented 2026-11-22)
- Date: 2026-11-22
- Related: [ADR-0035](0035-protocol-neutral-staged-upload-seam.md) (the staged-upload seam),
  [ADR-0039](0039-binary-file-responses.md) (the in-memory and the staged-blob file tiers),
  [ADR-0098](0098-streamed-files-for-http-only-endpoints.md) (streamed files),
  [ADR-0080](0080-errors-are-a-declared-contract-with-a-stable-code.md) (declared errors),
  [ADR-0082](0082-requiredness-comes-from-nullability-in-both-directions.md) (requiredness from nullability),
  [ADR-0100](0100-the-schema-exports-constants-presets-and-limits.md) (typed limits in the schema, proposed)

## Context

An application built on Elarion carries 60 request members that hold a file as base64 JSON. Its bank-statement import
is a wizard: analyze, preview (any number of times), commit. Every step resends the statement, up to 28 MB of base64,
buffered whole on the client and on the server, a third larger than the file, and close to the request-size limits of
the JSON-RPC host. ADR-0039 named the cure, the staged-blob tier, but only for exports and for handlers that read a
blob id from a plain string.

The upload half of that tier exists. The direct endpoint (`MapElarionBlobUploads`) and the tus endpoint
(`MapElarionResumableBlobUploads`) stream the bytes into a **pending** blob in the container `uploads`, record the
uploader as `BlobMetadata.OwnerId`, and answer the blob id. `IBlobLifecycle.CommitAsync` keeps a pending blob inside
the caller's transaction; the blob garbage collector removes it once its time to live passes.

What was missing is the step from the endpoint to the handler. The documented pattern passed the blob id as a string
member and called `IBlobStore.OpenReadAsync` or `IBlobLifecycle.CommitAsync` with it. Neither checks anything: the
examples did no owner, container or state check. Any caller who learned an id — from a log line, a shared screen, a
URL, an error report — could read another user's pending upload through an import handler, or commit it into a record
of their own. Each application had to rediscover and repeat the checks, and the obvious code was the unsafe one.

The schema/client chain did not help either. A string member says nothing about uploads, so the generated client could
not upload a file for the caller; each frontend hand-wrote an upload call before each method that takes one.

## Decision

1. **`UploadedFile`** (`Elarion.Abstractions`, next to `ElarionFile`) is the handler parameter type: a sealed record
   with `Id`, the blob id the upload endpoint answered with. On every JSON surface it is a plain string
   (`UploadedFileJsonConverter`), the form the endpoints return and a client stores between steps. It is seeded into
   `ElarionFrameworkJsonContext`, as `ElarionFile` is.

2. **`IUploadedFiles`** (`Elarion.Abstractions`) is the only way from the reference to the bytes:
   - `OpenAsync(file, constraints?, ct)` answers `Result<UploadedFileContent>`: the open stream with `ContentType`,
     `FileName` (the leaf of the stored name `{owner}/{guid}/{fileName}`), `Length` and `ContentSha256`
     (ADR-0099). The upload stays pending, so each step of a wizard opens it again.
   - `CommitAsync(file, constraints?, ct)` keeps it through `IBlobLifecycle.CommitAsync`, inside the caller's unit of
     work, and answers `Result<CommittedUpload>` (`BlobId` plus the same description) for the record to store.
   - `DeleteAsync(file, ct)` discards it early; idempotent, and a no-op for anything that is not the caller's pending
     upload.

   Every operation requires, failing closed: the blob exists; it is **pending** (a committed blob belongs to a record
   and that record's authorization, so it is not an upload any more); its recorded owner is **exactly**
   `ICurrentUser.UserId` of an authenticated caller (never parsed from the name; an unowned blob matches nobody); it
   lies in an **upload container** (`UploadedFilesOptions.Containers`, default `uploads`, the container of both
   endpoints). Every failure is the same `AppError.Validation` with code `upload.not_found` and one message, so ids
   cannot be probed; the cause is logged at debug level only. After those checks, the handler's
   **`UploadConstraints`** apply: `MaxBytes` (`upload.too_large`) and `ContentTypes` (`upload.content_type`; exact,
   case-insensitive, parameters ignored, as the direct endpoint compares them).

   The kind is `Validation`, not `NotFound`: the reference is an input of the operation, and a stale or foreign one is
   an invalid input the caller fixes by uploading again; the operation's own resource was not missing. It also lets a
   form mark the file field like any other invalid field.

3. **The implementation lives in `Elarion.Blobs`** (`AddElarionUploadedFiles`, scoped, over the registered
   `IBlobStore`, `ICurrentUser` and optional `IBlobLifecycle`), so it serves both endpoints and every blob provider.
   `Elarion.Blobs` therefore references `Elarion.Abstractions`, as the other provider-neutral cores (`Connections`,
   `ClientEvents`) do. `CommitAsync` without a registered `IBlobLifecycle` throws, because keeping an upload is
   impossible there and silently succeeding would leave it to the collector.

4. **Errors are the handler's declaration.** The handler returns these errors from its own call, so it declares them
   with `[ProducesError(ErrorCodes.UploadNotFound, ErrorKind.Validation)]` and the two constraint codes it can
   produce, like any error a service it calls returns (ADR-0080). They are not implied by the generator: the implied
   errors are those the pipeline adds, and which upload codes occur depends on the constraints the handler passes.
   (Changed by the 2026-11-23 amendment: declared `[UploadLimits]` imply them, with typed payloads.)

5. **The schema marks the member.** `JsonRpcSchemaExporter` exports an `UploadedFile` as
   `{ "type": "string", "x-elarion-upload": true, "description": … }`, with `null` added for a nullable member
   (ADR-0082). The exporter leaves out the `items` of a collection whose element is converter-backed, so the exporter
   puts them back for `UploadedFile` collections. MCP tool input schemas share the builder; a tool caller passes the
   id.

6. **The generated client uploads.** An upload member is typed `File | Blob | UploadedFileRef` in params and
   `UploadedFileRef` (an exported `string` alias in `rpc-types.ts`) in results. After params validation the client
   uploads every `File`/`Blob` at an upload position through `RpcClientOptions.uploadFile` and sends the reference;
   the default uploader POSTs the raw body to `/_elarion/blobs` on the origin of the RPC url (`uploadUrl` overrides
   it) with the client's fetch and headers, the `Content-Type` of the blob and the file name in
   `X-Elarion-File-Name` (or the `fileName` query parameter for a name outside printable ASCII, which a header cannot
   carry), and reads the id from the text body. `client.uploadFile(file)` / `rpc.$uploadFile(file)` uploads once and
   answers the reference, for a wizard. A schema without upload members generates byte-identical output.

7. **Lifetime is the endpoints' `Ttl`.** An upload lives until it is committed, deleted, or collected after the
   endpoint's `Ttl` (default 30 minutes). A wizard that needs longer raises the `Ttl`; there is no sliding expiry.

## Consequences

- A large file crosses the network once, streamed, and a wizard passes a short string to every step. A base64 member
  becomes an `UploadedFile` member whose caller passes a `File`, or a reference it uploaded before.
- The owner, state and container checks live in one place and fail closed; the published examples stop showing the
  unchecked pattern.
- `CommitAsync` checks and then commits; `IBlobLifecycle.CommitAsync` is idempotent and cannot report "already
  committed". Two concurrent commits of the same upload by the same user (a double submit) can attach one blob to two
  records. A committing handler should be `[Idempotent]`.
- Uploading inside a call means a retried call that carries the `File` uploads it again and sends another id. For an
  `[Idempotent]` operation the retry then differs from the first request and answers `idempotency.key_reused`: such a
  caller uploads once with `uploadFile` and retries with the reference.
- `BlobMetadata` carries no expiry, so an upload whose `Ttl` has passed still opens until the collector removes it
  (bounded by the collector's polling interval and safety margin). The collector never removes a committed blob, so
  this cannot lose a kept file.
- The default uploader uses the direct endpoint, which is not resumable. A client that uploads through tus passes the
  `Elarion-Blob-Ref` as the reference, or supplies `uploadFile`.
- Outside this decision: the direct endpoint's cancel (`DELETE {prefix}/{id}`) and download routes check the owner and
  container but not the state, so the uploader can still delete or download a blob after it was committed to a
  record.
- Constraints are passed when the file is read, not declared on the member, so the schema does not carry them. When
  ADR-0100's typed limits land, an upload limit can be exported the same way. (Done: see the amendment below.)

## Amendment (2026-11-22): limits are declared on the member

[ADR-0100](0100-the-schema-exports-constants-presets-and-limits.md) gives the constraints a declared form:
`[UploadLimits(MaxBytes = …, ContentTypes = […])]` on the `UploadedFile` member. The pipeline declares them to the
scoped `IDeclaredUploadConstraints` before the handler runs, and the `Elarion.Blobs` reader checks them on every open
and commit of that file, on top of the constraints a call passes; the schema exports them next to `x-elarion-upload`,
and the generated client rejects a file that breaks them before uploading it. Constraints passed at read time still
work and still apply. The rejected alternative "per-member limit attributes" below was rejected for as long as no
schema support existed; ADR-0100 supplies it with one attribute for both limits.

## Amendment (2026-11-23): typed refusals, file extensions and a targeted upload

An application that moved 27 import methods to uploaded files reported four gaps. The constraint errors were English
text with the limit in bytes, on the server and in the client's refinement, so it worded them by code and re-checked
the size itself. The content type a browser declares for `.xlsx`, `.vcf` and `.ics` varies by system and is often
missing (sent as `application/octet-stream`), so a content-type list refused real files. `uploadFile(file)` knew no
method and checked no limit. And `DeleteAsync` on the PostgreSQL store saved the handler's tracked changes with it.

1. **A refusal carries its limit as typed data.** `upload.too_large` carries `UploadTooLargeData` (`MaxBytes`,
   `Length`), `upload.content_type` `UploadContentTypeData` (the accepted `ContentTypes`, the file's `ContentType`),
   and the new `upload.file_extension` `UploadFileExtensionData` (the accepted `FileExtensions`, the stored
   `FileName`). The payloads live in `Elarion.Abstractions` and are seeded into `ElarionFrameworkJsonContext`, as
   `ValidationErrorData` is, because `AppError.Data` is an object slot no application context reaches. The message
   stays, as the English fallback.

2. **Decision 4 changes: declared limits imply the upload errors.** With `[UploadLimits]` the codes are statically
   knowable, exactly where the upload-limits decorator attaches: the generator implies `upload.not_found` and, for each
   kind of limit the request declares, its code with its payload, so the schema and the generated client type `data`
   without the handler declaring anything. A handler that only passes `UploadConstraints` still declares the codes it
   can produce, now with their payload. Because the handler's own declaration wins over an implied one, a declaration
   of an upload code with another payload — typically none, as written before the payloads existed — would hide the
   typed data; the generator reports it as the warning `ELERR004`.

3. **Accepted file extensions.** `UploadConstraints.FileExtensions` and `[UploadLimits(FileExtensions = […])]`
   accept a file by the end of the name the upload endpoint stored: each extension with its leading dot, compared
   case-insensitively, the name longer than the extension (a file without a name is stored as `upload` and has none).
   A declared extension that is not a dot followed by a name without white space or path separators is `ELUPL001`.
   Extensions are one more limit: every declared limit must hold, so a member that declares both types and extensions
   requires both, and a format with an unreliable type is declared by extension alone. They are exported as
   `x-elarion-file-extensions` next to the other limits (JSON-RPC schema, OpenAPI, `rpcLimits`), and the client checks
   them against the name it sends.

4. **The client words nothing itself.** Its refinements carry `params: { code, <limit> }` on the Zod issue, for an
   error map in the application's language. `rpc-schemas.ts` exports `rpcUploadLimits`, every upload member of every
   method by path (a list by its own path) with its limits, whenever params take an uploaded file, so `rpc-client.ts`
   stays independent of the declared values. `uploadFile(file, { method, member })` takes the pair typed from it,
   checks the file against that member's limits before uploading it (skipped with `validateParams: false`), and throws
   `RpcUploadLimitError`: an `RpcError` with the code, message and data the server would answer, plus the method and
   member, so one handler words the client's refusal and the server's.

5. **`DeleteAsync` is documented, not changed.** The PostgreSQL store removes the row through the scoped `DbContext`
   it shares with the handler and saves it, which also writes whatever the handler still tracks. The interface's
   documentation and the published page now say to call it after the handler's own save.

Rejected alternatives of this amendment:

- **An `ErrorMessage` on `[UploadLimits]`, as DataAnnotations has.** One attribute carries up to three limits, so one
  message cannot fit each refusal. A resource message is resolved when the schema is exported, in one language, as the
  DataAnnotations messages are, so it would not follow the user's language either. The client already knows the
  user's language; code and data give it the facts to word every refusal.
- **A sniffed content type.** Reading the bytes before the handler is the pre-handler check rejected above; magic
  numbers tell an `.xlsx` from any other zip archive no better than its name does, and `.vcf` and `.ics` are text.
- **Extensions as an alternative to the types (either one accepts).** A declaration would then loosen another one;
  declarations only tighten.
- **Completing a payload-less declaration with the implied payload.** Everywhere else the declaration closest to the
  handler wins; a warning keeps that rule and makes the migration visible.
- **`uploadFile` throwing `RpcParamsValidationError`.** The client would have to build a Zod error itself, and the
  application would handle the same refusal in two shapes, one from the client and one from the server.
- **Deleting without saving the shared context (`ExecuteDelete`).** It changes `IBlobStore.DeleteAsync` for every
  caller of the store and when the delete commits relative to the handler's own save; that is its own change.

## Rejected alternatives

- **`ElarionFile` for large files.** Base64 adds a third, buffers the whole file on both ends, hits the request-size
  limits, and a wizard resends it every step. `ElarionFile` stays the small-file tier.
- **A multipart JSON-RPC transport** (files as parts next to the envelope). It adds a second request format to a
  transport that only accepts JSON (the `415` rule), has no MCP or batch counterpart, and still resends the file on
  every step.
- **Binding uploads in a pre-handler decorator** (resolve and check the `UploadedFile`, hand the handler an open
  stream). It opens content before the handler decided it needs it, cannot know the per-operation constraints without
  new metadata, and splits the check from the commit, which must run inside the handler's transaction. Validation
  rules already say a pre-handler asynchronous check is a time-of-check/time-of-use bug.
- **Per-member limit attributes** (`[MaxUploadBytes]`, `[UploadContentTypes]`). They need generator and schema
  support for one feature; ADR-0100 proposes exporting typed limits for every member. Constraints at read time are
  enough until then and cost no new attribute.
- **An object reference** (`{ id, fileName, contentType }`). The client would assert metadata the server must not
  trust and already has.
- **A raw string id with a checked helper** on `IBlobStore`. The check would be available but optional, the schema
  could not mark the member, and the client could not upload for the caller.
- **Sliding expiry on read.** It needs a new `IBlobLifecycle` operation in every store; the endpoints' `Ttl` already
  covers a wizard.

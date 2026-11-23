---
type: Architecture Decision Record
title: "ADR-0106: An upload names a policy that chooses its staging limits"
description: "A client names a registered upload policy when it stages a file; the policy's limits replace the endpoint's and its check runs before the body is read, so one kind of upload may be larger without raising the cap for every upload."
status: draft
---
# ADR-0106: An upload names a policy that chooses its staging limits

- Status: Proposed
- Date: 2026-11-23
- Related: [ADR-0035](0035-protocol-neutral-staged-upload-seam.md) (the staged-upload seam),
  [ADR-0105](0105-uploaded-files-are-handler-parameters.md) (uploaded files and their declared limits)

## Context

The direct upload endpoint (`MapElarionBlobUploads`) and the tus endpoint (`MapElarionResumableBlobUploads`) each have
one cap (`MaxContentLength`, `MaxSize`) and, for the direct endpoint, one content-type allow-list. A handler's
`[UploadLimits]` can only narrow what the endpoint staged; it is checked when the handler reads the file, after the
bytes are stored.

An application allows 20 MB for ordinary uploads and 100 MB for imported e-mail messages (`.eml`). Raising the
endpoint's cap to 100 MB lets every signed-in user stage 100 MB for any purpose, and the server's own body limit
(Kestrel: about 28.6 MB by default) would have to be raised for every route, the JSON-RPC endpoint included. So the
application kept two minimal-API upload routes of its own instead of `UploadedFile`, and with them its own owner,
container and lifetime handling. It also wanted to refuse an import before 100 MB arrive when the draft it is for does
not exist or is not the caller's.

## Decision

1. **An upload policy is a named set of staging limits plus an optional check**, registered with
   `services.AddElarionUploadPolicy(name, policy => …)` in `Elarion.Blobs` and shared by both endpoints:
   `MaxBytes`, `ContentTypes` and `Authorize`. Registering a name again configures the same policy again, so a host can
   adjust a module's policy.
2. **The client names it when it stages.** The direct endpoint reads `?policy=` and `?resourceId=` from the query; the
   tus endpoint reads the `policy` and `resourceId` keys of `Upload-Metadata` at creation. The generated TypeScript
   client's `uploadFile(file, { policy, resourceId })` sends them; its default uploader puts them in the query.
3. **A limit the policy sets replaces the endpoint's; one it leaves `null` is the endpoint's.** A policy may raise or
   lower a limit. The endpoint's own limits stay the default for uploads that name no policy.
4. **`Authorize` runs before the body is read** — after authentication, before `ReadFormAsync` or the first byte of a
   raw body on the direct endpoint, before the session is created on tus. It receives the policy name, the client's
   `resourceId` (untrusted), the caller, the request's services and the declared length. A refusal answers `404`, so the
   record it names is not confirmed to exist; an unknown policy name answers `400`, because it is a client bug.
5. **The endpoint sets the request's body-size limit to the cap that applies** (`IHttpMaxRequestBodySizeFeature`, as
   ASP.NET's `[RequestSizeLimit]` does): the cap for a raw body, the cap plus 1 MiB for a form, whose file part is
   limited to the cap while it is read instead of after the form is buffered. A tus `PATCH` may carry at most the bytes
   the upload still lacks, which becomes its limit. This applies with and without a policy.
6. **The staged blob is the same pending upload.** Nothing records which policy staged it; the handler that takes it
   still checks its `[UploadLimits]` when it reads or keeps it, and still re-checks the record inside its transaction.

## Consequences

- One kind of upload can be larger than the rest without raising the default cap or the server's body limit for every
  route, and it is refused before its bytes arrive when the record it is for is not the caller's.
- A handler that accepts the larger files declares the larger `[UploadLimits]`; a handler that does not refuses them
  when it reads them. A policy only widens what can be *staged*.
- A file staged under a policy can be passed to any handler that takes an `UploadedFile`. That handler's own limits
  decide, as for any upload; nothing ties a file to the policy it was staged under.
- **Behavior change.** The direct endpoint now enforces its cap through the server's body limit, which raises a lower
  server default (a `MaxContentLength` above Kestrel's 28.6 MB now works) and lowers a higher one for this route. A
  multipart upload over the cap is refused while it is read. A tus `PATCH` that carries more bytes than the upload lacks
  answers `413` instead of having its excess ignored.
- The generated client's automatic staging of `File` params uses no policy; a member that needs one is uploaded with
  `uploadFile(file, { policy, resourceId })` and called with the reference.

## Rejected alternatives

- **Recording the policy on the blob and declaring it on the member** (`[UploadLimits(Policy = …)]`), so a file staged
  under one policy is accepted only by members that name it. It needs a new column in every blob store and lifecycle
  provider, and the member's own limits already decide what a handler accepts. Revisit if a policy ever grants
  something a member's limits cannot express.
- **Deriving the staging cap from the target member** (`?method=…&member=…`, the pair the TypeScript client already
  knows), so `[UploadLimits]` alone raises the cap. It couples the blob endpoints to the handler catalog and to JSON-RPC
  method names (an HTTP-only handler has none), and the per-record check would still need a hook.
- **A separate endpoint per kind of upload** (`MapElarionBlobUploads("/mail")` with its own options). It multiplies
  routes, prefixes and containers the reader must list, and a client must know which route belongs to which member;
  a policy name is one query parameter on the same endpoint.
- **A check delegate on the endpoint options instead of named policies.** It would see every upload and could not
  raise the cap for one kind only.
- **Answering a refused check with `403`.** It would confirm that the named record exists; the upload endpoints answer
  every ownership failure as not found.

---
type: Architecture Decision Record
title: "ADR-0099: Blob stores record the content's SHA-256 while they save it"
description: "Blob stores compute the SHA-256 of the content while SaveAsync streams it and expose it as BlobMetadata.ContentSha256; a fallback hashes blobs without one."
status: draft
---
# ADR-0099: Blob stores record the content's SHA-256 while they save it

- Status: Proposed
- Date: 2026-11-21
- Related: [ADR-0035](0035-protocol-neutral-staged-upload-seam.md) (staged uploads),
  [ADR-0041](0041-blob-streaming-connections-clone-the-context-connection.md) (streaming reads),
  [ADR-0098](0098-streamed-files-for-http-only-endpoints.md) (entity tags of streamed files)

## Context

Applications that keep uploaded documents want to know when the same file arrives twice, and want a stable entity tag
for a download. Both need a hash of the content. The blob stores computed none, so an application read every blob a
second time after saving it, streaming megabytes back out of the database only to hash them — the store had just
streamed the same bytes in.

## Decision

- **`BlobMetadata.ContentSha256`**: the SHA-256 of the content as 64 lower-case hex characters, or `null` when the store
  recorded none. Hex text, not bytes, because applications copy it into their own entities and compare it in queries;
  lower case, because that is what `Convert.ToHexStringLower` and most tooling write.
- **Computed while `SaveAsync` streams**, never by a second read. The PostgreSQL store hashes inside the stream that
  already caps and counts the bytes for the `bytea` bind, and writes the hash to the new nullable
  `stored_blobs.content_sha256` column in the same transaction as the content. The Azure store reads the content in
  4 MiB blocks, hashes each block, and writes the hash into the blob metadata (`elarion_content_sha256`) of the request
  that makes the blob visible: one Put Blob for content up to one block, otherwise staged blocks and a block-list
  commit.
- **`null` is a documented state**: blobs stored before the column existed, blobs another producer wrote to an Azure
  container, and Azure staged uploads, which complete by a server-side copy that never passes the bytes through the
  application. `BlobStoreExtensions.GetContentSha256Async` answers for every blob, reading the content only when no hash
  is recorded.
- **No lookup by hash on the store.** Listing is a browse surface; an application that finds duplicates copies the hash
  into its own entity and indexes it there. `content_sha256` gets no index.

## Consequences

- Duplicate detection and entity tags cost no extra read for new blobs.
- **Schema change**: EF applications add a migration for `content_sha256` after upgrading; without it every query of
  `stored_blobs` fails on the missing column.
- The PostgreSQL write takes one more statement (the hash update) inside the save's transaction.
- Azure saves no longer use the SDK's parallel block upload: blocks are staged one after another with one 4 MiB
  buffer. Memory is bounded per save; large uploads are slower than with eight parallel blocks, which matters only
  beyond the scale tier the framework targets.

## Rejected alternatives

- **The provider's own checksum** (Azure `Content-MD5`, CRC64). MD5 is no longer fit for duplicate detection across
  users, CRC64 is not a content identity, and neither exists on PostgreSQL; one algorithm on every store keeps hashes
  comparable across a migration between stores.
- **SHA-256 computed by PostgreSQL** (`sha256(data)` after the insert). The database would load the whole value again
  to hash it, which is the second read this decision removes, only moved.
- **Azure: upload through the SDK, then `SetMetadata` with the hash.** Two requests, and a window in which the blob is
  visible without its hash; the SDK would also buffer a forward-only hashing stream up to its single-upload threshold.
- **Hashing Azure staged uploads incrementally across appends.** The BCL's incremental hash state cannot be persisted
  between requests or nodes, and a session may resume on another node.
- **An opt-in flag.** The hash costs one pass over bytes already in memory; a default an application must remember to
  turn on would be missing exactly when the duplicate check is added later.

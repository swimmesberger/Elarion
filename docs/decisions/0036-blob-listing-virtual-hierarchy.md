# ADR-0036: Blob listing is prefix + delimiter virtual hierarchy, not directories

- Status: Accepted
- Date: 2026-07-04
- Related: [ADR-0035](0035-protocol-neutral-staged-upload-seam.md) (the staging seam this rounds out),
  [ADR-0007](0007-data-is-platform-module-as-plugin.md) (the database is the source of truth for structure)

## Context

`IBlobStore` had no enumeration at all: save/read/metadata/delete/exists, with the garbage collectors
walking their stores privately per provider. That left three legitimate consumers with no portable
surface — admin/browse UIs, migration and backup tooling (a generic `IBlobStore`-to-`IBlobStore` copier
was impossible), and ops. The question was what shape listing should take: real directory semantics, or
the flat model the storage industry uses.

Two facts constrain the design:

- **Every major blob API converged on the same model** — S3 `ListObjectsV2`, Azure
  `GetBlobsByHierarchy`, GCS: a flat namespace where "directories" are emulated by a prefix + delimiter
  query that rolls names up into *common prefixes*. Real directory semantics (empty folders, atomic
  subtree rename, per-folder metadata) exist only on ADLS Gen2 and filesystems; putting them in the
  floor would fragment every other backend.
- **In Elarion, the database is the source of truth for structure** (ADR-0007). A handler answering
  "which files belong to this contract" queries the entity that holds the `BlobRef`s; it never
  enumerates the store. An app that wants user-facing folders models them as entities and keeps blobs
  flat.

## Decision

Add listing to **`IBlobStore` itself** — unlike the two-state lifecycle (whose `IBlobLifecycle` split
exists because not every backend can model it), every conceivable blob backend can list, so listing is a
floor primitive, not an optional capability:

- `ListAsync(BlobListRequest)` → `BlobListing`: flat prefix listing, optionally rolled up into
  delimiter-inclusive virtual-directory `Prefixes` (the S3/Azure shape). Entries are returned in
  lexicographic (ordinal) name order and paged by an **opaque, store-specific continuation token**;
  a missing container yields an empty page. `ListContainersAsync` completes the browse surface.
- `BlobMetadata` gains **`State`**, and `BlobListRequest` a state filter, so browse surfaces can
  distinguish or hide half-finished pending uploads. On Azure the filter is client-side per page
  (metadata is not server-filterable) — a documented weaker tier: filtered pages may under-fill while
  the token indicates more.
- `BlobStoreExtensions.ListAllAsync` layers the auto-paging `IAsyncEnumerable` enumeration on top —
  the primitive stays paged (tokens serialize to UI clients), the ergonomic form is an extension,
  matching the package's existing pattern.
- Implementations: Azure maps 1:1 onto `GetBlobsByHierarchy` + service continuation tokens; PostgreSQL
  computes the entry roll-up in one grouped query with `COLLATE "C"` ordering and a keyset token
  (`(entry, isPrefix)`), riding the existing unique `(container, name)` index — its pages are
  consistent snapshots, a stronger guarantee than the cloud providers give.

Explicit rejections:

- **Real directories** — no folder objects, no empty folders, no subtree rename. Apps model folders as
  entities (ADR-0007).
- **Owner-scoped listing by name prefix** — upload transports name blobs `{userId}/…`, which makes a
  prefix look like a per-user filter, but names are client-influenced and ownership is the exact-match
  `OwnerId` (the same forgeability stance the upload endpoints take). If per-owner listing is ever
  needed it is an `OwnerId` filter on the request (trivial on PostgreSQL, page-and-filter on Azure),
  not a prefix convention.
- **Listing as an application query path** — documented as a browse/ops surface; handlers keep
  querying their own tables.

## Consequences

- A generic store-to-store migration/backup tool is now writable against the seam
  (`ListContainersAsync` × `ListAllAsync` × `OpenReadAsync`/`SaveAsync`).
- Breaking (pre-1.0): `IBlobStore` implementations and test doubles must add the two members;
  `BlobMetadata.State` is required, so construction sites name it.
- Continuation tokens are deliberately store-specific and not portable across backends; callers treat
  them as opaque and loop until `null` (page fill is not a termination signal).

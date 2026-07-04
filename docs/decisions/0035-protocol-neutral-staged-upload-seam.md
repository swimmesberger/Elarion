# ADR-0035: Protocol-neutral staged-upload seam (`IStagedUploadStore` supersedes `ITusUploadStore`)

- Status: Accepted
- Date: 2026-07-04
- Related: [ADR-0015](0015-ef-core-transaction-participation.md) (the PostgreSQL completion transaction),
  [ADR-0017](0017-dependency-light-core.md) (seam in the neutral package, providers opt-in),
  [ADR-0025](0025-distributed-scheduler-coordination.md) (scale positioning: swap the seam, don't grow a default)

## Context

`Elarion.Blobs.Tus` shipped resumable uploads behind a tus-named seam, `ITusUploadStore`, with an in-memory
default and a durable PostgreSQL implementation in its own package pair (`Elarion.Blobs.Tus.PostgreSql` +
generators). Reviewing the contract showed it was already ~95 % protocol-neutral — the endpoint owned all
naming policy, the metadata field was an opaque string the stores never interpreted — but three tus-isms were
baked in:

- **Auto-finalization**: `AppendAsync` sealed the upload the moment the declared length was reached. That is a
  tus-1.0-ism — the IETF successor protocol (RUFH, draft-ietf-httpbis-resumable-upload, "tus 2.0") signals
  completion with an explicit `Upload-Complete` flag and supports uploads whose length is unknown until the
  end (as does tus 1.0's `creation-defer-length` extension, which we did not support). It also forced the
  endpoint to fake an empty append (`AppendAsync(id, 0, Stream.Null)`) to finalize zero-length uploads.
- **Policy inside the store**: both stores computed session expiry and pending-blob TTL from `TusOptions`,
  coupling every storage backend to the tus package's option bag.
- **A tus-named seam** meant any future resumable protocol — RUFH above all — would either read tus types or
  need a second, near-identical seam and a second staging backend per provider.

Separately, the question "could an Azure Blob Storage backend implement every tus operation on the bare SDK?"
has a positive answer (append blobs carry a server-side atomic `If-Append-Position-Equal` guard; completion is
a server-side copy), which makes a good validation exercise for whatever seam we settle on: a correct seam
should let a provider stage natively with zero knowledge of any upload protocol.

## Decision

Promote the staging contract into `Elarion.Blobs` as the protocol-neutral **`IStagedUploadStore`**
(`StagedUpload`, `StagedUploadCreation`, `StagedUploadCompletion`, `StagedUploadConflictException`), deleting
`ITusUploadStore` outright (pre-1.0, no compatibility shim). The seam differs from its predecessor in exactly
the ways protocol neutrality demands:

- **Explicit, idempotent `CompleteAsync`** replaces finalize-on-length. `StagedUploadCreation.Length` is
  nullable (deferred length; completion seals it at the received byte count). This is the RUFH
  `Upload-Complete` shape; the tus 1.0 adapter simply completes when the offset reaches the declared length,
  and heals the append-crash window by completing idempotently on the next status probe.
- **Policy moves to the caller as data.** Session expiry rides `StagedUploadCreation.ExpiresAt`; the pending
  blob's TTL and the completed session's retention deadline ride `StagedUploadCompletion`. Stores carry no
  options bag.
- **The contract promises resolvability, not mechanism**: the produced `BlobRef` must resolve through the
  registered `IBlobStore` and enter the pending/committed lifecycle. *How* the bytes get there — generic
  `IBlobStore.SaveAsync`, or a backend-native server-side copy — is the implementation's choice, so a provider
  ships its blob store and staging store as a matched pair.

Package layout consequences:

- `Elarion.Blobs` gains the seam, the in-memory default, and the provider-neutral collectors
  (`StagedUploadGarbageCollector`; `BlobGarbageCollector` moves up from `Elarion.Blobs.PostgreSql` so non-EF
  providers reuse it). Deliberate cost: the previously dependency-free package now references the
  `Microsoft.Extensions.*` *Abstractions* trio (DI/Hosting/Logging) — within the framework's
  dependency-light rule.
- `Elarion.Blobs.Tus` shrinks to a **pure protocol adapter** (endpoint mapping, header parsing, `TusOptions`
  policy). `Elarion.Blobs.Tus.PostgreSql` and its generator project **dissolve into `Elarion.Blobs.PostgreSql`**
  (`PostgreSqlStagedUploadStore`, `UseElarionStagedUploads` / `[GenerateElarionStagedUploads]`, `ELBLB002`) —
  two projects deleted, one Postgres blob package owning blobs + staging.
- **`Elarion.Blobs.Azure`** is added as the seam-validation exercise: `AzureBlobStore`
  (`IBlobStore` + `IBlobLifecycle` via blob metadata, ETag-guarded commit/GC races) and
  `AzureStagedUploadStore` (one append blob per session, per-block `If-Append-Position-Equal` offset guard,
  completion as a server-side copy that stamps the pending metadata, then recreates the staging blob empty as
  the queryable completion marker). It contains zero protocol knowledge, which is the point.

When RUFH stabilizes it becomes a new endpoint file over the same seam; every staging backend lights up
unchanged.

## Consequences

- Breaking (pre-1.0): `ITusUploadStore`/`TusUpload`/`TusUploadCreation`/`TusOffsetConflictException` are gone;
  `AddElarionTusPostgreSql` → `AddElarionPostgreSqlStagedUploads`; `UseElarionTusStorage` /
  `[GenerateElarionTusStorage]` / `ELTUS001` → `UseElarionStagedUploads` / `[GenerateElarionStagedUploads]` /
  `ELBLB002`; the staging table default is `staged_uploads` (was `tus_uploads`) with a nullable `length`.
  Externally the tus endpoints behave identically.
- Completion-versus-append races are now guarded (the PostgreSQL completion stamp is conditioned on the loaded
  offset; Azure appends are position-conditioned per block), and completed-retention becomes uniform across
  stores instead of a PostgreSQL-only knob.
- The Azure lifecycle semantics are the documented weaker tier: `CommitAsync` is immediate (no ambient
  transaction to join) and expired-pending collection scans metadata listings — fine for the 1–10-node tier;
  at larger scale swap the collector for Azure lifecycle-management policies (the ADR-0025 pattern).
- The in-memory staging default now gets garbage collection (previously nothing swept expired in-memory
  sessions).

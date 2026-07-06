# ADR-0041: Blob streaming connections clone the context connection

- Status: Accepted
- Date: 2026-07-06
- Related: [ADR-0035](0035-protocol-neutral-staged-upload-seam.md) (the staging store that composes the blob
  store), [ADR-0039](0039-binary-file-responses.md) (the staged-blob file tier whose export leg streams
  through this path), the [blob storage](../capabilities/blob-storage.mdx) capability doc.

## Context

`PostgreSqlBlobStore<TDbContext>` streams reads: `OpenReadAsync` hands the caller a `BlobDownload` that owns
a dedicated connection for as long as the caller reads (the scoped `DbContext` connection cannot host a
reader that outlives the call). That dedicated connection used to come from an injected `NpgsqlDataSource` —
a **second, independently-configured channel** to a database that is by definition the same one the blob
`DbContext` is already configured for.

Every observed friction was a way for the two channels to disagree or for one to be missing:

- A host that configured EF with `UseNpgsql(connectionString)` and never registered an `NpgsqlDataSource`
  failed DI validation with the generic activator error (`Unable to resolve service for type
  'Npgsql.NpgsqlDataSource' while attempting to activate 'PostgreSqlBlobStore\`1'`) — nothing in the message
  says how to fix it. A real consuming-app upgrade hit exactly this, at the schema-gen build step.
- The `connectionString` overloads (added to smooth that upgrade) created a **second pool** via
  `NpgsqlDataSource.Create`. Any `NpgsqlDataSourceBuilder` configuration on the host's source (enum
  mappings, auth callbacks) silently did not apply to the store's pool.
- "Must target the same database as the blob context" was a prose invariant enforced by nothing; pointing
  the two channels at different databases would surface as blobs with metadata but unreadable content.
- The `TryAddSingleton` registration made "a host-registered data source wins" order-sensitive.

## Decision

The store derives its dedicated streaming connection **from the scoped context's own connection** and takes
no connection configuration of its own. `OpenReadAsync` clones the context's `NpgsqlConnection` via
`ICloneable.Clone()`, which Npgsql implements as:

```csharp
var conn = _dataSource is null
    ? _cloningInstantiator!(_connectionString)
    : _dataSource.CreateConnection();   // same data source as the original
```

So the clone comes from the connection's **owning `NpgsqlDataSource`** — same pool, same type mapping, same
auth callbacks (`CloneWith` documents the contract: "the same security information (password, SSL
callbacks)"), and the same database **by construction**. This covers every host shape uniformly:
`UseNpgsql(dataSource)` and `AddNpgsqlDataSource` + `UseNpgsql()` clone from the host's source;
`UseNpgsql(connectionString)` clones from the pool Npgsql builds internally for that string. The context
connection is opened before cloning (the write path already relies on the same open-if-closed helper), so
the owning data source is resolved.

Consequently:

- The `NpgsqlDataSource` constructor parameter is gone; nothing blob-related resolves or registers an
  `NpgsqlDataSource` in the container.
- The `connectionString` overloads of `AddElarionPostgreSqlBlobStore`, `AddElarionPostgreSqlBlobLifecycle`,
  and `AddElarionPostgreSqlStagedUploads` are **deleted** (pre-1.0 clean break). Registration is one
  parameterless call per store; the blob `DbContext` registration is the only connection wiring.
- The failure-mode class does not get a better diagnostic — it stops existing. There is no second statement
  of the connection to omit, misconfigure, order wrongly, or point elsewhere.

Streaming reads now draw from the same pool as the context: a long-held download connection counts against
that pool's `Maximum Pool Size`. That is the intended architecture (one shared pool per database), well
inside the framework's 1–10-node design tier; a host that ever needs an isolated streaming pool is a future
optional options bag, not a reason to keep the duplicate channel.

## Alternatives considered

- **Keep the dependency, document the shared-pool recipe.** A "one shared pool" snippet
  (`AddSingleton(NpgsqlDataSource.Create(cs))` + `UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>())`)
  next to the connection-string happy path. This was the right fix *while the dependency existed* — the
  pattern worked but was undocumented — and is subsumed: with no data source to register, there is no
  recipe to teach.
- **`AddElarionNpgsqlDataSource(connectionString)` alias.** An Elarion-namespaced wrapper over
  `AddNpgsqlDataSource`/`NpgsqlDataSource.Create` to make the shared-pool entry point discoverable.
  Rejected: a synonym with no semantics of its own, duplicating Npgsql's public API surface inside
  Elarion — and moot once nothing needs the registration.
- **An opinionated `AddElarionNpgsql<TDbContext>(cs, configure)`** registering the data source, the
  `DbContext`, and the stores in one call. Rejected: it couples Elarion to EF `AddDbContext` registration
  and has to thread provider options through a callback, breaking the deliberate host-owns-EF boundary.
- **A targeted startup error** ("register an `NpgsqlDataSource` — see …") instead of the generic activator
  failure. Rejected: MEDI cannot customize the activation message, a throwing placeholder registration can
  shadow a legitimate later registration, and a lazy resolver trades the build-time `ValidateOnBuild` catch
  for a friendlier-but-later runtime failure. Moot under the decision.
- **A same-database runtime guard** comparing the data source's host/database against the context
  connection's. Rejected: false positives on legitimate split topologies (e.g. EF via pgBouncer, streaming
  direct), and the invariant now holds by construction.

## Consequences

- A blob host wires exactly one thing: the `DbContext`. `AddElarionPostgreSqlBlobStore<AppDbContext>()`
  works identically under every EF connection shape, including AAD-style auth callbacks configured on a
  host data source.
- Breaking change for hosts that called a `connectionString` overload: drop the argument. Hosts that
  registered an `NpgsqlDataSource` purely for the blob store can delete the registration (one kept for EF
  binding is unaffected).
- The integration suite runs the store against real PostgreSQL under both `UseNpgsql(connectionString)`
  and `UseNpgsql(dataSource)` contexts, including the pool-release test (bounded pool, partial read, then
  dispose) that proves the cloned connection returns to the shared pool.

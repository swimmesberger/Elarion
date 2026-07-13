# ADR-0056: PostgreSQL extensions are within the one-Postgres positioning; ease the extension-image pain

- Status: Proposed (posture decided; the image/composition improvement is deferred until designed)
- Date: 2026-07-13
- Related: [ADR-0025](0025-distributed-scheduler-coordination.md) (the scale positioning this refines),
  [ADR-0020](0020-postgres-unlogged-l2-cache.md) (precedent: lean on what the one Postgres can do).

## Context

Elarion's positioning is "the one PostgreSQL the app already runs." The open question was whether
specialized workloads — time-series telemetry (both field gateways store device samples), vector search,
etc. — count as "outgrowing" that Postgres. Decision needed, because the alternative reading pushes
apps toward a second infrastructure piece prematurely.

Separately, the *practical* friction is real and recurring: recommending an extension (TimescaleDB,
pgvector, PostGIS, …) today means every project hand-assembles a custom Postgres container image for
1–4 extensions — its own Dockerfile, its own image builds for dev, CI (Testcontainers), and production —
because no official image carries an arbitrary combination.

## Decision

**Posture (decided): Postgres extensions are composition, not scale-out.** An extension changes what
the one Postgres can do, not how many infrastructure pieces the app runs — it fits the composition bill
exactly like `UNLOGGED` tables or `LISTEN/NOTIFY` did. Concretely: the time-series recommendation is a
**TimescaleDB recipe** (hypertables + `time_bucket` + retention policies over the app's existing EF
model), not an Elarion time-series subsystem; likewise vector search is a pgvector recipe. Elarion docs
may recommend extensions freely; Elarion packages must keep working without them (an extension is a
recipe's prerequisite, never a package's silent dependency).

**Improvement direction (deferred, to be designed):** reduce the custom-image friction. Candidate
shapes, in rough order of preference:

1. A documented **canonical extension-image recipe**: one parameterized Dockerfile pattern (base
   `postgres:N` + extension install layers) shared across dev/CI/prod, plus the Aspire wiring
   (`AddPostgres(...).WithImage(...)`).
2. A small **Testcontainers helper** that builds the image from the requested extension list
   (`ImageFromDockerfileBuilder` under the hood) so integration tests declare extensions instead of
   maintaining Dockerfiles.
3. An Elarion-published multi-extension base image — noted for completeness, but it carries an ongoing
   publish/patch burden and is the least-preferred option.

## Consequences

- Recipes may assume extensions; packages may not. A future `docs/capabilities` time-series page is a
  TimescaleDB recipe composing bulk insert, keyset paging, scheduled retention, and client events.
- The image-composition improvement waits for a consuming project's concrete need (likely the first
  telemetry-heavy port) to pick between shapes 1 and 2.

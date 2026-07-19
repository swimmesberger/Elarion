# Architecture Decision Records

This directory holds Architecture Decision Records (ADRs) for Elarion, in the
classic [Nygard format](https://cognitect.com/blog/2011/11/15/documenting-architecture-decisions).

An ADR captures a single architecturally significant decision: the context that
forced it, the decision itself, and the consequences we accept. ADRs are
append-only history — once an ADR is `Accepted`, prefer writing a new ADR that
supersedes it over editing the original.

These records are intentionally **not** published on the documentation site. The
site documents shipped behavior; ADRs document *why* we chose a design, including
designs that are not yet implemented.

## Conventions

- File name: `NNNN-kebab-case-title.md` (zero-padded sequence number).
- Status: `Proposed` | `Accepted` | `Superseded by ADR-NNNN` | `Deprecated`.
- Sections: Status, Context, Decision, Consequences. Add others (Options,
  Recommended pattern, References) when they add value.

## Index

- [ADR-0001: Event dispatch timing and transactional delivery](0001-event-transaction-phase.md)
- [ADR-0002: Direct cross-module communication](0002-cross-module-communication.md)
- [ADR-0003: Predicate-based decorator attachment (`AppliesTo`)](0003-decorator-attachment-predicates.md)
- [ADR-0004: Handler result caching](0004-handler-result-caching.md)
- [ADR-0005: Cross-module error channel (`Result` vs exceptions) and gRPC mapping](0005-cross-module-error-channel.md)
- [ADR-0006: Incremental source generator conventions](0006-incremental-source-generator-conventions.md)
- [ADR-0007: The data layer is application logic — modules as plugins over it](0007-data-is-platform-module-as-plugin.md)
- [ADR-0008: Bounded contexts and the graduation path](0008-bounded-contexts-and-the-graduation-path.md)
- [ADR-0009: Authorization building blocks](0009-authorization-building-blocks.md)
- [ADR-0010: The event bus is pub/sub-only; request/reply is unified under dispatch](0010-event-bus-is-pub-sub-only.md)
- [ADR-0011: Runtime-changeable settings subsystem](0011-runtime-settings-subsystem.md)
- [ADR-0012: Referencing dynamic variables in attributes — form follows tier](0012-dynamic-variable-references.md)
- [ADR-0013: Resource-based and data-level authorization](0013-resource-and-data-level-authorization.md)
- [ADR-0014: Cross-assembly generator composition](0014-cross-assembly-generator-composition.md)
- [ADR-0015: EF Core stores enlist in the caller's ambient transaction](0015-ef-core-transaction-participation.md)
- [ADR-0016: Feature-flag gating (`[FeatureGate]` over an OpenFeature-backed seam)](0016-feature-flag-gating.md)
- [ADR-0017: Elarion core is dependency-light; provider defaults are opt-in packages](0017-dependency-light-core.md)
- [ADR-0018: Generated infrastructure is framework-named, not user-declared](0018-generated-infrastructure-is-framework-named.md)
- [ADR-0019: Variant service injection (transparent, via an opt-in async-resolving handler proxy)](0019-variant-service-injection.md)
- [ADR-0020: PostgreSQL `UNLOGGED` table is the recommended L2 distributed cache](0020-postgres-unlogged-l2-cache.md)
- [ADR-0021: Idempotency (`[Idempotent]` over a single-transaction, unique-constrained key store)](0021-idempotency.md)
- [ADR-0022: Inbox pattern for integration-event consumers (idempotent consumers)](0022-inbox-idempotent-event-consumers.md)
- [ADR-0023: Canonical JSON serialization configuration](0023-canonical-json-serialization.md)
- [ADR-0024: Cross-instance settings change notification over PostgreSQL LISTEN/NOTIFY](0024-postgres-listen-notify-settings-changes.md)
- [ADR-0025: Cross-instance scheduler coordination (per-occurrence claims over EF Core/PostgreSQL)](0025-distributed-scheduler-coordination.md)
- [ADR-0026: OpenAPI for the HTTP transport](0026-openapi-http-transport.md)
- [ADR-0027: Declarative request validation (DataAnnotations exported to every contract surface; business rules in the handler)](0027-declarative-request-validation.md)
- [ADR-0028: Configuration-selected service variants (`[ConfigurationVariant]` — synchronous, proxy-free sibling of `[FeatureVariant]`)](0028-configuration-selected-service-variants.md)
- [ADR-0029: The variant registry (`ElarionVariants`), named defaults, and the host-seeded catalog](0029-variant-registry-and-catalog.md)
- [ADR-0030: Client capability bootstrap — modules, flags/variants, and grants projected to the frontend over OpenFeature](0030-client-capability-bootstrap.md)
- [ADR-0031: Imperative handler transport mapping (and why HTTP stays concrete)](0031-imperative-handler-transport-mapping.md)
- [ADR-0032: Frontend contribution model and the typed capability vocabulary](0032-frontend-contribution-model.md)
- [ADR-0033: User-context trace and log enrichment](0033-user-context-trace-and-log-enrichment.md)
- [ADR-0034: Abstractions holds contracts, not implementations](0034-abstractions-holds-contracts-not-implementations.md)
- [ADR-0035: Protocol-neutral staged-upload seam (`IStagedUploadStore` supersedes `ITusUploadStore`)](0035-protocol-neutral-staged-upload-seam.md)
- [ADR-0036: Blob listing is prefix + delimiter virtual hierarchy, not directories](0036-blob-listing-virtual-hierarchy.md)
- [ADR-0037: Read-replica query routing and read-your-writes consistency tokens — Proposed](0037-read-replica-routing-and-consistency-tokens.md)
- [ADR-0038: Client-assigned entity identity — UUIDv7 and a truthful key model](0038-client-assigned-entity-identity.md)
- [ADR-0039: File payloads — the in-memory `ElarionFile` tier and the staged-blob tier](0039-binary-file-responses.md)
- [ADR-0040: Host-declared module endpoint hooks (`[ModuleEndpoints]`)](0040-host-declared-module-endpoints.md)
- [ADR-0041: Blob streaming connections clone the context connection](0041-blob-streaming-connections-clone-the-context-connection.md)
- [ADR-0042: In-memory actors — mailbox-protected state machines with generated typed facades](0042-in-memory-actors.md)
- [ADR-0043: Client events — after-commit facts projected to the browser over SSE](0043-client-events.md)
- [ADR-0044: Streaming requests and responses — deferred, with the design pre-decided](0044-streaming-requests-and-responses.md)
- [ADR-0045: Handler-action audit trail — `[Auditable]`, `IAuditScope`, and the transactional EF sink](0045-handler-action-audit-trail.md)
- [ADR-0046: Actor event consumers — `[ConsumeEvent]` on `[Actor]` methods → generated relay](0046-actor-event-consumers.md)
- [ADR-0047: Actor state snapshotting — `IActorState<TState>` + `IActorSnapshotStore`, PostgreSQL default](0047-actor-state-snapshotting.md)
- [ADR-0048: Single-homed actors — a PostgreSQL home lease, not a cluster](0048-single-homed-actors.md)
- [ADR-0049: Role leases — the leader-election primitive, extracted from the actor home](0049-role-leases.md)
- [ADR-0050: The role-holder proxy — an in-app ingress rule, not a cluster](0050-role-holder-proxy.md)
- [ADR-0051: Bulk insert as an EF-shaped seam with a PostgreSQL binary COPY provider](0051-postgresql-bulk-insert.md)
- [ADR-0052: Ordered streams — a sequencer-owned hub, actor stream methods, and a resumable SSE leg](0052-ordered-streams.md)
- [ADR-0053: Bidirectional client connections — a transport-neutral connection seam; adapters adopted whole](0053-bidirectional-client-connections.md)
- [ADR-0054: Device identity and provisioning — pairing codes, device keys, and the HMAC connect-time handshake](0054-device-identity-and-provisioning.md)
- [ADR-0055: Data-rate shaping helpers — write-behind buffer and keyed conflater](0055-data-rate-shaping-helpers.md)
- [ADR-0056: PostgreSQL extensions are within the one-Postgres positioning; ease the extension-image pain](0056-postgres-extensions-posture.md)
- [ADR-0057: A Flyway-shaped PostgreSQL migration runner for EF-free (AOT) hosts](0057-postgresql-sql-migration-runner.md)
- [ADR-0058: AOT-native SQL row mapping — explicit generated mappers, not call-site interception — Proposed](0058-aot-sql-row-mapping.md)
- [ADR-0059: Merge the always-on tracing + context-enrichment decorators into one observability decorator](0059-merged-handler-observability-decorator.md)
- [ADR-0060: A database-neutral migration core, and a SQLite provider](0060-database-neutral-migration-core.md)
- [ADR-0061: Virtual-sharded actors — fixed role partitions without a cluster](0061-virtual-sharded-actors.md)
- [ADR-0062: Role-affine routing and target-group outbox delivery](0062-role-affine-routing-and-outbox-delivery.md)
- [ADR-0063: gRPC ships as typed unary and server-streaming transport adapters](0063-grpc-unary-transport.md)
- [ADR-0064: Acknowledgment-gated outbound delivery is a codec-tier helper, adopted on second demand — Proposed](0064-acknowledgment-gated-outbound-delivery.md)
- [ADR-0065: Self-typed request markers enable inferred dispatch; ConnectionHandlerInvoker binds per connection](0065-self-typed-request-markers-and-bound-connection-invoker.md)
- [ADR-0066: Opt-in low-allocation dispatch profile for high-rate connections](0066-low-allocation-connection-dispatch.md)

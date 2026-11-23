# Architecture decision records

* [ADR format](README.md) - when to write an architecture decision record, its format and its statuses.
* [ADR-0001](0001-event-transaction-phase.md) - Event dispatch timing and transactional delivery
* [ADR-0002](0002-cross-module-communication.md) - Direct cross-module communication
* [ADR-0003](0003-decorator-attachment-predicates.md) - Predicate-based decorator attachment (`AppliesTo`)
* [ADR-0004](0004-handler-result-caching.md) - Handler result caching (`[Cacheable]` / `[CacheInvalidate]`)
* [ADR-0005](0005-cross-module-error-channel.md) - Error channel on cross-module contracts (`Result` vs exceptions) and gRPC mapping
* [ADR-0006](0006-incremental-source-generator-conventions.md) - Incremental source generator conventions
* [ADR-0007](0007-data-is-platform-module-as-plugin.md) - The data layer is application logic — modules as plugins over it
* [ADR-0008](0008-bounded-contexts-and-the-graduation-path.md) - Bounded contexts and the graduation path (proposed)
* [ADR-0009](0009-authorization-building-blocks.md) - Authorization building blocks
* [ADR-0010](0010-event-bus-is-pub-sub-only.md) - The event bus is pub/sub-only; request/reply is unified under dispatch
* [ADR-0011](0011-runtime-settings-subsystem.md) - Runtime-changeable settings subsystem
* [ADR-0012](0012-dynamic-variable-references.md) - Referencing dynamic variables in attributes — form follows tier
* [ADR-0013](0013-resource-and-data-level-authorization.md) - Resource-based and data-level authorization
* [ADR-0014](0014-cross-assembly-generator-composition.md) - Cross-assembly generator composition
* [ADR-0015](0015-ef-core-transaction-participation.md) - EF Core stores enlist in the caller's ambient transaction
* [ADR-0016](0016-feature-flag-gating.md) - Feature-flag gating (`[FeatureGate]` over an OpenFeature-backed seam)
* [ADR-0017](0017-dependency-light-core.md) - Elarion core is dependency-light; provider defaults are opt-in packages
* [ADR-0018](0018-generated-infrastructure-is-framework-named.md) - Generated infrastructure is framework-named, not user-declared
* [ADR-0019](0019-variant-service-injection.md) - Variant service injection (transparent, via an opt-in async-resolving handler proxy)
* [ADR-0020](0020-postgres-unlogged-l2-cache.md) - PostgreSQL `UNLOGGED` table is the recommended L2 distributed cache
* [ADR-0021](0021-idempotency.md) - Idempotency (`[Idempotent]` over a single-transaction, unique-constrained key store)
* [ADR-0022](0022-inbox-idempotent-event-consumers.md) - Inbox pattern for integration-event consumers (idempotent consumers)
* [ADR-0023](0023-canonical-json-serialization.md) - Canonical JSON serialization configuration
* [ADR-0024](0024-postgres-listen-notify-settings-changes.md) - Cross-instance settings change notification over PostgreSQL LISTEN/NOTIFY
* [ADR-0025](0025-distributed-scheduler-coordination.md) - Cross-instance scheduler coordination (per-occurrence claims over EF Core/PostgreSQL)
* [ADR-0026](0026-openapi-http-transport.md) - OpenAPI for the HTTP transport
* [ADR-0027](0027-declarative-request-validation.md) - Declarative request validation
* [ADR-0028](0028-configuration-selected-service-variants.md) - Configuration-selected service variants (`[ConfigurationVariant]`)
* [ADR-0029](0029-variant-registry-and-catalog.md) - The variant registry (`ElarionVariants`), named defaults, and the host-seeded catalog
* [ADR-0030](0030-client-capability-bootstrap.md) - Client capability bootstrap — modules, flags/variants, and grants projected to the frontend over OpenFeature
* [ADR-0031](0031-imperative-handler-transport-mapping.md) - Imperative handler transport mapping (and why HTTP stays concrete)
* [ADR-0032](0032-frontend-contribution-model.md) - Frontend contribution model and the typed capability vocabulary
* [ADR-0033](0033-user-context-trace-and-log-enrichment.md) - User-context trace and log enrichment
* [ADR-0034](0034-abstractions-holds-contracts-not-implementations.md) - Abstractions holds contracts, not implementations
* [ADR-0035](0035-protocol-neutral-staged-upload-seam.md) - Protocol-neutral staged-upload seam (`IStagedUploadStore` supersedes `ITusUploadStore`)
* [ADR-0036](0036-blob-listing-virtual-hierarchy.md) - Blob listing is prefix + delimiter virtual hierarchy, not directories
* [ADR-0037](0037-read-replica-routing-and-consistency-tokens.md) - Read-replica query routing and read-your-writes consistency tokens (proposed)
* [ADR-0038](0038-client-assigned-entity-identity.md) - Client-assigned entity identity — UUIDv7 and a truthful key model
* [ADR-0039](0039-binary-file-responses.md) - File payloads — the in-memory `ElarionFile` tier and the staged-blob tier
* [ADR-0040](0040-host-declared-module-endpoints.md) - Host-declared module endpoint hooks (`[ModuleEndpoints]`)
* [ADR-0041](0041-blob-streaming-connections-clone-the-context-connection.md) - Blob streaming connections clone the context connection
* [ADR-0042](0042-in-memory-actors.md) - In-memory actors — mailbox-protected state machines with generated typed facades
* [ADR-0043](0043-client-events.md) - Client events — after-commit facts projected to the browser over SSE
* [ADR-0044](0044-streaming-requests-and-responses.md) - Request-driven server-streaming handlers
* [ADR-0045](0045-handler-action-audit-trail.md) - Handler-action audit trail (`[Auditable]` + `IAuditScope` + transactional EF sink)
* [ADR-0046](0046-actor-event-consumers.md) - Actor event consumers (`[ConsumeEvent]` on `[Actor]` methods → generated relay)
* [ADR-0047](0047-actor-state-snapshotting.md) - Actor state snapshotting (`IActorState<TState>` + `IActorSnapshotStore`, PostgreSQL default)
* [ADR-0048](0048-single-homed-actors.md) - Single-homed actors — a PostgreSQL home lease, not a cluster
* [ADR-0049](0049-role-leases.md) - Role leases — the leader-election primitive, extracted from the actor home
* [ADR-0050](0050-role-holder-proxy.md) - The role-holder proxy — an in-app ingress rule, not a cluster
* [ADR-0051](0051-postgresql-bulk-insert.md) - Bulk insert as an EF-shaped seam with a PostgreSQL binary COPY provider
* [ADR-0052](0052-ordered-streams.md) - Ordered streams — a sequencer-owned hub, actor stream methods, and a resumable SSE leg
* [ADR-0053](0053-bidirectional-client-connections.md) - Bidirectional client connections — a transport-neutral connection seam; adapters adopted whole
* [ADR-0054](0054-device-identity-and-provisioning.md) - Device identity and provisioning
* [ADR-0055](0055-data-rate-shaping-helpers.md) - Data-rate shaping helpers — write-behind buffer and keyed conflater
* [ADR-0056](0056-postgres-extensions-posture.md) - PostgreSQL extensions are within the one-Postgres positioning; ease the extension-image pain
* [ADR-0057](0057-postgresql-sql-migration-runner.md) - A Flyway-shaped PostgreSQL migration runner for EF-free (AOT) hosts
* [ADR-0058](0058-aot-sql-row-mapping.md) - AOT-native SQL row mapping — explicit generated mappers, not call-site interception
* [ADR-0059](0059-merged-handler-observability-decorator.md) - Merge the always-on tracing + context-enrichment decorators into one observability decorator
* [ADR-0060](0060-database-neutral-migration-core.md) - A database-neutral migration core, and a SQLite provider
* [ADR-0061](0061-virtual-sharded-actors.md) - Virtual-sharded actors — fixed role partitions without a cluster
* [ADR-0062](0062-role-affine-routing-and-outbox-delivery.md) - Role-affine routing and target-group outbox delivery
* [ADR-0063](0063-grpc-unary-transport.md) - gRPC ships as typed unary and server-streaming transport adapters
* [ADR-0064](0064-acknowledgment-gated-outbound-delivery.md) - Acknowledgment-gated outbound delivery is a codec-tier helper, adopted on second demand (proposed)
* [ADR-0065](0065-self-typed-request-markers-and-bound-connection-invoker.md) - Self-typed request markers enable inferred dispatch; ConnectionHandlerInvoker binds per connection
* [ADR-0066](0066-low-allocation-connection-dispatch.md) - Opt-in low-allocation dispatch profile for high-rate connections
* [ADR-0067](0067-connection-sessions-and-single-framer-emit-path.md) - Per-connection sessions and a single framer emit path
* [ADR-0068](0068-aot-sql-binary-copy.md) - Source-generated binary COPY and staged upsert for the AOT SQL tier (proposed)
* [ADR-0069](0069-producer-owned-hot-state-buffering.md) - Producer-owned hot-state primitives — bounded MPSC queue and staged-batch flusher
* [ADR-0070](0070-contract-set-registration.md) - Contract-set registration — unconditional composition of one contract's implementation set
* [ADR-0071](0071-generator-owned-http-endpoint-binding.md) - Generator-owned HTTP endpoint binding — RequestDelegate registrations, not RDG
* [ADR-0072](0072-per-endpoint-customization-and-created-envelope.md) - Per-endpoint HTTP customization hook and the created-resource envelope
* [ADR-0073](0073-leased-work-rows.md) - Leased work rows are a recognized pattern, extracted on second demand
* [ADR-0074](0074-reactive-client-capabilities.md) - Reactive client capabilities — stores over values, with the registry left pure
* [ADR-0075](0075-ambient-tenant-scoping.md) - Ambient tenant scoping — a model-level filter and a write-time stamp, not a per-call-site rule
* [ADR-0076](0076-web-push.md) - Web Push is a framework package — native crypto, a fail-closed endpoint allow-list, and a pluggable browser transport
* [ADR-0077](0077-sql-array-parameters.md) - SQL array parameters are an explicit wrapper, backed by a build-time diagnostic
* [ADR-0078](0078-settings-definitions-and-effective-value-resolver.md) - Settings are declared definitions with one effective-value resolver (proposed)
* [ADR-0079](0079-every-feature-flag-is-declared-and-has-exactly-one-owner.md) - Every feature flag is declared and has exactly one owner (proposed)
* [ADR-0080](0080-errors-are-a-declared-contract-with-a-stable-code.md) - Errors are a declared contract with a stable code (proposed)
* [ADR-0081](0081-one-migration-plan.md) - One migration plan — SQL, code and EF steps share a version sequence, a history and a lock (proposed)
* [ADR-0082](0082-requiredness-comes-from-nullability-in-both-directions.md) - Requiredness comes from nullability, in both directions (proposed)
* [ADR-0083](0083-installable-app-shell.md) - The installable-app shell is a framework npm package — safe caching rules built in, the privacy choice left to the app (proposed)
* [ADR-0084](0084-authenticating-proxy-identity.md) - Authenticating-proxy identity is an optional package on IdentityModel's ConfigurationManager (proposed)
* [ADR-0085](0085-handler-declarations-are-projected-not-copied.md) - Handler declarations are projected to clients and hosts, not copied (proposed)
* [ADR-0086](0086-one-answer-for-unexpected-errors.md) - One framework-owned answer for unexpected exceptions, on every transport (proposed)
* [ADR-0087](0087-transaction-scoped-locks-and-one-advisory-key-scheme.md) - Transaction-scoped locks are a framework helper with one advisory key scheme (proposed)
* [ADR-0088](0088-entity-lifecycle-events-for-soft-references.md) - Deletions of marked entities are domain events, published with their database cascades (superseded by ADR-0090)
* [ADR-0089](0089-resource-authorization-composes.md) - Resource authorization composes — hidden-as-missing, owner-derived filters, combinators (proposed)
* [ADR-0090](0090-references-between-records-stay-in-the-database.md) - References between records stay in the database — entity lifecycle events are removed
* [ADR-0091](0091-query-sources-and-requery-hints.md) - A query declares its source entities; committed writes to them become re-query hints (proposed)
* [ADR-0092](0092-runtime-async-on-net11.md) - The net11 build compiles with runtime async
* [ADR-0093](0093-typst-pdf-rendering.md) - Typst PDF rendering is a framework package — the pinned CLI, bundled by the build, one sandboxed workspace per document (proposed)
* [ADR-0094](0094-in-process-typst-engine.md) - An in-process Typst engine is an opt-in second implementation of ITypstCompiler — prototype findings (proposed)
* [ADR-0095](0095-one-resource-rule-for-list-point-check-and-complement.md) - One resource rule for the list, the point check, the complement and the inverse (proposed)
* [ADR-0096](0096-the-repository-is-the-memory.md) - Harness memory is off; the repository is the memory
* [ADR-0097](0097-adopt-the-agentic-repository-layout.md) - The repository follows the agentic repository template's layout, with docs/ shared between the knowledge bundle and the website
* [ADR-0098](0098-streamed-files-for-http-only-endpoints.md) - HTTP-only endpoints stream files through ElarionStreamedFile (proposed)
* [ADR-0099](0099-blob-stores-record-a-content-sha256.md) - Blob stores record the content's SHA-256 while they save it (proposed)
* [ADR-0100](0100-the-schema-exports-constants-presets-and-limits.md) - The schema exports declared constants, request presets and typed limits
* [ADR-0101](0101-a-decision-required-answer.md) - A request that needs the caller's decision answers DecisionRequired (proposed)
* [ADR-0102](0102-rules-are-expressions-composed-once.md) - A rule is one expression, composed into queries and compiled for memory (proposed)
* [ADR-0103](0103-one-permission-decision-and-members-visible-by-permission.md) - One permission decision for the gate and the code, and response members visible by permission
* [ADR-0104](0104-previewable-commands.md) - A command previews by running and rolling back, and commits only the outcome it previewed
* [ADR-0105](0105-uploaded-files-are-handler-parameters.md) - An uploaded file is a handler parameter
* [ADR-0106](0106-upload-policies-choose-the-staging-limits.md) - An upload names a policy that chooses its staging limits (proposed)
* [ADR-0107](0107-a-shared-entity-names-the-module-that-writes-it.md) - A shared entity may name the one module that writes it (proposed)

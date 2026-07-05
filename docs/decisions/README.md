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
- [ADR-0022: Inbox pattern for integration-event consumers (idempotent consumers) — Proposed](0022-inbox-idempotent-event-consumers.md)
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
- [ADR-0038: Client-assigned entity identity — UUIDv7, a truthful key model, and ELID001](0038-client-assigned-entity-identity.md)

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

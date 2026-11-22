# Update log

## 2026-11-22

* **Creation**: [ADR-0103](decisions/0103-one-permission-decision-and-members-visible-by-permission.md), one permission decision (IPermissionChecker) shared by the gate, handler code and response members visible by permission ([VisibleWith]); glossary terms Permission checker and Member visible by permission.

## 2026-11-21

* **Creation**: proposed [ADR-0100](decisions/0100-the-schema-exports-constants-presets-and-limits.md) (constants, request presets and typed limits in the schema), [ADR-0101](decisions/0101-a-decision-required-answer.md) (a DecisionRequired answer with typed options and a client helper that resends) and [ADR-0102](decisions/0102-rules-are-expressions-composed-once.md) (one expression per rule, composed into queries and compiled for memory), from gaps a consuming application hand-rolled.
* **Creation**: [ADR-0098](decisions/0098-streamed-files-for-http-only-endpoints.md) — HTTP-only endpoints stream files through ElarionStreamedFile, amending [ADR-0039](decisions/0039-binary-file-responses.md); the [glossary](../GLOSSARY.md) gained *streamed file*.
* **Creation**: [ADR-0099](decisions/0099-blob-stores-record-a-content-sha256.md) — blob stores record the content's SHA-256 while they save it.
* **Update**: [ADR-0087](decisions/0087-transaction-scoped-locks-and-one-advisory-key-scheme.md) gained an amendment for locking several rows or advisory keys in one stable order.
* **Update**: [ADR-0095](decisions/0095-one-resource-rule-for-list-point-check-and-complement.md) now decides the inverse query instead of deferring it (the caller owns the candidate set, the principal directory agrees with the live principal by contract, an unrestricted alternative is every candidate), and [ADR-0089](decisions/0089-resource-authorization-composes.md) points to it; the [glossary](../GLOSSARY.md) gained *principal directory*.
* **Update**: fixed seven relative links that pointed at files that no longer exist under those names, in [ADR-0004](decisions/0004-handler-result-caching.md), [ADR-0006](decisions/0006-incremental-source-generator-conventions.md), [ADR-0032](decisions/0032-frontend-contribution-model.md), [ADR-0052](decisions/0052-ordered-streams.md) and [ADR-0075](decisions/0075-ambient-tenant-scoping.md).
* **Update**: [ADR-0096](decisions/0096-the-repository-is-the-memory.md) now says that its two statements about the layout and the missing log were overtaken by [ADR-0097](decisions/0097-adopt-the-agentic-repository-layout.md).
* **Creation**: the [glossary](../GLOSSARY.md) with the framework's terms, the [spec format](../specs/README.md) with the specs of the two check tools, and the [issue tracker](agents/issue-tracker.md) and [domain docs](agents/domain.md) pages that configure the engineering skills.
* **Update**: every ADR got its frontmatter envelope and the list of records moved from the README to [decisions/index.md](decisions/index.md); the [ADR format](decisions/README.md) now states the status mapping the check enforces; the three research notes moved from decisions/research/ to [research/](research/README.md).
* **Creation**: the knowledge bundle: the [index](index.md), the [guide](guide.md) to what lives where, the [workflow](workflow.md) and the [architecture page](architecture.md), with [ADR-0097](decisions/0097-adopt-the-agentic-repository-layout.md) recording how the agentic repository template's layout was adapted.

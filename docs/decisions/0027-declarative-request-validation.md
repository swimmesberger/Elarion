# ADR-0027: Declarative request validation

- Status: Accepted
- Date: 2026-07-02
- Related: [ADR-0017](0017-dependency-light-core.md) (opt-in sibling packages carry heavy runtime dependencies),
  [ADR-0023](0023-canonical-json-serialization.md) (the canonical JSON options and naming policy the error paths
  and schema export read), [ADR-0026](0026-openapi-http-transport.md) (the OpenAPI document the constraints flow
  into), [ADR-0009](0009-authorization-building-blocks.md) / [ADR-0016](0016-feature-flag-gating.md) (the
  generator-attached pipeline-gate pattern this reuses), [ADR-0006](0006-incremental-source-generator-conventions.md)
  (the incrementality conventions the new generator follows).

## Context

Validation in Elarion was FluentValidation-shaped: rules lived in `AbstractValidator<T>` classes discovered by
`ModuleValidatorRegistrationGenerator`, executed by an *application-owned* decorator, and surfaced as a flat
`IReadOnlyList<string>` of messages. That model has a structural flaw for a framework whose contracts are
schema-driven: **the rules are imperative lambdas, invisible to every contract surface.** The Billing sample
already showed the drift — `.MaximumLength(100)` in a validator that never reached `rpc-schema.json`, the
OpenAPI document, or the generated Zod client.

The dividing line that matters is not "simple vs. complex" validation but **what a wire contract can express**.
JSON Schema (and therefore OpenAPI, the JSON-RPC schema, and Zod generated from either) can carry exactly one
category: static, single-property shape constraints — lengths, ranges, patterns, formats, enums, requiredness.
Cross-field comparisons, conditional rules, and async/database-backed checks are inherently server-side. Keeping
a validator layer that spans both categories guarantees exportable rules hide in unexportable places.

Async pre-handler validation is also independently wrong in this architecture: the validation decorator runs
*before* `TransactionDecorator`, so a database-backed check (e.g. uniqueness) is TOCTOU — the constraint in the
database is the real fence and the handler must handle the conflict anyway — and it duplicates queries the
handler performs moments later inside the transaction.

Three facts shaped the implementation choice:

- **`Microsoft.AspNetCore.OpenApi` 10.x already maps DataAnnotations onto schemas** (`[Range]`,
  `[MinLength]`/`[MaxLength]`/`[Length]`/`[StringLength]`, `[RegularExpression]`, `[Url]`, `[Base64String]`),
  reading attributes through `JsonPropertyInfo.AttributeProvider` — verified to work under this repo's
  reflection-off source-generated resolver chain. Annotating DTOs makes the OpenAPI leg nearly free.
- **`Microsoft.Extensions.Validation` (net10) is a reusable, transport-free enforcement base**: dependencies are
  only `Microsoft.Extensions.DependencyInjection.Abstractions` + `Microsoft.Extensions.Options`; it is trimmable
  with NativeAOT test coverage; `ValidationOptions.Resolvers` (`IValidatableInfoResolver`, first-match-wins) is
  an explicit seam for source-generated resolvers; the base classes implement the hard parts (recursive object
  graph walking with `MaxDepth` cycle protection, `[Required]` short-circuiting, `IValidatableObject`, record
  primary-constructor parameter attributes, error-message/resource resolution); and `ValidateContext.ValidationErrors`
  is a field-*path*-keyed `IReadOnlyDictionary<string, IEnumerable<string>>` — exactly the structured error shape
  the transports want.
- **Microsoft's bundled `ValidationsGenerator` does not fit Elarion** and is excluded: it emits only when it can
  intercept a literal `services.AddValidation()` call site in the same compilation (C# interceptors — impossible
  from inside a package-provided wrapper), discovers types from minimal-API endpoint signatures (invisible here:
  Elarion's `Map*` calls are themselves generator output, and generators cannot see other generators' output) or
  a same-compilation `[ValidatableType]` attribute (no cross-assembly story), and its emitted code still
  materializes attributes and reads properties via cached reflection.

## Decision

Adopt a **two-tier validation model** and remove the FluentValidation integration.

**Tier 1 — wire-shape constraints are declarative, on the request DTO, as `System.ComponentModel.DataAnnotations`
attributes.** One declarative source feeds four surfaces: the generated runtime validator, the JSON-RPC schema,
the OpenAPI document, and the generated Zod client. Requiredness comes from nullability + the `required`
modifier (already exported); `[Required]` is not needed. Reusable custom constraints subclass the mapped
attributes (e.g. a `[Slug]` deriving from `RegularExpressionAttribute` gets enforcement *and* every schema
surface for free).

**Tier 2 — business rules live in the handler** (or a domain `[Service]` it calls): cross-field comparisons,
conditional rules, and every async/database-backed check, returning `AppError.Validation`/`Conflict` through the
normal `Result<T>` channel — inside the transaction, where those checks are actually sound. There is no
standalone validator class layer anymore.

### Enforcement: `Microsoft.Extensions.Validation` base, Elarion generator

1. **Seam in `Elarion.Abstractions.Validation`** (dependency-free, per ADR-0017):
   `IRequestValidator` — `ValueTask<RequestValidationErrors?> ValidateAsync(Type requestType, object request,
   CancellationToken ct)` returning `null` when valid — and the `RequestValidationErrors` record carrying
   `FieldErrors: IReadOnlyDictionary<string, string[]>` keyed by wire-named field path (empty key = not
   field-specific). The framework-owned `ValidationDecorator<TRequest, TResponse>`
   (`Elarion.Abstractions.Pipeline`, beside `TransactionDecorator`, constrained on
   `IResultFailureFactory<TResponse>`) calls the seam and fails with `AppError.Validation` before the request
   reaches caching, the pipeline, or the transaction.
2. **`ValidationErrorData` gains structure (additive):** `FieldErrors: IReadOnlyDictionary<string, string[]>?`
   joins the existing flat `Errors` list, and `AppError.Validation` gains a field-keyed factory overload. The
   HTTP error mapper surfaces `FieldErrors` as the RFC 7807 `errors` extension (the
   `ValidationProblemDetails`/`HttpValidationProblemDetails` shape); the JSON-RPC error carries it in `error.data`.
   Field paths use the canonical JSON naming policy (ADR-0023), so error keys match the wire property names the
   client sent — a Zod pre-flight failure and a server 400 address the same field the same way.
3. **New opt-in sibling package `Elarion.Validation`** (the `Elarion.Caching` shape — `Microsoft.Extensions.Options`
   bars the dependency from core): references `Microsoft.Extensions.Validation` with `ExcludeAssets="analyzers"`
   (runtime base only, Microsoft's generator never runs), implements `IRequestValidator` over
   `ValidationOptions`/`IValidatableInfoResolver` + `ValidateContext`, translates error paths through the canonical
   naming policy, and registers via `AddElarionValidation([configure])` (which also calls `AddElarionJson()`), plus
   the `AddElarionValidationResolver(resolver)` helper generated module code calls. The package suppresses the
   `[Experimental("ASP0029")]` surface internally.
4. **`ValidationResolverGenerator`** (in `Elarion.Generators`, conditional on the compilation referencing
   `Elarion.Validation`): discovers each module's handler request types structurally (no extra attribute — the
   handler's `TRequest` is already known), walks the request type graph for `ValidationAttribute`-annotated
   properties (or `IValidatableObject`), and emits a per-module `IValidatableInfoResolver` whose
   `ValidatableTypeInfo`/`ValidatablePropertyInfo` subclasses return **constant-constructed attribute arrays**
   (`new StringLengthAttribute(100) { MinimumLength = 3 }` from compile-time `AttributeData`) — no runtime
   attribute reflection, unlike Microsoft's generator. Registration flows through the module's existing gated
   `ConfigureDefaultServices` hook (`AddValidators`, vacated by the FluentValidation generator's removal), so a
   disabled module contributes no validation metadata. Follows every ADR-0006 convention (syntax-provider
   discovery, equatable models, diagnostics-as-data, tracking names + cache-reuse tests).
5. **`HandlerRegistrationGenerator` auto-attaches `ValidationDecorator`** just inside the feature gate
   (tracing → authorization → feature gate → **validation** → `[DefaultPipeline]` list → handler) for any handler
   whose request is validatable — the same opt-in-by-construction pattern as authorization and feature gating,
   sharing one "is validatable" computation with the resolver generator. Diagnostics: `ELVAL001` when the
   response type cannot represent failure (mirrors `ELAUTH001`), `ELVAL002` (warning) when a request carries
   validation attributes but `Elarion.Validation` is not referenced — the attributes would be documented in the
   schemas but unenforced, which must be a visible choice, never a silent one.

### Contract export: the same attributes, three schema surfaces

6. **JSON-RPC schema:** `JsonRpcSchemaExporter` composes a constraint-injection transform beside the existing
   `[Description]` injection (the exporter is already `RequiresDynamicCode` build-time surface), mapping
   attribute → keyword identically to Microsoft's OpenAPI mapping: `[Range]` → `minimum`/`maximum`
   (`exclusiveMinimum`/`exclusiveMaximum` for exclusive bounds), `[MinLength]`/`[MaxLength]`/`[Length]`/
   `[StringLength]` → `minLength`/`maxLength` (`minItems`/`maxItems` on array schemas), `[RegularExpression]` →
   `pattern`, `[Url]` → `format: "uri"`, `[Base64String]` → `format: "byte"`, plus `[EmailAddress]` →
   `format: "email"`. MCP tool input schemas share the same schema builder and inherit the constraints.
7. **OpenAPI:** Microsoft's mapping covers everything except `[EmailAddress]`; `Elarion.AspNetCore.OpenApi` adds
   a small schema transformer for `format: "email"` parity so the two documents never diverge.
8. **TypeScript client generator:** the `JsonSchema` model and the Zod emitter learn the constraint keywords
   (`minLength`/`maxLength` → `.min()`/`.max()`, `pattern` → `.regex()`, `minimum`/`maximum` → `.gte()`/`.lte()`,
   exclusive variants → `.gt()`/`.lt()`, `minItems`/`maxItems` → array `.min()`/`.max()`, `type: "integer"` →
   `.int()`, `format` `uuid`/`email`/`uri` → `.uuid()`/`.email()`/`.url()`), and the generator now emits **params
   schemas** alongside result schemas — the client pre-validates requests before the wire by default (opt-out),
   completing the loop: tier-1 failures are caught client-side pre-flight, tier-2 failures come back as
   field-keyed 400s that map onto the same field paths.

### Removal

9. `ModuleValidatorRegistrationGenerator`, `[GenerateModuleValidators]`, and the FluentValidation package
   references are deleted (pre-1.0, no compatibility shim). The Billing sample migrates its validators to
   attributes + in-handler checks and drops its hand-written `ValidationDecorator` for the framework one. Teams
   that want FluentValidation can still wire it as an app-owned decorator — which is all it ever was.

## Consequences

- **Exportable rules cannot drift from the contract.** A shape constraint has exactly one legal home, and every
  surface — runtime enforcement, `rpc-schema.json`, OpenAPI, Zod, MCP tool schemas — reads it. What the client
  pre-validates is by construction what the server enforces.
- **The honest boundary is explicit.** Cross-field and async rules are server-only *by design*; they surface
  through field-keyed `ValidationErrorData` that renders identically to a client-side Zod failure. Nothing
  pretends a uniqueness check can run in a browser.
- **`[Experimental("ASP0029")]` risk is contained.** The entire M.E.Validation extensibility surface is
  experimental and still moving in 11.0 previews. Only `Elarion.Validation` and generated resolver code touch it;
  the seam (`IRequestValidator`) is Elarion-owned, and the base is ~10 small MIT files — vendoring or replacing
  it behind the seam is cheap if the API shifts.
- **Property value access in the enforcement path is DAM-annotated reflection** (the M.E.Validation base's
  walker) — trim/AOT-safe and NativeAOT-tested, but not reflection-free. Attribute materialization *is*
  reflection-free (constant-constructed by the Elarion generator). If a fully generated walker is ever wanted,
  it lands behind the same seam.
- **Validation runs pre-transaction and costs nothing when absent**: unannotated requests get no decorator, no
  resolver entry, no lookup.
- **Breaking (pre-1.0):** the FluentValidation generator/attribute are gone; `ValidationErrorData` consumers see
  a new optional `FieldErrors` member; sample pipelines change. Downstream apps migrate rules to attributes or
  re-own a FluentValidation decorator locally.

# ADR-0080: Errors are a declared contract with a stable code

- Status: Proposed
- Date: 2026-10-04
- Related: [ADR-0005](0005-cross-module-error-channel.md) (the `Result`/`AppError` channel and the gRPC mapping),
  [ADR-0063](0063-grpc-unary-transport.md) (the `elarion-error-kind` trailer),
  [ADR-0071](0071-generator-owned-http-endpoint-binding.md) (HTTP results translate `AppError` to ProblemDetails),
  [ADR-0027](0027-declarative-request-validation.md) (validation errors), [ADR-0082](0082-requiredness-comes-from-nullability-in-both-directions.md)
  (the schema rule the error payloads follow).

## Context

`AppError` was `{ Kind, Message, Data }`. The JSON-RPC code, HTTP status and gRPC status all derive from `Kind`, so a
client could tell seven categories apart; two failures of one kind (an expired token versus a malformed one) differed
only in `Message`, which is prose and not a contract. `Data` was an untyped `object?` whose shape appeared in no schema,
and only validation errors had a documented shape. The wire shape also differed per error: validation sent its payload
as `error.data`, everything else sent nothing.

Elarion already compiles a manifest of handler facts and exports a JSON-RPC schema that drives the generated
TypeScript client. Errors are the part of a handler's contract that bypassed that chain.

## Decision

Errors are **declared once on the handler, verified at compile time where knowable, and flow into DI, the schema and
the generated client** — the same shape as every other declared contract.

1. **`AppError.Code` is always present.** A stable string; each `ErrorKind` has a default code (`validation`,
   `not_found`, `conflict`, `forbidden`, `unauthorized`, `business_rule`, `internal`, in `ErrorCodes`) and every
   factory takes a specific one (`AppError.Conflict(msg, code: "seat.taken", data: payload)`). `Validation` is just
   the `validation` code carrying `ValidationErrorData`. A code is lower-case ASCII letters, digits and underscores in
   dot-separated segments, validated when set. The property reads the kind's default when none was set, so
   `new AppError { Kind, Message }` stays valid, equality compares the effective code, and an error stored before
   codes existed replays with its kind's default.
2. **Handlers declare their errors.** `[ProducesError(code, kind, typeof(Payload))]` (and kind-default forms) on the
   handler. The generator reads them with the rest of the operation, adds the failures the framework attaches where they
   are statically knowable — `validation` for a validatable request, `unauthorized`/`forbidden` for `[Require*]`,
   `not_found` for `[FeatureGate]`, the three `idempotency.*` codes for `[Idempotent]` — validates them (`ELERR001`
   invalid code, `ELERR002` conflicting declarations), and publishes the sorted list in the assembly manifest
   (schema version 2) and the generated `HandlerDispatcher.Map(..., errors:)` registration, so referenced module
   assemblies contribute theirs too.
3. **Undeclared codes are checked at runtime, in development.** What a handler body returns is not statically
   knowable — an `AppError` can come from a helper, a service or a branch — so a static diagnostic would either miss
   or falsely flag. The route verifies each failure against its declared contract and reports a violation (an
   undeclared code, or a declared code with a different kind) to `IErrorContractMonitor`; the default registration
   logs each distinct violation once as a warning in the Development environment and does nothing elsewhere, so
   production pays one dictionary lookup per failed result. `internal` failures and the default `unauthorized` and
   `forbidden` codes are always admitted, because assembly or module authorization defaults attach them without a
   per-handler attribute the generator could see. Hand-wired routes without a contract are not checked.
4. **One wire shape everywhere.** JSON-RPC `error.data = { code, data? }` on every error, protocol errors included
   (`parse_error`, `invalid_request`, `method_not_found`, `invalid_params`); the numeric `error.code` stays the kind
   mapping. HTTP ProblemDetails always carries `code` and, for any typed payload, a `data` extension (a
   `ValidationErrorData` payload stays the standard `errors` map). gRPC always carries the `elarion-error-code`
   trailer next to the kind trailer. MCP structured content is `{ code, data? }`. An idempotency replay returns the
   stored `AppError`, so the code and payload round-trip.
5. **The schema lists the contract and the client types it.** Each method gets `errors: { <code>: { kind, data? } }`
   in `rpc-schema.json` (omitted when empty, ordinal code order). The TypeScript generator emits `errors` per method
   in `RpcMethods`, generated Zod schemas for declared payloads, and a client where `RpcError<TCode, TData>` has
   `code` (the stable string), `data` (typed), and `rpcCode` (the numeric JSON-RPC code, renamed so it cannot be
   confused with `code`). `RpcMethodError<M>` is the union of variants discriminated by `code`; `isRpcMethodError`
   narrows a caught error. A declared payload is validated like a result; a mismatch is a protocol error.

## Consequences

- Clients branch on a stable identifier with typed payloads, without parsing messages; the declared set is visible in
  the schema and in review.
- **Breaking.** `AppError` factories take `(message, code, data)` (`AppError.X(msg, data)` call sites change to
  `data:`); `RpcError.Data`/`RpcErrorResponse.Data` are the required `RpcErrorData`; `error.data` changes shape for
  validation errors (`{ code: "validation", data: ValidationErrorData }`); the manifest schema version is 2 and
  referenced module assemblies must be rebuilt; the TypeScript `RpcError.code` is now a string and the number is
  `rpcCode`; MCP structured content changed; `elarion-error-code` is always present.
- The runtime check costs one lookup per failed result and logs at most once per (operation, code, kind) in
  development; a violation is a warning, never a failed request.
- Implied errors cover only what is statically visible per handler; authorization defaults at assembly or module
  scope are covered by the runtime admission rule instead of the schema.

## Rejected alternatives

- **The compatibility-driven variant: an optional `Code` plus an `errorCode`/`details` envelope only for coded
  errors.** It kept codeless errors byte-identical, so the wire had two shapes, clients had to detect an envelope by
  its exact key set, `RpcError` needed `errorCode`/`errorData`/`hasErrorCode` beside the numeric `code`, and nothing
  declared which codes a method could produce. Optionality forwards the problem to every consumer; an always-present
  code with the kind default costs nothing.
- **A static diagnostic for undeclared codes.** Returned errors are not analyzable in general, so the check would be
  incomplete and trusted anyway; a runtime check in development is honest about what it verifies.
- **Throwing on a violation in development.** A contract drift should be visible, not turn a working call into a
  failure; a warning keeps the signal without behaviour that differs by environment.
- **Merging `{ code, ...data }` into `error.data`.** It needs a second serialization pass over an `object?` payload and
  collides with payloads that already have a `code` member.
- **Declaring errors in a side registry instead of on the handler.** The contract belongs next to the code that
  returns it, where attributes already carry authorization, idempotency and gates.

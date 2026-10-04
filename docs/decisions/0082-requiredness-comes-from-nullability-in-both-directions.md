# ADR-0082: Requiredness comes from nullability, in both directions

- Status: Proposed
- Date: 2026-10-04
- Related: [ADR-0027](0027-declarative-request-validation.md) (the two-tier validation model that cited `required` for
  requiredness), [ADR-0023](0023-canonical-json-serialization.md) (canonical serializer options),
  [ADR-0080](0080-errors-are-a-declared-contract-with-a-stable-code.md) (error payloads use the same rule).

## Context

The schema exporter marked a property `required` only when System.Text.Json did: the C# `required` modifier or
`[JsonRequired]`. Requiredness therefore depended on a modifier that says nothing about what the server actually
writes. A response member `string Name` without `required` was exported optional although the serializer always wrote
it, so generated clients typed it `name?: string` and every consumer null-checked a value that is never absent. The
same member declared `required string? Note` was exported required although the canonical `WhenWritingNull` default
omits it when null. A request with a defaulted member was just as unreliable. Nullable annotations already say exactly
what is present on the wire.

## Decision

**One rule for request and response schemas: a non-nullable property is required on the wire; a nullable property is
optional.** For **request** types a non-nullable property with a default value — a constructor parameter default, or
an initializer that yields a non-default value — is also optional to send. The exporter computes `required` from this
rule and replaces STJ's list entirely, so `required`/`[JsonRequired]` no longer affect the schema (they remain useful
to the compiler). Constructor defaults are read from the serializer's parameter metadata; an initializer is observed on
one constructed instance per type. An initializer equal to the CLR default (`= 0`, `= false`) is indistinguishable from
none; the documented fix is a constructor default or a nullable member. The same rule applies to MCP input schemas,
event payloads and error payloads.

**The serializer enforces the contract instead of the schema carrying opt-outs.**
- The canonical options enable `RespectNullableAnnotations` and `RespectRequiredConstructorParameters` (and the HTTP
  JSON options copy them): `null` for a non-nullable member and a constructor parameter without a default are rejected
  on read, and a null non-nullable member fails on write. They are fixed, not configurable — a switch would let the
  exported schema stop describing the wire.
- Result and event types must not opt non-nullable members out of being written. `[JsonIgnore(Condition =
  WhenWritingDefault)]` (or `WhenWritingNull` on a non-nullable reference) on a member reachable from a handler
  response is the compile-time error `ELRPC004`; the exporter fails with the member name for event payloads, for
  attributes the generator cannot see, and for serializer options using `DefaultIgnoreCondition = WhenWritingDefault`.
  Making the member nullable is the way to declare it optional.

There is no exporter flag or CLI switch to restore the old behaviour.

## Consequences

- Generated TypeScript types and Zod schemas match what the server writes and reads: response members are present
  unless nullable, request members the client may omit are exactly the nullable and defaulted ones.
- **Breaking.** `required string? X` becomes optional and a non-`required` non-nullable property becomes required in
  `rpc-schema.json`; clients must send non-nullable request members that have no default; handlers whose response
  graph carries an ignore condition on a non-nullable member no longer compile (`ELRPC004`); `null` for a
  non-nullable property is now rejected by the canonical serializer. Schemas and generated clients must be regenerated.
- ADR-0027's tier-1 wording ("NRT plus `required`") is refined: nullability is the requiredness source; `required`
  is a construction-site aid. Validation of presence beyond what the serializer enforces stays a DataAnnotations
  concern (`[Required]`).
- Initializer-based defaults are detected heuristically; constructor defaults are exact.

## Rejected alternatives

- **The compatibility-driven variant: keep STJ's `required` and add an opt-in to also mark always-written response
  properties required.** It left two rules and a flag consumers had to know about, and the default stayed the
  inaccurate one. Making the accurate rule the only rule removes the flag.
- **An opt-out (`--no-required-responses`, an exporter option) for types that omit members.** The serializer is the
  authority on what is written; allowing conditional writes of required members makes the schema lie. The member should
  be nullable.
- **Reading defaults from the generator for every request type.** Exact, but it needs a per-type metadata table for
  the whole request graph and a second registration channel; the runtime probe plus constructor metadata covers the
  idiomatic shapes.
- **Treating the `required` modifier as authoritative for requests only.** It keeps the asymmetry that caused the
  inconsistency and teaches two meanings of one keyword.

---
type: Architecture Decision Record
title: "ADR-0103: One permission decision for the gate and the code, and response members visible by permission"
description: "IPermissionChecker answers whether the caller holds a permission for the [RequirePermission] gate, handler code and [VisibleWith] members; the pipeline hides a marked response member from callers without its permission through a generated copy."
status: stable
---
# ADR-0103: One permission decision for the gate and the code, and response members visible by permission

- Status: Accepted (implemented 2026-11-22)
- Date: 2026-11-22
- Related: [ADR-0009](0009-authorization-building-blocks.md) (authorization building blocks),
  [ADR-0030](0030-client-capability-bootstrap.md) (client capability bootstrap),
  [ADR-0085](0085-handler-declarations-are-projected-not-copied.md) (declarations are projected, not copied)

## Context

`[RequirePermission]` gates a whole handler. Two needs sit below that gate, and applications build both by hand.

**A permission check that is not a gate.** One application on Elarion has 34 places that call
`currentUser.HasClaim(IdentitySeeder.PermissionClaimType, ElarionPermissions.X.Read)`. Some pass string literals
(`"settlements.write"`), some add `IsInRole(Roles.Admin)` because administrators hold everything. Each call repeats the
claim type the framework already knows (`AuthorizationOptions.PermissionClaimType`), and the administrator rule lives in
every call instead of one place, so the gate and the code can disagree. `IAuthorizer.AuthorizeGateAsync` gives the
gate's answer but is asynchronous and takes a requirements object — too heavy for a `bool` in a projection.

**A member only some callers may see.** The caller may run the handler, but one member of the answer belongs to a
narrower audience: a bank-import preview runs for `banking.write` holders, but the stored booking a line resembles may
only go to `banking.read` holders; the opening balance in an account list; payments in invoice lists. Each handler nulls
the member by hand, a cached or replayed answer has to remember to do it again, and the exported schema cannot say why
the member may be missing.

## Decision

**`IPermissionChecker` is the one permission decision.** `bool Holds(string permission)` in
`Elarion.Abstractions.Authorization`, with `Holds(resource, verb)`, `HoldsAny` and `HoldsAll` conveniences. The default,
`ClaimsPermissionChecker` (core), answers `IsAuthenticated && HasClaim(PermissionClaimType, permission)` — exactly the
gate's rule. `AddElarionAuthorization()` registers it scoped with `TryAdd`, as its own type too, so a host replacement
registered first can wrap it. `ClaimsAuthorizer` evaluates every `[RequirePermission]` through it, so a host whose
administrators hold every permission registers one checker and the gate, handler code and visibility follow. The
session snapshot lists the permission claims plus every catalog permission the checker grants, so the client projection
agrees with the gate.

**`[VisibleWith(permission)]` marks a nullable response member** (property, or a positional record's parameter; the
permission as `ElarionPermissions.X.Y` or as `resource, verb`). The handler fills it for everybody; the pipeline hides it:

- The handler-registration generator walks the handler's `Result<T>` value type at compile time — records and structs,
  arrays, `List<T>`, `ImmutableArray<T>`, the read-only collection interfaces, `Nullable<T>` and `ElarionCreated<T>`,
  cycle-safe through a fixpoint — and emits one copy function per type on the way, built from `with` expressions. No
  reflection at run time; trimming and AOT are unaffected.
- A new built-in `VisibilityDecorator<TRequest, TValue>` sits just inside authorization: outside the feature gate,
  validation, caching, idempotency and the transaction, so a cached or replayed answer is stored whole and hidden per
  caller, and an in-process call is hidden like a remote one. A caller holding every permission the response uses gets
  the handler's value unchanged (no copy); anyone else gets the copy. A failure whose payload is the response type is
  hidden the same way. Without a registered checker every marked member is hidden.
- Whatever the copy cannot pass through is a build error, never a disclosed member: `ELAUTH003` (a marked member is not
  nullable) and `ELAUTH004` (the response is not `Result<T>`; a type on the way is neither record nor struct; a
  rewritten member has no accessible `init`/`set`; another collection, such as a `HashSet<T>` or a dictionary, holds
  marked members; a type is inaccessible). A singleton handler cannot use it (`ELSG013`).
- `rpc-schema.json` exports `x-elarion-visible-with: "<permission>"` on the member, and the generated TypeScript
  documents it on the property. Like every client-side projection, that is a hint; the pipeline decides.

## Consequences

- One place decides what a caller holds; in-handler checks, the gate, hidden members and the session snapshot cannot
  drift apart, and an "administrators hold everything" rule is one class.
- Handlers stop nulling members; a response type says which of its members are restricted, and clients learn it from
  the schema.
- A hidden member costs one copy of the response per call for callers without the permission, nothing for those with it.
- The walk follows declared types and, for a polymorphic base, the derived types its `[JsonDerivedType]` attributes
  declare — the only derived types System.Text.Json writes with their own members — dispatching on the runtime type,
  most-derived first. An interface without them that reaches a marked member is a build error. A member declared as
  `object` is written by its runtime type and is not looked into.
- Generated SQL filters (`[ResourceFilter]`'s `BypassPermission`, `QueryAuthorizers.WhenPermission`) still read the
  claims, because they translate to SQL over the principal directory; a host whose checker grants more than the claims
  grants those permissions as claims too.
- `ClaimsAuthorizer` and `SessionHandler` gain optional constructor parameters; their existing callers are unaffected.

## Rejected alternatives

- **An `ICurrentUser.HasPermission(...)` extension reading a default claim type.** An extension cannot see the configured
  claim type or a host's replacement rule, so it would repeat today's drift with less typing.
- **Asking `IAuthorizer.AuthorizeGateAsync` from handler code.** Asynchronous and requirement-object shaped for a
  synchronous `bool`; and visibility needs the decision per member, not per handler.
- **Redacting in the serializer (a JSON contract modifier with an ambient caller).** It would act per transport, miss
  in-process calls, depend on an ambient value surviving into serialization, and read attributes reflectively at run
  time. The handler pipeline is where Elarion enforces authorization.
- **Runtime reflection over the response graph.** Breaks the AOT/trimming posture and cannot report an uncopyable shape
  before the application runs.
- **Omitting the member from the schema per caller, or a separate response type per audience.** One contract that says
  which members are restricted is what the generated client can type; per-audience types multiply handlers.
- **Mutating the response in place.** Response records are immutable by convention, and a cached or replayed instance is
  shared between callers.

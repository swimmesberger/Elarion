# ADR-0085: Handler declarations are projected to clients and hosts, not copied

- Status: Proposed
- Date: 2026-10-25
- Related: [ADR-0009](0009-authorization-building-blocks.md) (authorization is declared on handlers and enforced in
  the pipeline), [ADR-0032](0032-frontend-contribution-model.md) (`when` is a UX projection),
  [ADR-0071](0071-generator-owned-http-endpoint-binding.md) (generated HTTP registrations),
  [ADR-0080](0080-errors-are-a-declared-contract-with-a-stable-code.md) (the per-method error contract).

## Context

A handler's authorization declarations — `[AllowAnonymous]`, `[RequirePermission]`, `[RequireRole]`, the
`[ElarionAuthorizationDefaults]` in its scope — are the single source of truth for who may call an operation. Three
places outside the pipeline need the same facts and, without framework support, a consuming application copies them:

- **The UI.** To grey out an action for a user who cannot call it, the application names the permission again at
  every call site and keeps the copies in step with a lint that scans the C# sources. Handler markers the UI must
  honour (a slow operation's timeout, a marker for a special session mode) are invisible to the client entirely.
- **ASP.NET's own authorization.** A host that sets a deny-by-default `FallbackPolicy` closes the generated route of an
  `[AllowAnonymous]` handler, because the route carries no `IAllowAnonymous`; and `MapElarionEndpoints` returned
  `void`, so the host could not add a convention to Elarion's routes at all. The workaround — mapping inside a
  `MapGroup("")` and opening routes by path prefix — is a second list of public operations.
- **Global authorization rules.** A rule that depends on a handler marker ("every write must declare whether it is
  allowed in this session mode") cannot be an `IGlobalAuthorizationRule`, because `AuthorizationContext` does not
  name the handler; it becomes an application decorator with its own ordering problem.

## Decision

Project the declarations outward from the one source; never ask an application to restate them.

1. **Per-method client metadata in the contract.** The generator computes, per `[Handler]`, what a client can know
   before calling — `[AllowAnonymous]`, the required permissions and roles, the authentication an
   `[ElarionAuthorizationDefaults]` in scope requires (resolved by the same code that attaches the authorization
   decorator), a `serverChecks` flag for policy, claim and resource requirements a client cannot evaluate, and new
   `[ClientTag(key, value)]` hints — and attaches it to the route as `HandlerClientMetadata` (an optional options bag
   on `HandlerDispatcher.Map`). The schema export writes `requires`, `tags`, and `readOnly` (an `IQuery` request) per
   method, each omitted when empty. The TypeScript generator emits `rpcMethodInfo`; the contributions package adds
   `canCall(info, capabilities)`. Like `when`, all of it is a UX projection: the pipeline enforces every call.
2. **One ASP.NET projection, nothing more.** A generated `[HttpEndpoint]` route of an `[AllowAnonymous]` handler
   carries `.AllowAnonymous()`, so host-wide authorization conventions leave it open exactly as the pipeline does.
   No `.RequireAuthorization()` is inferred for guarded handlers: which ASP.NET policy guards a route, and whether the
   host runs ASP.NET authorization at all, stays the host's decision (and an inferred requirement would break a host
   without `UseAuthorization()`). `MapElarionEndpoints` maps everything into one prefix-less route group and returns
   it, so the host states that decision once: `app.MapElarionEndpoints(cfg).RequireAuthorization()`.
3. **Rules see the handler.** `AuthorizationContext.HandlerType` (with `GetHandlerAttribute<T>()`) names the concrete
   handler being authorized, carried on `AuthorizationRequirements.HandlerType` so `IAuthorizer`'s signature is
   unchanged; it is `null` on the client-event subscription path. `[AllowAnonymous]` still short-circuits the
   authorizer, so rules never see anonymous handlers.

## Consequences


- `requires` reflects the handler's own declarations only. `IGlobalAuthorizationRule`s, feature gates and business
  rules are invisible to it, so `canCall` may answer `allowed: true, serverChecks: false` for a call the server then
  refuses. The client still handles every refusal.
- Role names reach the client bundle through `requires.roles`. Permissions were already listed in the capabilities
  snapshot; roles were not. An application whose role names are themselves sensitive keeps those checks in a policy,
  which exports only as `serverChecks`.
- A UI gates actions from `rpcMethodInfo` and `canCall`; the per-call-site permission lists and the source-scanning
  lint go away, and a new `[RequirePermission]` reaches the client by regenerating the schema.
- The schema now names every method's required permissions. It ships with the client bundle, so this discloses
  nothing the `capabilities` block did not already list; the generic forbidden message (which keeps a denied caller
  from learning the vocabulary at run time) is unaffected.
- Every query method gains `readOnly: true` and the generated client gains `rpcMethodInfo`, so schemas and generated
  clients change once on upgrade; methods without declarations stay byte-identical.
- An `[AllowAnonymous]` handler's route is open even inside a module group that requires a policy. That matches the
  pipeline, which never authorized it; a host that wants an HTTP-only gate in front of a public operation puts it in
  middleware.
- The manifest's HTTP and RPC entries gain an appended field each; manifests from older builds decode without it
  (no anonymous flag, no client metadata).

## Rejected alternatives

- **Reading the handler type's attributes at schema-export time.** The export runs by reflection anyway, but
  `[ElarionAuthorizationDefaults]` is resolved per module namespace by the generator; re-deriving it at run time
  would be a second implementation of that rule, free to drift from the one that attaches the decorator.
- **Inferring `.RequireAuthorization()` for guarded handlers.** It would move a host policy decision into the
  generator and couple the HTTP transport to ASP.NET authorization middleware being present.
- **`HandlerMetadata` on `AuthorizationContext`.** Stream handlers carry `StreamHandlerMetadata`, a different type;
  the handler type is what a rule needs and is available on both paths.

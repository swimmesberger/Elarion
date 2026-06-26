# ADR-0009: Authorization building blocks

- Status: Accepted
- Date: 2026-06-26
- Related: [ADR-0003](0003-decorator-attachment-predicates.md) (decorator attachment),
  [ADR-0006](0006-incremental-source-generator-conventions.md) (generator conventions),
  [ADR-0007](0007-data-is-platform-module-as-plugin.md) (the data layer is application logic),
  [authorization](../concepts/authorization.mdx).

## Context

Downstream apps (e.g. ImmoCentral) each hand-roll handler authorization: an `AuthorizationDecorator`
reading a `[RequirePermission]` attribute, an `IPermissionChecker` over `HttpContext`, and — when using
ASP.NET Core Identity with snake_case — a hand-written `OnModelCreating` block remapping the Identity
tables. The framework should own these as reusable, domain-neutral building blocks, while keeping the
**base authorization independent of ASP.NET Core Identity** and offering Identity as an optional package.

Two cross-cutting questions shaped the design: should authorization be opt-in or secure-by-default, and
should named "policies" reuse ASP.NET Core's policy engine?

## Decision

1. **Authorization is transport-neutral and lives in the core packages, not ASP.NET.** The attributes,
   the `IAuthorizer` seam, the `IAuthorizationPolicy` named-policy seam, and the `AuthorizationDecorator`
   live in `Elarion.Abstractions`; the default `ClaimsAuthorizer` and registration live in `Elarion` core.
   Nothing in the authorization path depends on ASP.NET Core. A decision is made against
   [`ICurrentUser`](../concepts/handlers.mdx) claims and the **handler request** as the resource, so the
   same `[Require*]` handlers are enforced identically under JSON-RPC, MCP, and HTTP.

   - **Why not bridge to ASP.NET's `IAuthorizationService`?** Its engine is lightweight, but it is an
     `AspNetCore`-named dependency in the core authorization path, and real-world policy handlers routinely
     cast `AuthorizationHandlerContext.Resource` to `HttpContext` — which silently breaks under non-HTTP
     transports. An Elarion-native policy authorizes against the typed request instead. (An ASP.NET-policy
     adapter was prototyped and **dropped**: it leaked the HTTP-resource assumption into a transport-neutral
     surface and is rarely needed; re-expressing a policy as an `IAuthorizationPolicy` is trivial.)
   - **Named policies are declared, not hand-wired.** An `IAuthorizationPolicy` marked
     `[AuthorizationPolicy("name")]` is **auto-registered per module** by a dedicated generator (a 7th
     `ConfigureDefaultServices` category alongside handlers/services/validators/jobs/consumers/module-api),
     mirroring `[Service]`. The name lives on the attribute (the single compile-time source), so a future
     analyzer can validate `[RequirePolicy("name")]` references against the declared set. `ELPOL001` flags
     `[AuthorizationPolicy]` on a non-policy; `ELPOL002` flags a policy outside any module. Manual
     `AddElarionAuthorizationPolicy` (typed or delegate) remains for inline/host registration.

2. **`AppError` gains `Unauthorized` (401)** alongside `Forbidden` (403): an unauthenticated caller fails
   with `Unauthorized`, an authenticated-but-denied caller with `Forbidden`, matching ASP.NET semantics and
   mapped centrally per transport.

3. **The decorator is auto-attached by the generator, supporting opt-in and secure-by-default.** The
   authorization decorator is auto-appended (not listed in a `[DecoratorList]`), so attachment is a
   compile-time presence decision the `HandlerRegistrationGenerator` makes by inspecting the **handler symbol**
   for authorization attributes — an `AppliesTo` predicate (ADR-0003) is a runtime gate that always emits the
   decorator. By default it attaches only to handlers carrying a `Require*`/`[RequirePolicy]` attribute (zero
   cost otherwise). An
   assembly- or `[AppModule]`-scoped `[ElarionAuthorizationDefaults]` (resolved most-specific-wins, like
   `[DefaultPipeline]`) flips to deny-by-default: every in-scope handler requires authorization unless it has
   `[AllowAnonymous]`. The decorator reads requirements through `HandlerMetadata` (never `inner.GetType()`),
   so it is correct as the outermost functional gate. A handler whose response cannot represent failure
   (`IResultFailureFactory<T>`) is reported as `ELAUTH001` rather than silently left unguarded.

4. **ASP.NET Core Identity is an optional package that composes, not inherits.** `Elarion.AspNetCore.Identity`
   wires Identity onto a **plain** `DbContext`: `AddEntityFrameworkStores` needs only the Identity entities
   mapped, so `[GenerateElarionIdentity<TUser, TRole, TKey>]` drives a generator that emits the Identity
   `DbSet`s and applies the model configuration through a neutral `OnEntitiesConfigured` seam on the EF
   `DbContextGenerator`'s `ConfigureEntities`. The host writes a single `ConfigureEntities(modelBuilder)` call
   and no generics; the context never derives from `IdentityDbContext`. `snake_case` is a self-contained
   option on the generated model (no `EFCore.NamingConventions` dependency). This is the headline instance of
   [composition over inheritance](../why-elarion.mdx#composition-over-inheritance).

## Consequences

- Authorization works under every transport with one set of attributes; authentication (Identity, Entra ID,
  any OIDC/JWT) is a separable host concern that only populates `ICurrentUser`.
- `ICurrentUser` gains claim access (`HasClaim`/`GetClaimValues`) as fail-closed default interface methods, so
  existing implementers keep compiling and a stale fake denies rather than fails open.
- Named policies are resolved by string at runtime; a typo'd policy name fails closed (forbidden + a warning),
  not at compile time. Because `[AuthorizationPolicy("name")]` puts the name in compile-time metadata, a future
  analyzer can cross-check `[RequirePolicy("name")]` references against the declared policies and flag typos —
  the attribute exists in part to enable that.
- The EF generator now emits `ConfigureEntities` + the neutral seam for every `[GenerateDbSets]` context, even
  one with no `[EntityConfiguration]` entities (e.g. an Identity-only context), so the host's `ConfigureEntities`
  call always resolves.
- **Replicating EF's Identity model configuration is the accepted "bitter pill."** The rejected alternatives are
  worse: inheriting `IdentityDbContext` (the coupling this design removes), or reflectively invoking
  `IdentityDbContext<…>.OnModelCreating` on a throwaway instance to harvest its config (AOT/trim-hostile hidden
  runtime discovery that depends on EF internals like instance `StoreOptions`). Replication is ~80 lines of
  MIT-licensed, extremely stable config, pinned to the Identity version and locked by a model test; the only
  realistic drift is a new Identity column, which the app's column convention still snake_cases.

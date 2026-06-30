# ADR-0019: Client capability bootstrap — modules, flags/variants, and grants projected to the frontend over OpenFeature

- Status: Accepted
- Date: 2026-06-30
- Related: [ADR-0016](0016-feature-flag-gating.md) (the boolean `IFeatureFlagService` seam), [ADR-0018](0018-variant-service-injection.md)
  (the `IFeatureVariantService` variant accessor), [ADR-0009](0009-authorization-building-blocks.md) (authorization /
  `ICurrentUser`), the [modules](../concepts/modules.mdx) and [transports](../concepts/transports.mdx) concept docs.
  The source-generated permission catalog (separate work) composes with the grants exposed here.

## Context

A frontend wants to hide or adapt UI based on **what the backend actually offers for the current user and
deployment**: which modules are enabled, which feature flags/variants are on, and the user's roles/permissions.
Today none of that is reachable from the client in a first-class way, and all three pieces already exist
server-side:

- **Module enablement** — `Modules:{Name}:Enabled` config + the generated `IConfiguration.IsModuleEnabled(name)`
  (`AppModuleDiscoveryGenerator`). Deployment-scoped, backend-only.
- **Flags / variants** — evaluated server-side through `IFeatureFlagService.IsEnabledAsync` and
  `IFeatureVariantService.GetVariantAsync` against the user's `ElarionEvaluationContext`. Per-user.
- **Roles / permissions** — on `ICurrentUser` (`Roles`, and `GetClaimValues(PermissionClaimType)` with the default
  `"permission"` claim type). Per-user.

The **backend is the source of truth** — it holds the flag-provider configuration, the user's evaluation context,
and the module config. The client must *reflect* that, not re-evaluate independently: a client-side flag provider
would duplicate provider config/secrets, diverge on targeting, and **cannot see module enablement at all**.

OpenFeature's **web SDK** is the natural client abstraction — the static-context paradigm (the context is one
user/session), a stateless provider, synchronous reads, and React bindings. But OpenFeature deliberately exposes
**no "evaluate all flags" API**, so the backend must know *which* names to evaluate — i.e. exposure has to be
explicit. We also want **one** client decision mechanism, not separate APIs for modules vs flags vs permissions on,
say, React.

## Decision

### 1. A single, transport-neutral bootstrap handler

One built-in handler returns the whole client picture for the current user + deployment:

```jsonc
{ "user":     { "id": "u-123", "roles": ["admin"], "permissions": ["billing.write"] },
  "modules":  { "Billing": true, "Experiments": false },
  "flags":    { "new-checkout": true },
  "variants": { "ForecastAlgorithm": "neural" } }
```

Because the handler is **framework-owned**, the host cannot decorate it with attributes (`[Handler]`/`[HttpEndpoint]`
are compile-time on the class). So exposure is **imperative**, host-chosen, via a thin capability extension:
`AddElarionClientCapabilities()` registers it, the host opts it onto the **named bus** (JSON-RPC/MCP) through the
imperative `MapHandler<…>(name, transports)` seam, and onto **REST** through a concrete, framework-authored
`MapElarionClientCapabilities("/session")` (concrete types, so ASP.NET's Request Delegate Generator stays
AOT/trim-safe — a generic HTTP map would not). This is the general gap for exposing any handler whose class the host
doesn't own; it has its own record in [ADR-0020](0020-imperative-handler-transport-mapping.md). The handler composes
existing seams only: `IsModuleEnabled`, `IFeatureFlagService.IsEnabledAsync`, `IFeatureVariantService.GetVariantAsync`,
and `ICurrentUser`.

### 2. Explicit per-module exposure — `[ClientFeatures]`

A module declares the flag/variant names it exposes to the client, on its `[AppModule]` type — where it already
owns its handlers, endpoints, and gating:

```csharp
[AppModule("Billing")]
[ClientFeatures("new-checkout", "dashboard-v2")]   // exposed to the frontend
public static class BillingModule { }
```

`AppModuleDiscoveryGenerator` collects these per-module lists into the client-feature manifest (next to the MCP/RPC
manifests it already builds). The bootstrap evaluates **only those names, only for enabled modules**. Properties
that fall out:

- **Opt-in by enumeration** — nothing reaches the wire unless a module names it, so the "don't leak internal flags"
  concern is answered structurally (no allowlist to maintain, no dump-all).
- **Module-gated** — a disabled module exposes nothing, consistent with how its handlers/endpoints already disappear.
- **Local ownership** — the module that owns the feature owns its client exposure.

**Client-only flags are first-class.** A listed name with *no* `[FeatureGate]`/`[FeatureVariant]` behind it is valid
— a pure UI flag. It is still a real flag in the provider config, so the backend evaluates it with the **same
provider + the user's context** (single source of truth, consistent targeting); the module listing it is the only
thing required. Exposure is thus **decoupled from gate discovery** — the manifest is literally the union of the
`[ClientFeatures]` lists, not a scan of `[FeatureGate]`/`[FeatureVariant]`.

### 3. Authorization as raw grants

The bootstrap returns the user's **roles + permissions** (`ICurrentUser.Roles` and
`GetClaimValues(PermissionClaimType)`); the frontend does its own checks. We do **not** ship a per-operation
"canInvoke" map: resource-scoped (`[RequireResource]`) and request-inspecting (`[RequirePolicy]`) gates cannot be
pre-decided without the actual resource/request, and raw grants cover the common UI case. This composes with the
source-generated permission catalog — the catalog is the *universe* of permissions, the bootstrap is the user's
*subset*.

### 4. One client mechanism — project everything into OpenFeature

The generated `@openfeature/web-sdk` provider is hydrated from the single bootstrap and answers **every** key from
the cached snapshot, via reserved namespaces, so React only ever uses OpenFeature:

| Key | OpenFeature read | Source in the snapshot |
| --- | --- | --- |
| `module.Billing` | `getBooleanValue` | `modules["Billing"]` (deployment-scoped) |
| `permission.billing.write` | `getBooleanValue` | `permissions.includes(...)` (user) |
| `role.admin` | `getBooleanValue` | `roles.includes(...)` (user) |
| `new-checkout` | `getBooleanValue` | `flags[...]` (the `[ClientFeatures]` set) |
| `ForecastAlgorithm` | `getStringValue` / `.variant` | `variants[...]` |

The provider dispatches on the key prefix into the cached snapshot, default otherwise (≈30 lines). **Module
enablement being deployment-scoped, not user-scoped, is fine** under the static-context paradigm: an OpenFeature
flag value is *allowed* not to vary by context. The module value rides the same bootstrap and refreshes on the same
`onContextChange` (re-fetching constant module state on login is negligible) — one fetch, one cache, one mechanism.

To avoid magic strings, the TS generator emits **typed key constants** from the manifest (+ permission catalog) —
`Keys.module.Billing`, `Keys.permission.billingWrite`, … — but they resolve through the one provider.

### 5. Two client outputs

The TypeScript generator emits **(a)** the typed snapshot client (free from the RPC schema, like every other
operation) and **(b)** the OpenFeature provider + typed keys. Teams that don't want OpenFeature can use the snapshot
client directly; teams that do get the standard, vendor-neutral client API + React hooks.

## How a handler chooses its transport (the existing API)

For reference, because the bootstrap relies on it rather than adding anything new. Exposure is declarative, per
handler:

- `[Handler]` / `[Handler(Transports = HandlerTransports.JsonRpc | HandlerTransports.Mcp)]` — the **name-routed**
  transports. `HandlerTransports` is `JsonRpc = 1`, `Mcp = 2`, `All = JsonRpc | Mcp` (the default).
- `[HttpEndpoint("route")]` / `[HttpEndpoint(HttpVerb.X, "route")]` — a **separate** opt-in for REST (route/verb/param
  binding don't fit a flags enum).
- `[McpHandler(ToolName = …)]` — only customizes the MCP tool name.

The host turns each surface on independently: `AddElarionJsonRpc` + `MapElarionJsonRpc`, `AddElarionMcp` +
`MapElarionMcp`, and the generated `MapElarion`/`Map{Module}Http` for `[HttpEndpoint]` handlers. This declarative path
covers handlers **you own**; a handler whose class you do not own (a framework handler like this bootstrap) is
exposed imperatively instead — see [ADR-0020](0020-imperative-handler-transport-mapping.md).

## Consequences

**Positive**

- **One mechanism on the client** — modules, flags, variants, and grants are all OpenFeature keys; React learns one
  API.
- The **backend stays the source of truth**; the client never re-hosts provider config or re-implements targeting.
- **Opt-in exposure is leak-safe by construction**, and **client-only flags need no server handler**.
- Vendor-neutral on **both** ends (OpenFeature on the client mirrors the OpenFeature seam on the server).

**Negative / accepted**

- **Authz-as-flags is a deliberate read-only UX projection, not enforcement.** The handler's `[RequirePermission]`
  is the real gate; a hidden button is not a secured operation. Because the provider is **local** (served from the
  bootstrap, not a real flag backend), no external flag dashboard treats permission checks as flag evaluations, so
  the usual "don't model authz as flags" telemetry worry does not bite — but the enforcement boundary must be stated
  plainly in the docs.
- The snapshot is **per-user and client-cached**; it can be stale between refreshes (mitigated by `onContextChange`
  re-evaluation and an explicit refresh hook).
- **No per-operation `canInvoke`** (raw grants only), by choice — revisit if demand appears.
- Exposed flag names are **not validated** against the provider config (flags live in external config; a typo
  resolves to the provider default, as everywhere else) — a soft diagnostic could be added later.

## Implementation (follow-up — not in this ADR)

- `[ClientFeatures]` attribute in `Elarion.Abstractions`, collected per module into a client-feature manifest by
  `AppModuleDiscoveryGenerator`.
- A built-in bootstrap handler composing `IsModuleEnabled` + `IFeatureFlagService` + `IFeatureVariantService` +
  `ICurrentUser`, returning the `{ user, modules, flags, variants }` contract, exposed via the imperative mapping of
  [ADR-0020](0020-imperative-handler-transport-mapping.md) (`AddElarionClientCapabilities` + the bus `MapHandler` seam
  + a concrete `MapElarionClientCapabilities` for REST).
- TypeScript generator: the typed snapshot client, the `@openfeature/web-sdk` provider, and the typed key constants.
- Docs: a `concepts/client-capabilities.mdx`, and a short "choosing transport exposure" section added to
  `transports.mdx` (today that decision lives only in the attribute XML docs).

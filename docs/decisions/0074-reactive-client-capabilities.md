# ADR-0074: Reactive client capabilities — stores over values, with the registry left pure

- Status: Accepted
- Date: 2026-08-15
- Related: [ADR-0030](0030-client-capability-bootstrap.md) (the snapshot itself),
  [ADR-0032](0032-frontend-contribution-model.md) (the contribution kernel and the `when` vocabulary), and the
  [client-capabilities](../concepts/client-capabilities.mdx) concept page.

## Context

The client-capability snapshot (ADR-0030) is fetched once at boot and handed to the generated
`createSessionCapabilities(...)`, which produces an immutable reader. The contribution kernel (ADR-0032)
resolves manifests against that reader **eagerly and purely**: same inputs, same registry, so a server render
and the client hydration produce identical trees.

That is exactly right for boot and wrong for everything after it. Capabilities change *during* a session — a
login, a tenant switch, a subscription upgrade, an admin granting a role, any mutation whose response changes
what the user may see. The documented remedy was "build a new registry and swap it", which every application
then had to implement itself:

- The React binding took a plain registry, so a refresh meant threading new state through the composition root.
- The TanStack Router adapter reads `context.caps` per navigation, but the router context is created once —
  updating it means rebuilding the router.
- Only the Angular binding had a reactive story, because it happened to hold a `Signal<ContributionRegistry>`.

Three bindings, three different answers, and the app owning the wiring in all of them. Meanwhile the natural
fix — making `createContributionRegistry` itself stateful and subscribable — would destroy the purity that
makes SSR/hydration parity provable.

## Decision

**Add a store layer above the pure functions; change neither the pure functions nor the snapshot contract.**

- **`CapabilitySource`** — `CapabilityReader` plus `subscribe(listener): () => void`. A structural interface,
  like `CapabilityReader` before it, so the generated session client satisfies it without either package
  referencing the other.
- **`createCapabilityStore(initial)`** returns a `CapabilityStore`: a `CapabilitySource` that *delegates every
  read to the reader it currently holds*, plus `set(next)` (a no-op on the same reference). Because the store
  **is** a `CapabilityReader`, it is placed once into long-lived contexts — a TanStack router context, a DI
  provider, a closure — and every later read sees the current snapshot. `redirectUnless` needed no change at
  all; that is the test of the design.
- **`createContributionRegistryStore(manifests, source)`** keeps a resolved registry in step with a source.
  Rebuilds go through the unchanged `createContributionRegistry`, so `when` filtering, deterministic ordering,
  and the duplicate-id check cannot drift between the "resolve once" and "resolve often" paths. Rebuilding is
  **lazy**: a notification invalidates and notifies, and the next `current` read resolves. That keeps `current`
  referentially stable between changes (`useSyncExternalStore`'s requirement), avoids resolving snapshots
  nobody reads, and keeps a manifest data bug throwing at the read that surfaces it instead of inside a
  notifier.
- **Bindings accept either form.** `ContributionProvider` takes a registry *or* a store and consumes the store
  through `useSyncExternalStore`; the React context still carries a plain `ContributionRegistry`, so
  `useContributions`/`ExtensionSlot` are untouched. `provideContributions` gains a store overload alongside its
  existing `Signal` one. Reactivity is confined to one component and one provider function.
- **The generated client emits `createSessionCapabilitiesStore(fetchSnapshot)`** — `current`, `refresh()`,
  `subscribe()`, and the reader methods with **fail-closed** defaults (modules/permissions/roles/flags answer
  `false`, variants and sections `undefined`) until the first refresh completes, matching the fail-closed
  posture the snapshot already takes. The transport is **injected as a callback** rather than built in: that
  is what keeps the emitted module import-free, which is a hard constraint on generated session code.
  Contributor-fed sections (the `sections` bag) ride along automatically, since they are part of the same
  snapshot.

## Alternatives considered

- **Make `createContributionRegistry` stateful/subscribable.** Rejected — it is the one function whose purity
  makes render/hydration parity provable, and it is used directly in tests and SSR paths.
- **Per-framework reactivity only** (a React hook, an Angular signal helper, a router plugin). Rejected — it
  triples the surface, and each binding would define "when did capabilities change" slightly differently. The
  kernel now owns the semantics; bindings only adapt them.
- **Eager rebuild inside the source notification.** Rejected — it resolves for snapshots nobody reads and moves
  a duplicate-id `Error` into a notifier callback, where the throw has no meaningful call site.
- **Deep-comparing snapshots in `set`.** Rejected — a reader is opaque (it is an interface, often a closure);
  reference identity is the only honest signal, and callers control it.
- **A `session.changed` client-event topic** pushing invalidations from the server. **Deferred, deliberately.**
  Client events are at-most-once re-query hints (a fine fit), but the topic needs per-user targeting, an
  authorization story for who may subscribe, and a fan-out policy — a security-sensitive design that should not
  ride along with a client-side refactor. `refresh()` after a mutation covers the actual demand today, and a
  push topic would call the same `refresh()` when it lands.

## Consequences

- An application refreshes capabilities by calling `store.refresh()` (or `capabilityStore.set(...)`) — one call,
  no context rebuild, no re-mount, no per-framework wiring.
- Existing code is unaffected: every previous call shape still compiles and behaves identically, and an app that
  never needs a refresh never touches a store.
- The contributions package and the generated client remain mutually independent; `CapabilitySource` joins
  `CapabilityReader` as a structurally-shared contract, pinned from the contributions side by a compile-only
  typing assertion.
- Capability snapshots stay **UX projections**. A store makes the UI converge faster; it does not make it an
  enforcement boundary. The handler's `[Require*]`/`[FeatureGate]` remains the authority.

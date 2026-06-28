# ADR-0011: Runtime-changeable settings subsystem

- Status: Accepted
- Date: 2026-06-27
- Related: [ADR-0004](0004-handler-result-caching.md) (current-user scoping pattern),
  [ADR-0007](0007-data-is-platform-module-as-plugin.md) (the data layer is application logic),
  [ADR-0009](0009-authorization-building-blocks.md) (`ICurrentUser`, fail-closed posture),
  [settings](../concepts/settings.mdx), [variable substitution](../concepts/variable-substitution.mdx).

> **Update (2026-06-27):** all four phases shipped — the foundation + native accessor, the EF Core store,
> the `IConfiguration`/`IOptionsMonitor` adapter, and the scheduler integration. The scheduler tie-in went
> beyond next-fire pickup: variable substitution was extracted into a general, reusable building block
> (`IVariableSource`/`VariableSubstitution`, decoupled from scheduling — see the variable substitution
> concept doc), the scheduler now resolves through that seam, and an observable source
> (`IObservableVariableSource`) drives **live reschedule** of affected recurring jobs on change. Remaining:
> cross-instance change sources.

## Context

Downstream apps repeatedly ask for a **runtime-changeable settings/configuration API**: key/value,
hierarchical like `IConfiguration` (with the hierarchy possibly *virtual*, like environment variables),
with **both global/system and per-user** settings, in-process **change watching**, and a database backing.
Elarion has no settings/options abstraction today — config is read once at startup, and there is no
`IOptionsMonitor`/`IChangeToken` usage anywhere in the repo.

Two forces shape the design. First, **AOT/trim**: `Directory.Build.props` sets
`JsonSerializerIsReflectionEnabledByDefault=false`, so a reflection-based `ConfigurationBinder` /
`IOptionsMonitor` read path would fail at runtime. Second, the request is explicitly for **good
abstractions on both sides, swappable independently**: the store (the "sink", including listening) should
be replaceable — database is one shipped implementation, but Redis or others must fit — and the consuming
side should be a choice (an `IConfiguration` adapter, `IOptionsMonitor`, or a native accessor), not a
single hard-wired API.

## Decision

1. **Two swappable sides behind interfaces.** The **sink** is `ISettingsStore` (read/write/enumerate) plus
   a separate listen seam, `ISettingsChangeSource` (hands out `IChangeToken`s) and `ISettingsChangePublisher`
   (stores signal it after a write). The **consuming** side is the native `ISettingsManager` accessor.
   Either side can be replaced without touching the other: a database or Redis store is a new
   `ISettingsStore`; an `IConfiguration` provider is a new consumer over the same store.

2. **The store substrate is flat string key → string value, with virtual hierarchical keys.** Keys use the
   `:` separator convention; the store treats them as opaque. This maps 1:1 to a future `IConfiguration`
   provider and lets the hierarchy be virtual (like env vars). Typed access is layered on top by the
   accessor, which serializes a value to a JSON string via a source-generated `JsonTypeInfo<T>`.

3. **Scopes are an open `(Kind, Owner)` value, not a closed enum.** `Global` and `User(ownerId)` ship;
   `tenant`/`environment`/custom scopes can be added with no contract or schema change. The per-user scope
   resolves the owner from `ICurrentUser` and **fails closed** when unauthenticated, mirroring current-user
   handler caching (ADR-0004). The store never sees an unresolved current-user placeholder.

4. **Change notification is `IChangeToken`-based.** This is the standard .NET shape that bridges cleanly to
   `IConfiguration` reload tokens and `IOptionsMonitor`, and is implementable by an in-process source, a
   Postgres `LISTEN/NOTIFY` source, and a Redis source alike. The **shipped default notifies only within the
   process** (single instance); cross-instance propagation is a swap-in change source, not baked into the
   core.

5. **Native-first consuming side; `IConfiguration`/`IOptionsMonitor` as adapters.** Because reflection JSON
   is disabled, the primary read path is the native accessor binding through source-gen `JsonTypeInfo<T>`
   (AOT-clean). An `IConfiguration` *provider* adapter is still AOT-safe to author (it emits flat string
   key/value; reflection is the consumer's opt-in via `.Get<T>()`) and ships as a separate package later.

6. **Phased delivery.** Phase 1 (now): the contracts, the in-process store + change source, and the native
   accessor (`Elarion.Settings`). Fast-follows over the same contracts: a database store
   (`Elarion.Settings.EntityFrameworkCore`, ported from the outbox store seam), an `IConfiguration`/
   `IOptionsMonitor` adapter (`Elarion.Settings.Configuration`), and cross-instance change sources. The
   `IConfiguration` adapter also makes the scheduler's per-occurrence `${...}` variable substitution
   runtime-changeable with no scheduler change (it already re-resolves `IConfiguration` on every reschedule).

### Why not make a custom `IConfigurationProvider` the core?

It is the most familiar .NET shape, but it cannot be the foundation: reflection binding is disabled
repo-wide, and app-wide `IConfiguration` has no place for **per-user** settings. So `IConfiguration` is one
*consumer* of the store (global scope), not the store itself.

## Consequences

- A single `Elarion.Settings` package is immediately usable (contracts + in-process backend + native
  accessor) with one reference, mirroring how `Elarion` core bundles the in-memory domain bus. The
  contracts vs. backend split is by namespace; providers replace the `ISettingsStore` registration.
- The default backend is single-instance: a setting changed on one node is not seen by others until a
  cross-instance change source is added. The native read path and in-process watch are correct within a
  process; this is documented and is the deliberate v1 boundary.
- Optimistic concurrency is surfaced as data (`SettingWriteResult.ConcurrencyConflict`), not exceptions,
  keeping the store seam exception-free across providers (as with the EF Core outbox store).
- Typed access requires callers to pass a source-generated `JsonTypeInfo<T>`. This is slightly more verbose
  than reflection binding but is the price of AOT-safety and is consistent with the rest of the framework.

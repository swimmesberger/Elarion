// The contribution kernel of the Elarion frontend module system (ADR-0032): typed extension-point tokens,
// declarative module manifests, and a snapshot-pure resolution step. Framework-free — the React bindings
// live in the `/react` sub-export, and porting them to another view framework is a page of code.
//
// Semantics in one paragraph: a module ships a manifest (plain data plus lazy component references — never
// import-time `register()` calls, which are import-order-dependent, side-effectful, and invisible to
// tree-shaking); an extension point is a typed token whose export from a module's public entry is the
// frontend [ModuleContract]; resolution filters every contribution by its `when` clause (ANDed with the
// owning manifest's module-level `when`) against the capability snapshot and sorts deterministically, so
// the same input always renders the same UI on server and client. `when` is a UX projection, never
// security — the backend's [RequirePermission]/[FeatureGate] remain the enforcement on every call.

/**
 * The capability vocabulary a contribution kit is typed against — supplied by the application from the
 * generated literal unions in `session-client.ts` (ModuleName/PermissionName/FlagName/RoleName).
 */
export interface Vocabulary {
  module: string
  permission: string
  flag: string
  role: string
}

/** Keeps literal-union autocomplete while accepting out-of-vocabulary names (the generated accessors' shape). */
type Hint<T extends string> = T | (string & {})

/**
 * The declarative visibility condition of a contribution — the frontend mirror of
 * `[RequirePermission]`/`[RequireRole]`/`[FeatureGate]`: every present field must hold (AND).
 */
export interface WhenClause<V extends Vocabulary = Vocabulary> {
  readonly module?: Hint<V["module"]>
  readonly permission?: Hint<V["permission"]>
  readonly flag?: Hint<V["flag"]>
  readonly role?: Hint<V["role"]>
}

/**
 * The snapshot surface resolution reads — structurally satisfied by the `SessionCapabilities` class the
 * Elarion client generator emits, so this package never depends on generated code.
 */
export interface CapabilityReader {
  isModuleEnabled(name: string): boolean
  hasPermission(permission: string): boolean
  hasRole(role: string): boolean
  isFlagEnabled(name: string): boolean
}

export function evaluateWhen(when: WhenClause | undefined, capabilities: CapabilityReader): boolean {
  if (when === undefined) return true
  if (when.module !== undefined && !capabilities.isModuleEnabled(when.module)) return false
  if (when.permission !== undefined && !capabilities.hasPermission(when.permission)) return false
  if (when.role !== undefined && !capabilities.hasRole(when.role)) return false
  if (when.flag !== undefined && !capabilities.isFlagEnabled(when.flag)) return false
  return true
}

declare const CONTRIBUTION: unique symbol
declare const SLOT_CONTEXT: unique symbol

/**
 * A typed extension-point token. Exporting one from a module's public entry is the frontend
 * [ModuleContract]: contributors import the token — an explicit, compile-checked, correctly-directed
 * dependency — without pulling the owning module's components into their chunk, because the token is data.
 *
 * `TItem` is the payload the point accepts; `TContext` documents what a slot site supplies to the payload's
 * callbacks or components (`void` for context-free slots like a sidebar).
 */
export interface ExtensionPoint<TItem, TContext = void> {
  /** Globally unique, stable id — convention `{owner}.{point}`, e.g. `"clients.rowActions"`. */
  readonly id: string
  /** Phantom type markers only — nothing is ever stored under these keys. */
  readonly [CONTRIBUTION]?: TItem
  readonly [SLOT_CONTEXT]?: TContext
}

export function defineExtensionPoint<TItem, TContext = void>(id: string): ExtensionPoint<TItem, TContext> {
  return { id }
}

/** A single contribution: the point's payload plus identity, ordering, and visibility. */
export type Contribution<TItem, V extends Vocabulary = Vocabulary> = TItem & {
  /** Unique within the point across all modules; part of the deterministic sort key. */
  readonly id: string
  /** Ascending sort rank (default 0); ties break by id, then by contributing module name. */
  readonly order?: number
  /** Visibility condition, ANDed with the contributing module's manifest-level `when`. */
  readonly when?: WhenClause<V>
}

/** One manifest entry: a point token and the items contributed to it. Built with {@link contribute}. */
export interface ContributionBatch<V extends Vocabulary = Vocabulary> {
  readonly point: ExtensionPoint<unknown, unknown>
  readonly items: ReadonlyArray<Contribution<unknown, V>>
}

/** Types a batch against its point: the items must match the point's declared payload. */
export function contribute<TItem, TContext, V extends Vocabulary = Vocabulary>(
  point: ExtensionPoint<TItem, TContext>,
  items: ReadonlyArray<Contribution<TItem, V>>
): ContributionBatch<V> {
  return { point, items }
}

/**
 * A frontend module's manifest: plain data plus lazy references — the whole surface the composition root
 * sees. Manifests are inspectable without executing module code and testable as plain arrays (the reason
 * VS Code moved contributions into static `package.json` data).
 */
export interface ModuleManifest<V extends Vocabulary = Vocabulary> {
  /** The module's name — its backend [AppModule] name when it mirrors one, any unique name when UI-only. */
  readonly name: Hint<V["module"]>
  /**
   * Module-level visibility, ANDed into every contribution. A backend-paired module gates itself here once
   * — `when: { module: Modules.X }` — so disabling the backend module removes the whole frontend module;
   * a UI-only module omits it.
   */
  readonly when?: WhenClause<V>
  readonly contributes: ReadonlyArray<ContributionBatch<V>>
}

export function defineModule<V extends Vocabulary = Vocabulary>(manifest: ModuleManifest<V>): ModuleManifest<V> {
  return manifest
}

/** The resolved, filtered, deterministically ordered view of every manifest against one snapshot. */
export interface ContributionRegistry {
  get<TItem, TContext>(point: ExtensionPoint<TItem, TContext>): ReadonlyArray<Contribution<TItem>>
}

// Code-unit string comparison — localeCompare would make the resolved order environment-dependent.
function compareStrings(a: string, b: string): number {
  if (a < b) return -1
  if (a > b) return 1
  return 0
}

const EMPTY: ReadonlyArray<Contribution<unknown>> = []

/**
 * Resolves manifests against a capability snapshot. Resolution is eager and pure — the same
 * (manifests, capabilities) input yields the same registry, so a server render and the client hydration
 * produce identical trees. Refreshing the snapshot (login, context change) means building a new registry,
 * the same contract as the generated session provider.
 */
export function createContributionRegistry(
  manifests: ReadonlyArray<ModuleManifest>,
  capabilities: CapabilityReader
): ContributionRegistry {
  const byPoint = new Map<string, Array<{ item: Contribution<unknown>; module: string }>>()
  for (const manifest of manifests) {
    if (!evaluateWhen(manifest.when, capabilities)) continue
    for (const batch of manifest.contributes) {
      for (const item of batch.items) {
        if (!evaluateWhen(item.when, capabilities)) continue
        const entries = byPoint.get(batch.point.id)
        if (entries === undefined) {
          byPoint.set(batch.point.id, [{ item, module: manifest.name }])
        } else {
          entries.push({ item, module: manifest.name })
        }
      }
    }
  }

  const resolved = new Map<string, ReadonlyArray<Contribution<unknown>>>()
  for (const [pointId, entries] of byPoint) {
    entries.sort(
      (a, b) =>
        (a.item.order ?? 0) - (b.item.order ?? 0) ||
        compareStrings(a.item.id, b.item.id) ||
        compareStrings(a.module, b.module)
    )
    resolved.set(
      pointId,
      entries.map((entry) => entry.item)
    )
  }

  return {
    get<TItem, TContext>(point: ExtensionPoint<TItem, TContext>) {
      return (resolved.get(point.id) ?? EMPTY) as ReadonlyArray<Contribution<TItem>>
    },
  }
}

/** The vocabulary-bound facade of the kernel — created once by the application, imported by every module. */
export interface ContributionKit<V extends Vocabulary> {
  defineModule(manifest: ModuleManifest<V>): ModuleManifest<V>
  defineExtensionPoint<TItem, TContext = void>(id: string): ExtensionPoint<TItem, TContext>
  contribute<TItem, TContext>(
    point: ExtensionPoint<TItem, TContext>,
    items: ReadonlyArray<Contribution<TItem, V>>
  ): ContributionBatch<V>
}

/**
 * Binds the kernel to an application's capability vocabulary (the generated literal unions), so every
 * `when` clause is compile-checked against the same catalog the backend enforces — a typo'd permission is
 * a type error, not a silently hidden item. Purely a typing layer with zero runtime cost.
 */
export function createContributionKit<V extends Vocabulary>(): ContributionKit<V> {
  return { defineModule, defineExtensionPoint, contribute }
}

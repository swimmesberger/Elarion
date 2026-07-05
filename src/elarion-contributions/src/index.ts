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
 * generated literal unions in `session-client.ts` (ModuleName/PermissionName/FlagName/RoleName). Declare
 * only the axes the application actually has: an omitted (or `never`) axis makes every `when` use of it
 * a compile error, so a no-auth app binding `{ module: ModuleName }` gets a real error on a stray
 * permission/flag/role clause instead of a silently hidden item.
 */
export interface Vocabulary {
  module?: string
  permission?: string
  flag?: string
  role?: string
}

/**
 * Keeps literal-union autocomplete while accepting out-of-vocabulary names. Used only where a name is an
 * *identity* (a UI-only module's `name`), never where it is a capability lookup — a lookup outside the
 * vocabulary is exactly the drift `when` typing exists to catch.
 */
type Hint<T extends string> = T | (string & {})

/**
 * One `when`-clause axis: exactly the vocabulary's names. `Extract` keeps the unbound default permissive
 * (`Vocabulary`'s optional `string` axes stay plain `string`) while an omitted or `never` axis reduces to
 * `never` — unusable, by design.
 */
type Axis<V extends Vocabulary, K extends keyof Vocabulary> = Extract<V[K], string>

/**
 * The declarative visibility condition of a contribution — the frontend mirror of
 * `[RequirePermission]`/`[RequireRole]`/`[FeatureGate]`: every present field must hold (AND).
 *
 * Axes are checked strictly against the vocabulary: a typo'd or out-of-vocabulary name is a compile
 * error, not a silently hidden item (the evaluator fails closed, so a wrong name would never surface at
 * runtime either). Deliberately stricter than the generated snapshot accessors, which accept
 * `Name | (string & {})`.
 */
export interface WhenClause<V extends Vocabulary = Vocabulary> {
  readonly module?: Axis<V, "module">
  readonly permission?: Axis<V, "permission">
  readonly flag?: Axis<V, "flag">
  readonly role?: Axis<V, "role">
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

/** One axis of {@link createStaticCapabilities}: everything, an allow-list, or an explicit on/off map. */
export type StaticCapabilitySet = "all" | ReadonlyArray<string> | Readonly<Record<string, boolean>>

/**
 * A fixed {@link CapabilityReader} for applications without a session snapshot — self-hosted/no-auth
 * deployments, tests, stories. Modules, permissions, and roles default to `"all"` (a no-auth app shows
 * everything it ships); flags default to none, because a flag-gated contribution should stay hidden until
 * the flag system that owns it exists (`when` stays fail-closed).
 *
 * This is a static snapshot by design, resolved once at composition. When the backend ships the
 * `elarion.session` operation, swap it for the generated `SessionCapabilities` — same structural
 * interface, nothing else changes.
 *
 * @example
 * ```ts
 * // Everything on (e.g. behind an authenticating reverse proxy):
 * const capabilities = createStaticCapabilities()
 * // Env-driven module toggles; permissions/roles open, flags off:
 * const capabilities = createStaticCapabilities({
 *   modules: { core: true, "ai-agent": import.meta.env.VITE_MODULE_AI_AGENT_ENABLED !== "false" },
 * })
 * ```
 */
export function createStaticCapabilities(
  init: {
    readonly modules?: StaticCapabilitySet
    readonly permissions?: StaticCapabilitySet
    readonly roles?: StaticCapabilitySet
    readonly flags?: StaticCapabilitySet
  } = {}
): CapabilityReader {
  return {
    isModuleEnabled: toPredicate(init.modules, true),
    hasPermission: toPredicate(init.permissions, true),
    hasRole: toPredicate(init.roles, true),
    isFlagEnabled: toPredicate(init.flags, false),
  }
}

function toPredicate(
  set: StaticCapabilitySet | undefined,
  enabledByDefault: boolean
): (name: string) => boolean {
  if (set === undefined) return () => enabledByDefault
  if (set === "all") return () => true
  if (Array.isArray(set)) {
    const names = new Set<string>(set)
    return (name) => names.has(name)
  }
  // Array.isArray does not remove ReadonlyArray from the negated union (TS#17002), hence the assertion.
  const map = set as Readonly<Record<string, boolean>>
  return (name) => map[name] === true
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

/** The payload type an extension point accepts — `ItemOf<typeof sidebarItems>`. */
export type ItemOf<P extends ExtensionPoint<unknown, unknown>> =
  P extends ExtensionPoint<infer TItem, infer _TContext> ? TItem : never

/**
 * The slot context an extension point declares — `ContextOf<typeof stackDetailTabs>`. Reference it in the
 * point's payload signatures (`component: (context: ContextOf<typeof stackDetailTabs>) => ReactNode`) and
 * receive it from the slot (`<ExtensionSlot context={…}>` in the React bindings), so the payload, the
 * point, and the slot site can never drift apart.
 */
export type ContextOf<P extends ExtensionPoint<unknown, unknown>> =
  P extends ExtensionPoint<infer _TItem, infer TContext> ? TContext : never

/** A single contribution: the point's payload plus identity, ordering, and visibility. */
export type Contribution<TItem, V extends Vocabulary = Vocabulary> = TItem & {
  /**
   * Unique within the point among co-visible contributions (enforced when the registry resolves — ids
   * double as render keys). Prefix with the contributing module's name, e.g. `"invoicing.create-invoice"`,
   * to stay collision-free across modules.
   */
  readonly id: string
  /** Ascending sort rank (default 0); ties break by id. */
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
  readonly name: Hint<Axis<V, "module">>
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
 *
 * Resolution validates that no two co-visible contributions to one point share an id (ids double as
 * render keys and as the deterministic tiebreak) and throws on a collision — manifests are static data,
 * so a collision is a data bug that fails fast here rather than corrupting keyed rendering downstream.
 */
export function createContributionRegistry<V extends Vocabulary = Vocabulary>(
  manifests: ReadonlyArray<ModuleManifest<V>>,
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
    const contributors = new Map<string, string>()
    for (const entry of entries) {
      const previous = contributors.get(entry.item.id)
      if (previous !== undefined) {
        throw new Error(
          `Duplicate contribution id "${entry.item.id}" on extension point "${pointId}" ` +
            `(contributed by module "${previous}" and module "${entry.module}"). Contribution ids must ` +
            `be unique within a point — prefix with the module name, e.g. "${entry.module}.${entry.item.id}".`
        )
      }
      contributors.set(entry.item.id, entry.module)
    }
    entries.sort(
      (a, b) => (a.item.order ?? 0) - (b.item.order ?? 0) || compareStrings(a.item.id, b.item.id)
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

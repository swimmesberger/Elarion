import type { RpcSchemaCapabilities } from './schema.js'

export interface GenerateSessionClientSourceOptions {
  generatedBy: string
  sourceLabel: string
  /** The bus/REST operation name the session snapshot is served under (default `elarion.session`). */
  operationName: string
  /** The schema's capability vocabulary (ADR-0032); absent on older schemas — types then fall back to `string`. */
  capabilities?: RpcSchemaCapabilities
}

const q = (value: string): string => JSON.stringify(value)

/**
 * Emits the typed vocabulary section: `Modules`/`Flags`/`Permissions`/`Roles` const objects (only those with
 * entries) plus the `ModuleName`/`FlagName`/`PermissionName`/`RoleName` aliases the accessors reference. With no
 * vocabulary every alias is `string`, so the emitted accessors keep their pre-vocabulary signatures.
 */
function vocabularySection(capabilities: RpcSchemaCapabilities | undefined): string {
  const moduleNames = Object.keys(capabilities?.modules ?? {}).sort()
  const flagNames = [
    ...new Set(Object.values(capabilities?.modules ?? {}).flatMap((m) => m.features ?? [])),
  ].sort()
  const permissions = [...(capabilities?.permissions ?? [])].sort((a, b) =>
    a.permission < b.permission ? -1 : a.permission > b.permission ? 1 : 0
  )
  const roles = [...(capabilities?.roles ?? [])].sort()

  const lines: string[] = []

  if (moduleNames.length > 0) {
    lines.push('/** Module names declared by the backend (enabled at schema-export time). */')
    lines.push('export const Modules = {')
    for (const name of moduleNames) lines.push(`  ${q(name)}: ${q(name)},`)
    lines.push('} as const')
    lines.push('export type ModuleName = (typeof Modules)[keyof typeof Modules]')
  } else {
    lines.push('/** No module vocabulary in the schema — any string is accepted. */')
    lines.push('export type ModuleName = string')
  }
  lines.push('')

  if (flagNames.length > 0) {
    lines.push('/** Flag/variant names the backend exposes to the client (the union of every [ClientFeatures] list). */')
    lines.push('export const Flags = {')
    for (const name of flagNames) lines.push(`  ${q(name)}: ${q(name)},`)
    lines.push('} as const')
    lines.push('export type FlagName = (typeof Flags)[keyof typeof Flags]')
  } else {
    lines.push('/** No flag vocabulary in the schema — any string is accepted. */')
    lines.push('export type FlagName = string')
  }
  lines.push('')

  if (permissions.length > 0) {
    const byResource = new Map<string, Array<{ verb: string; permission: string }>>()
    for (const entry of permissions) {
      const list = byResource.get(entry.resource) ?? []
      list.push({ verb: entry.verb, permission: entry.permission })
      byResource.set(entry.resource, list)
    }
    lines.push('/** The declared permission catalog, nested by resource → verb (values are the composed claim strings). */')
    lines.push('export const Permissions = {')
    for (const [resource, entries] of [...byResource.entries()].sort(([a], [b]) => (a < b ? -1 : 1))) {
      lines.push(`  ${q(resource)}: {`)
      for (const { verb, permission } of entries) lines.push(`    ${q(verb)}: ${q(permission)},`)
      lines.push('  },')
    }
    lines.push('} as const')
    lines.push(
      `export type PermissionName = ${permissions.map((p) => q(p.permission)).join(' | ')}`
    )
  } else {
    lines.push('/** No permission vocabulary in the schema — any string is accepted. */')
    lines.push('export type PermissionName = string')
  }
  lines.push('')

  if (roles.length > 0) {
    lines.push('/** Role names declared by the backend ([RequireRole] across enabled modules). */')
    lines.push('export const Roles = {')
    for (const name of roles) lines.push(`  ${q(name)}: ${q(name)},`)
    lines.push('} as const')
    lines.push('export type RoleName = (typeof Roles)[keyof typeof Roles]')
  } else {
    lines.push('/** No role vocabulary in the schema — any string is accepted. */')
    lines.push('export type RoleName = string')
  }

  return lines.join('\n')
}

/**
 * Emits a self-contained client for the Elarion client-capability snapshot (ADR-0030): a typed `ClientSnapshot`,
 * synchronous capability accessors, and an `@openfeature/web-sdk`-shaped provider that answers every key from the
 * cached snapshot via the reserved `module.` / `permission.` / `role.` namespaces (a bare key is a flag/variant).
 * When the schema carries the ADR-0032 capability vocabulary, the module/flag/permission/role names become typed
 * constants and literal unions, so a typo in a capability check is a compile error; accessor parameters keep an
 * escape hatch (`| (string & {})`) for names outside the exported vocabulary.
 *
 * The module has **no imports** — it neither depends on the RPC schema's generated dictionary typing nor pins
 * `@openfeature/web-sdk`, so it type-checks and runs in any browser or Node target. The consumer fetches the
 * snapshot through the generated RPC client (`rpc.{operation}(...)`) and hands it to the factory.
 */
export function generateSessionClientSource(options: GenerateSessionClientSourceOptions): string {
  const { generatedBy, sourceLabel, operationName, capabilities } = options
  return `// Auto-generated by ${generatedBy} — DO NOT EDIT
// Source: ${sourceLabel}
//
// Client-capability snapshot (operation: ${JSON.stringify(operationName)}). Fetch it with the generated RPC client
// and pass it to createSessionCapabilities / createElarionOpenFeatureProvider.

/** The current user's identity and raw grants, as projected into the snapshot. */
export interface ClientSnapshotUser {
  readonly id: string
  readonly email?: string | null
  readonly isAuthenticated: boolean
  readonly roles: readonly string[]
  readonly permissions: readonly string[]
}

/** The client-capability snapshot returned by the ${JSON.stringify(operationName)} operation. */
export interface ClientSnapshot {
  readonly user: ClientSnapshotUser
  readonly modules: Readonly<Record<string, boolean>>
  readonly flags: Readonly<Record<string, boolean>>
  readonly variants: Readonly<Record<string, string>>
}

${vocabularySection(capabilities)}

/** The reserved OpenFeature key namespaces and their typed builders. */
export const Keys = {
  module: (name: ModuleName | (string & {})): string => \`module.\${name}\`,
  permission: (permission: PermissionName | (string & {})): string => \`permission.\${permission}\`,
  role: (role: RoleName | (string & {})): string => \`role.\${role}\`,
} as const

/** Synchronous, typed accessors over a cached snapshot. */
export class SessionCapabilities {
  // An explicit field, not a constructor parameter property — the emitted code must stay valid under
  // 'erasableSyntaxOnly' (parameter properties are non-erasable TypeScript).
  private readonly snapshot: ClientSnapshot

  constructor(snapshot: ClientSnapshot) {
    this.snapshot = snapshot
  }

  get user(): ClientSnapshotUser {
    return this.snapshot.user
  }

  isModuleEnabled(name: ModuleName | (string & {})): boolean {
    return this.snapshot.modules[name] ?? false
  }

  hasRole(role: RoleName | (string & {})): boolean {
    return this.snapshot.user.roles.includes(role)
  }

  hasPermission(permission: PermissionName | (string & {})): boolean {
    return this.snapshot.user.permissions.includes(permission)
  }

  isFlagEnabled(name: FlagName | (string & {})): boolean {
    return this.snapshot.flags[name] ?? false
  }

  getVariant(name: FlagName | (string & {})): string | undefined {
    return this.snapshot.variants[name]
  }

  /** Resolves a reserved-namespace boolean key (the OpenFeature boolean read). */
  resolveBoolean(key: string): boolean {
    if (key.startsWith('module.')) return this.isModuleEnabled(key.slice('module.'.length))
    if (key.startsWith('permission.')) return this.hasPermission(key.slice('permission.'.length))
    if (key.startsWith('role.')) return this.hasRole(key.slice('role.'.length))
    return this.isFlagEnabled(key)
  }
}

export function createSessionCapabilities(snapshot: ClientSnapshot): SessionCapabilities {
  return new SessionCapabilities(snapshot)
}

/** A single flag resolution result (structurally compatible with OpenFeature's ResolutionDetails). */
export interface ResolutionDetails<T> {
  readonly value: T
  readonly variant?: string
  readonly reason: string
}

/**
 * The subset of the \`@openfeature/web-sdk\` Provider surface this snapshot provider implements. Register it with
 * \`OpenFeature.setProvider(provider as unknown as Provider)\` — the static-context (web) paradigm reads
 * synchronously, so module enablement (deployment-scoped, context-invariant) rides the same snapshot.
 */
export interface ElarionWebProvider {
  readonly metadata: { readonly name: string }
  readonly runsOn: 'client'
  resolveBooleanEvaluation(flagKey: string, defaultValue: boolean): ResolutionDetails<boolean>
  resolveStringEvaluation(flagKey: string, defaultValue: string): ResolutionDetails<string>
  resolveNumberEvaluation(flagKey: string, defaultValue: number): ResolutionDetails<number>
  resolveObjectEvaluation<T>(flagKey: string, defaultValue: T): ResolutionDetails<T>
}

const STATIC = 'STATIC'
const DEFAULT = 'DEFAULT'

/**
 * Builds an OpenFeature web-SDK provider hydrated from one session snapshot. Every key resolves from the cache:
 * \`module.{name}\` / \`permission.{p}\` / \`role.{r}\` are booleans, a bare key is a flag (boolean) or a variant
 * (string), and anything else falls back to the default. No network calls — refresh by building a new provider
 * from a freshly fetched snapshot (e.g. on \`onContextChange\`).
 */
export function createElarionOpenFeatureProvider(snapshot: ClientSnapshot): ElarionWebProvider {
  const capabilities = new SessionCapabilities(snapshot)
  return {
    metadata: { name: 'elarion-session' },
    runsOn: 'client',
    resolveBooleanEvaluation(flagKey, defaultValue) {
      if (
        flagKey.startsWith('module.') ||
        flagKey.startsWith('permission.') ||
        flagKey.startsWith('role.')
      ) {
        return { value: capabilities.resolveBoolean(flagKey), reason: STATIC }
      }
      const flag = snapshot.flags[flagKey]
      return flag === undefined ? { value: defaultValue, reason: DEFAULT } : { value: flag, reason: STATIC }
    },
    resolveStringEvaluation(flagKey, defaultValue) {
      const variant = capabilities.getVariant(flagKey)
      return variant === undefined
        ? { value: defaultValue, reason: DEFAULT }
        : { value: variant, variant, reason: STATIC }
    },
    resolveNumberEvaluation(_flagKey, defaultValue) {
      return { value: defaultValue, reason: DEFAULT }
    },
    resolveObjectEvaluation(_flagKey, defaultValue) {
      return { value: defaultValue, reason: DEFAULT }
    },
  }
}
`
}

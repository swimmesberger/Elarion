import {
  childContext,
  collectUploadNodes,
  formatPropertyName,
  resolveSchema,
  stripNullable,
  type SchemaContext,
} from './json-schema.js'
import type {JsonSchema, RpcSchema} from './schema.js'

/**
 * The values the server exports next to the method schemas (ADR-0100), rendered as typed constants at the end of the
 * schemas file: `rpcLimits` (every params member's limits, by path), `rpcUploadLimits` (every upload member with its
 * limits, which the client's uploadFile checks), `rpcPresets` (named params values, typed by the method), `rpcSortKeys`
 * and `rpcConstants`. Each block is emitted only when it has an entry, so a schema without any produces byte-identical
 * output.
 */
export function generateClientExportsLines(schema: RpcSchema, methods: readonly string[], typesImport: string): {
  importLines: string[]
  lines: string[]
} {
  const lines: string[] = []

  const limits: Record<string, LimitsNode> = {}
  for (const method of methods) {
    const params = schema.methods[method].params
    const node = collectLimits(stripNullable(params), {root: params, path: `methods.${method}.params`, resolvingRefs: new Set()})
    if (node !== undefined) {
      limits[method] = node
    }
  }
  if (Object.keys(limits).length > 0) {
    lines.push(
      '/**',
      ' * The limits each method\'s params declare — the schema\'s constraint keywords (`maxLength`, `maxItems`, `minimum`, …)',
      ' * and an upload member\'s `maxBytes`/`contentTypes`/`fileExtensions` — by member path (`items` for the elements of a',
      ' * list), as literal types. Show a limit from here instead of copying it; params validation already enforces each one.',
      ' */',
      `export const rpcLimits = ${literal(limits, 0)} as const`,
      '',
      'export type RpcLimits = typeof rpcLimits',
      '',
    )
  }

  // Every upload member, with or without limits: the generated client imports it whenever params take uploaded files,
  // so its uploadFile can check a file against the member it is for and type the method/member pair.
  const uploads: Record<string, Record<string, LimitsNode>> = {}
  for (const method of methods) {
    const members = collectUploadMembers(schema.methods[method].params, `methods.${method}.params`)
    if (Object.keys(members).length > 0) {
      uploads[method] = members
    }
  }
  if (Object.keys(uploads).length > 0) {
    lines.push(
      '/**',
      ' * Every upload member of each method\'s params by member path (a list by its own path, its limits applying to each',
      ' * file), with its declared `maxBytes`/`contentTypes`/`fileExtensions` — `{}` when it declares none. The client\'s',
      ' * `uploadFile(file, { method, member })` checks a file against them before uploading it.',
      ' */',
      `export const rpcUploadLimits = ${literal(uploads, 0)} as const`,
      '',
      'export type RpcUploadLimits = typeof rpcUploadLimits',
      '',
    )
  }

  const presetMethods = methods.filter((method) => Object.keys(schema.methods[method].presets ?? {}).length > 0)
  if (presetMethods.length > 0) {
    const typeLines: string[] = ['{']
    const values: Record<string, Record<string, unknown>> = {}
    for (const method of presetMethods) {
      const presets = schema.methods[method].presets as Record<string, Record<string, unknown>>
      const names = Object.keys(presets).sort()
      typeLines.push(`  readonly ${JSON.stringify(method)}: {`)
      for (const name of names) {
        typeLines.push(`    readonly ${formatPropertyName(name)}: RpcMethods[${JSON.stringify(method)}]['params']`)
      }
      typeLines.push('  }')
      values[method] = Object.fromEntries(names.map((name) => [name, presets[name]]))
    }
    typeLines.push('}')
    lines.push(
      '/**',
      ' * Named request presets (`[RequestPreset]` on the handler), typed as the method\'s params: the exact request the',
      ' * server counts with. Spread one into a call (`{ ...rpcPresets[m].name, page: 2 }`) to open what it counted.',
      ' */',
      `export const rpcPresets: ${typeLines.join('\n')} = ${literal(values, 0)}`,
      '',
    )
  }

  const sortKeyMethods = methods.filter((method) => (schema.methods[method].sortKeys ?? []).length > 0)
  if (sortKeyMethods.length > 0) {
    const values = Object.fromEntries(sortKeyMethods.map((method) => [
      method,
      (schema.methods[method].sortKeys ?? []).map((sortKey) => ({
        key: sortKey.key,
        direction: sortKey.direction,
        default: sortKey.default === true,
      })),
    ]))
    lines.push(
      '/**',
      ' * The sort keys each method\'s handler allows (its sort map), in declaration order. A bare key sorts in the key\'s',
      ' * `direction`; `+key` forces ascending and `-key` descending. `default` marks the key used when none is sent.',
      ' */',
      `export const rpcSortKeys = ${literal(values, 0)} as const`,
      '',
      'export type RpcSortKeys = typeof rpcSortKeys',
      '',
    )
  }

  const constants = schema.constants ?? {}
  const modules = Object.keys(constants).sort().filter((module) => Object.keys(constants[module]).length > 0)
  if (modules.length > 0) {
    const values = Object.fromEntries(modules.map((module) => [
      module,
      Object.fromEntries(Object.keys(constants[module]).sort().map((name) => [name, constants[module][name]])),
    ]))
    lines.push(
      '/** The constants each module exports (`[ClientConstants]`), by module and name, as literal types. */',
      `export const rpcConstants = ${literal(values, 0)} as const`,
      '',
      'export type RpcConstants = typeof rpcConstants',
      '',
    )
  }

  return {
    importLines: presetMethods.length > 0 ? [`import type { RpcMethods } from '${typesImport}'`] : [],
    lines,
  }
}

type LimitsNode = { [key: string]: number | string[] | LimitsNode }

const LIMIT_KEYWORDS = [
  'minLength',
  'maxLength',
  'minimum',
  'exclusiveMinimum',
  'maximum',
  'exclusiveMaximum',
  'minItems',
  'maxItems',
] as const

// A member's own limits, then its elements' (`items`) or its properties' by name. Objects carry no limit keywords, so
// property names never collide with them; union cases are skipped, as no fixed path reaches them.
function collectLimits(schema: JsonSchema, ctx: SchemaContext): LimitsNode | undefined {
  const {schema: resolved, ctx: resolvedCtx} = resolveSchema(schema, ctx)
  const node: LimitsNode = {}
  for (const keyword of LIMIT_KEYWORDS) {
    const value = resolved[keyword]
    if (value !== undefined) {
      node[keyword] = value
    }
  }
  if (resolved['x-elarion-upload'] === true) {
    Object.assign(node, uploadLimits(resolved))
  }
  if (resolved.items !== undefined) {
    const items = collectLimits(stripNullable(resolved.items), childContext(resolvedCtx, 'items'))
    if (items !== undefined) {
      node.items = items
    }
  }
  if (resolved.properties !== undefined && resolved.anyOf === undefined) {
    for (const [key, property] of Object.entries(resolved.properties)) {
      const child = collectLimits(stripNullable(property), childContext(resolvedCtx, `properties.${key}`))
      if (child !== undefined) {
        node[key] = child
      }
    }
  }
  return Object.keys(node).length > 0 ? node : undefined
}

// An upload member's declared limits (ADR-0100, ADR-0105), under the names rpcLimits and rpcUploadLimits use.
function uploadLimits(schema: JsonSchema): LimitsNode {
  const node: LimitsNode = {}
  if (schema['x-elarion-max-bytes'] !== undefined) {
    node.maxBytes = schema['x-elarion-max-bytes']
  }
  if (schema['x-elarion-content-types'] !== undefined && schema['x-elarion-content-types'].length > 0) {
    node.contentTypes = [...schema['x-elarion-content-types']]
  }
  if (schema['x-elarion-file-extensions'] !== undefined && schema['x-elarion-file-extensions'].length > 0) {
    node.fileExtensions = [...schema['x-elarion-file-extensions']]
  }
  return node
}

// Every upload member of a params schema by member path, the list levels ('*') left out, with its declared limits.
function collectUploadMembers(params: JsonSchema, path: string): Record<string, LimitsNode> {
  const members: Record<string, LimitsNode> = {}
  const nodes = collectUploadNodes(stripNullable(params), {root: params, path, resolvingRefs: new Set()})
  for (const node of nodes) {
    members[node.path.filter((segment) => segment !== '*').join('.')] = uploadLimits(node.schema)
  }
  return members
}

// A deterministic TypeScript literal of JSON data: objects one property per line, arrays of scalars on one line.
function literal(value: unknown, indent: number): string {
  if (value === null || typeof value !== 'object') {
    return value === undefined ? 'undefined' : JSON.stringify(value)
  }
  const pad = '  '.repeat(indent + 1)
  const closing = '  '.repeat(indent)
  if (Array.isArray(value)) {
    if (value.length === 0) {
      return '[]'
    }
    if (value.every((item) => item === null || typeof item !== 'object')) {
      return `[${value.map((item) => JSON.stringify(item)).join(', ')}]`
    }
    return `[\n${value.map((item) => `${pad}${literal(item, indent + 1)},`).join('\n')}\n${closing}]`
  }
  const entries = Object.entries(value as Record<string, unknown>)
  if (entries.length === 0) {
    return '{}'
  }
  return `{\n${entries.map(([key, item]) => `${pad}${formatPropertyName(key)}: ${literal(item, indent + 1)},`).join('\n')}\n${closing}}`
}

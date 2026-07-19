import type {JsonSchema} from './schema.js'
import {
  baseType,
  childContext,
  formatPropertyName,
  isNullable,
  resolveSchema,
  type SchemaContext,
} from './json-schema.js'

export function jsonSchemaToTypeScript(schema: JsonSchema, ctx: SchemaContext, indent = 0): string {
  const {schema: resolved, ctx: resolvedCtx} = resolveSchema(schema, ctx)
  const pad = '  '.repeat(indent)
  const nullable = isNullable(resolved)
  const base = baseType(resolved)

  // The Elarion file envelope surfaces as a native File; the generated client converts to/from the
  // base64 wire shape at the call boundary, so callers never see the envelope.
  if (resolved['x-elarion-file'] === true) {
    return nullable ? 'File | null | undefined' : 'File'
  }

  if (resolved.enum) {
    const values = resolved.enum
      .filter((value) => value !== null)
      .map((value) => JSON.stringify(value))
    const union = values.length > 0 ? values.join(' | ') : 'never'
    return nullable ? `(${union}) | null | undefined` : union
  }

  if (base === 'string') {
    return nullable ? 'string | null | undefined' : 'string'
  }
  if (base === 'number' || base === 'integer') {
    return nullable ? 'number | null | undefined' : 'number'
  }
  if (base === 'boolean') {
    return nullable ? 'boolean | null | undefined' : 'boolean'
  }

  if (base === 'array' && resolved.items) {
    // Unstripped items: the recursion emits the item's own nullability (`string | null | undefined`).
    const itemType = jsonSchemaToTypeScript(resolved.items, childContext(resolvedCtx, 'items'), indent)
    // A union item type must be parenthesized — `"red" | "green"[]` is `"red" | ("green"[])`.
    const element = itemType.includes('|') ? `(${itemType})` : itemType
    return nullable ? `${element}[] | null | undefined` : `${element}[]`
  }

  if (base === 'object' && resolved.properties) {
    const required = new Set(resolved.required ?? [])
    const lines = Object.entries(resolved.properties).map(([key, property]) => {
      const optional = required.has(key) ? '' : '?'
      const propertyType = jsonSchemaToTypeScript(property, childContext(resolvedCtx, `properties.${key}`), indent + 1)
      return `${pad}  ${formatPropertyName(key)}${optional}: ${propertyType}`
    })
    const objectType = `{\n${lines.join('\n')}\n${pad}}`
    return nullable ? `${objectType} | null | undefined` : objectType
  }

  return 'unknown'
}

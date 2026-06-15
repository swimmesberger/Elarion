import type { JsonSchema } from './schema.js'
import {
  baseType,
  childContext,
  formatPropertyName,
  isNullable,
  resolveSchema,
  stripNullable,
  type SchemaContext,
} from './json-schema.js'

export function jsonSchemaToTypeScript(schema: JsonSchema, ctx: SchemaContext, indent = 0): string {
  const resolved = resolveSchema(schema, ctx)
  const pad = '  '.repeat(indent)
  const nullable = isNullable(resolved)
  const base = baseType(resolved)

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
    const itemType = jsonSchemaToTypeScript(
      stripNullable(resolved.items),
      childContext(ctx, 'items'),
      indent
    )
    return nullable ? `(${itemType})[] | null | undefined` : `${itemType}[]`
  }

  if (base === 'object' && resolved.properties) {
    const required = new Set(resolved.required ?? [])
    const lines = Object.entries(resolved.properties).map(([key, property]) => {
      const optional = required.has(key) ? '' : '?'
      const propertyType = jsonSchemaToTypeScript(property, childContext(ctx, `properties.${key}`), indent + 1)
      return `${pad}  ${formatPropertyName(key)}${optional}: ${propertyType}`
    })
    const objectType = `{\n${lines.join('\n')}\n${pad}}`
    return nullable ? `${objectType} | null | undefined` : objectType
  }

  return 'unknown'
}

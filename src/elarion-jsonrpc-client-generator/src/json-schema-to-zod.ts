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

export function jsonSchemaToZod(schema: JsonSchema, ctx: SchemaContext, indent = 0): string {
  const resolved = resolveSchema(schema, ctx)
  const nullable = isNullable(resolved)
  const base = baseType(resolved)

  if (resolved.enum) {
    const values = resolved.enum.filter((value) => value !== null)
    const allStrings = values.every((value) => typeof value === 'string')
    let zodExpression: string
    if (allStrings && values.length > 0) {
      zodExpression = `z.enum([${values.map((value) => JSON.stringify(value)).join(', ')}])`
    } else {
      const literals = values.map((value) => `z.literal(${JSON.stringify(value)})`)
      zodExpression = literals.length === 1
        ? literals[0]
        : `z.union([${literals.join(', ')}])`
    }
    return nullish(zodExpression, nullable)
  }

  if (base === 'string') {
    return nullish('z.string()', nullable)
  }
  if (base === 'number' || base === 'integer') {
    return nullish('z.number()', nullable)
  }
  if (base === 'boolean') {
    return nullish('z.boolean()', nullable)
  }

  if (base === 'array' && resolved.items) {
    const itemSchema = jsonSchemaToZod(stripNullable(resolved.items), childContext(ctx, 'items'), indent)
    return nullish(`z.array(${itemSchema})`, nullable)
  }

  if (base === 'object' && resolved.properties) {
    const required = new Set(resolved.required ?? [])
    const pad = '  '.repeat(indent + 1)
    const fields = Object.entries(resolved.properties).map(([key, property]) => {
      let fieldSchema = jsonSchemaToZod(property, childContext(ctx, `properties.${key}`), indent + 1)
      if (!required.has(key)) {
        fieldSchema += '.optional()'
      }
      return `${pad}${formatPropertyName(key)}: ${fieldSchema},`
    })
    return nullish(`z.object({\n${fields.join('\n')}\n${'  '.repeat(indent)}})`, nullable)
  }

  return 'z.unknown()'
}

function nullish(expression: string, nullable: boolean): string {
  return nullable ? `${expression}.nullish()` : expression
}

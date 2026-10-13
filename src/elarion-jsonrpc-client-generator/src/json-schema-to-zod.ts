import type {JsonSchema} from './schema.js'
import {
  baseType,
  childContext,
  formatPropertyName,
  isNullable,
  resolveSchema,
  type SchemaContext,
} from './json-schema.js'

export function jsonSchemaToZod(schema: JsonSchema, ctx: SchemaContext, indent = 0): string {
  const {schema: resolved, ctx: resolvedCtx} = resolveSchema(schema, ctx)
  const nullable = isNullable(resolved)
  const base = baseType(resolved)

  // Schemas validate the user-facing shape: a file field is a native File on both sides of a call (the
  // client encodes params after validation and decodes results before validation).
  if (resolved['x-elarion-file'] === true) {
    return nullish('z.instanceof(File)', nullable)
  }

  if (resolved.enum) {
    const values = resolved.enum.filter((value) => value !== null)
    const allStrings = values.every((value) => typeof value === 'string')
    let zodExpression: string
    if (allStrings && values.length > 0) {
      zodExpression = `z.enum([${values.map((value) => JSON.stringify(value)).join(', ')}]${paramsArg(resolved, 'enum')})`
    } else {
      const literals = values.map((value) => `z.literal(${JSON.stringify(value)})`)
      zodExpression = literals.length === 1
        ? `z.literal(${JSON.stringify(values[0])}${paramsArg(resolved, 'enum')})`
        : `z.union([${literals.join(', ')}]${paramsArg(resolved, 'enum')})`
    }
    return nullish(zodExpression, nullable)
  }

  if (base === 'string') {
    return nullish(stringSchema(resolved), nullable)
  }
  if (base === 'number' || base === 'integer') {
    return nullish(numberSchema(resolved, base), nullable)
  }
  if (base === 'boolean') {
    return nullish('z.boolean()', nullable)
  }

  if (base === 'array' && resolved.items) {
    // The item schema goes in unstripped: the recursion handles item-level nullability, so a
    // `["string","null"]` item emits `z.string().nullish()` and a legal `["a", null]` element validates.
    // Nullability is only ever stripped at the top-level params/result envelope.
    const itemSchema = jsonSchemaToZod(resolved.items, childContext(resolvedCtx, 'items'), indent)
    return nullish(arraySchema(`z.array(${itemSchema})`, resolved), nullable)
  }

  if (base === 'object' && resolved.properties) {
    const required = new Set(resolved.required ?? [])
    const pad = '  '.repeat(indent + 1)
    const fields = Object.entries(resolved.properties).map(([key, property]) => {
      let fieldSchema = jsonSchemaToZod(property, childContext(resolvedCtx, `properties.${key}`), indent + 1)
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

// A Zod check call carrying, as its last argument, the explicit DataAnnotations message the server exporter
// attached to the check's JSON Schema keyword. Checks take the plain-string message form, which Zod v3 and v4
// both accept without deprecation. No message → no argument, so message-free schemas stay byte-identical.
function check(name: string, args: readonly string[], schema: JsonSchema, keyword: string): string {
  const message = errorMessage(schema, keyword)
  return `.${name}(${(message === undefined ? args : [...args, JSON.stringify(message)]).join(', ')})`
}

// Schema constructors (z.enum, z.literal, z.union) take the message as `{ message }`, the one params form
// Zod v3 (no string shorthand there) and v4 share.
function paramsArg(schema: JsonSchema, keyword: string): string {
  const message = errorMessage(schema, keyword)
  return message === undefined ? '' : `, { message: ${JSON.stringify(message)} }`
}

function errorMessage(schema: JsonSchema, keyword: string): string | undefined {
  return schema['x-elarion-error-messages']?.[keyword]
}

// Constraint keywords apply to the inner type in a deterministic modifier order — base type, then
// `.int()`, then bounds (min-side before max-side), then format/regex refinements — with `.nullish()`
// always appended last by the caller.
function stringSchema(schema: JsonSchema): string {
  let expression = 'z.string()'
  if (schema.minLength !== undefined) {
    expression += check('min', [String(schema.minLength)], schema, 'minLength')
  }
  if (schema.maxLength !== undefined) {
    expression += check('max', [String(schema.maxLength)], schema, 'maxLength')
  }
  if (schema.format === 'uuid') {
    expression += check('uuid', [], schema, 'format')
  } else if (schema.format === 'email') {
    expression += check('email', [], schema, 'format')
  } else if (schema.format === 'uri') {
    expression += check('url', [], schema, 'format')
  }
  if (schema.pattern !== undefined) {
    // new RegExp(<JSON string literal>) is deterministic and safe for any pattern content (slashes,
    // quotes, backslashes) — a /.../ regex literal would need its own escaping rules.
    expression += check('regex', [`new RegExp(${JSON.stringify(schema.pattern)})`], schema, 'pattern')
  }
  return expression
}

function numberSchema(schema: JsonSchema, base: string): string {
  let expression = 'z.number()'
  if (base === 'integer') {
    expression += '.int()'
  }
  if (schema.minimum !== undefined) {
    expression += check('gte', [String(schema.minimum)], schema, 'minimum')
  }
  if (schema.exclusiveMinimum !== undefined) {
    expression += check('gt', [String(schema.exclusiveMinimum)], schema, 'exclusiveMinimum')
  }
  if (schema.maximum !== undefined) {
    expression += check('lte', [String(schema.maximum)], schema, 'maximum')
  }
  if (schema.exclusiveMaximum !== undefined) {
    expression += check('lt', [String(schema.exclusiveMaximum)], schema, 'exclusiveMaximum')
  }
  return expression
}

function arraySchema(expression: string, schema: JsonSchema): string {
  let result = expression
  if (schema.minItems !== undefined) {
    result += check('min', [String(schema.minItems)], schema, 'minItems')
  }
  if (schema.maxItems !== undefined) {
    result += check('max', [String(schema.maxItems)], schema, 'maxItems')
  }
  return result
}

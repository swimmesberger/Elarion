import type { JsonSchema } from './schema.js'

export class UnsupportedJsonSchemaError extends Error {
  constructor(
    public readonly schemaPath: string,
    message: string
  ) {
    super(`${schemaPath}: ${message}`)
    this.name = 'UnsupportedJsonSchemaError'
  }
}

export interface SchemaContext {
  root: JsonSchema
  path: string
  resolvingRefs: ReadonlySet<string>
}

export function isNullable(schema: JsonSchema): boolean {
  if (Array.isArray(schema.type)) {
    return schema.type.includes('null')
  }
  if (schema.enum) {
    return schema.enum.includes(null)
  }
  return false
}

export function baseType(schema: JsonSchema): string | undefined {
  if (Array.isArray(schema.type)) {
    return schema.type.find((type) => type !== 'null')
  }
  return schema.type
}

export function stripNullable(schema: JsonSchema): JsonSchema {
  const copy: JsonSchema = { ...schema }
  if (Array.isArray(copy.type)) {
    const nonNull = copy.type.filter((type) => type !== 'null')
    copy.type = nonNull.length === 1 ? nonNull[0] : nonNull
  }
  if (copy.enum) {
    copy.enum = copy.enum.filter((value) => value !== null)
  }
  return copy
}

export function resolveSchema(schema: JsonSchema, ctx: SchemaContext): JsonSchema {
  assertSupportedComposition(schema, ctx.path)

  if (!schema.$ref) {
    return schema
  }

  if (!schema.$ref.startsWith('#/')) {
    throw new UnsupportedJsonSchemaError(ctx.path, `only local JSON Pointer $ref values are supported (${schema.$ref})`)
  }

  if (ctx.resolvingRefs.has(schema.$ref)) {
    throw new UnsupportedJsonSchemaError(ctx.path, `cyclic $ref detected (${schema.$ref})`)
  }

  const target = resolveJsonPointer(ctx.root, schema.$ref, ctx.path)
  return resolveSchema(target, {
    root: ctx.root,
    path: `${ctx.path}${schema.$ref}`,
    resolvingRefs: new Set([...ctx.resolvingRefs, schema.$ref]),
  })
}

export function childContext(ctx: SchemaContext, segment: string): SchemaContext {
  return {
    root: ctx.root,
    path: `${ctx.path}.${segment}`,
    resolvingRefs: ctx.resolvingRefs,
  }
}

export function formatPropertyName(key: string): string {
  if (/^[A-Za-z_$][\w$]*$/.test(key)) {
    return key
  }
  return JSON.stringify(key)
}

function assertSupportedComposition(schema: JsonSchema, path: string) {
  if (schema.oneOf) {
    throw new UnsupportedJsonSchemaError(path, 'oneOf is not supported')
  }
  if (schema.anyOf) {
    throw new UnsupportedJsonSchemaError(path, 'anyOf is not supported')
  }
  if (schema.allOf) {
    throw new UnsupportedJsonSchemaError(path, 'allOf is not supported')
  }
}

function resolveJsonPointer(root: JsonSchema, ref: string, path: string): JsonSchema {
  const segments = ref
    .slice(2)
    .split('/')
    .map((segment) => segment.replace(/~1/g, '/').replace(/~0/g, '~'))

  let current: unknown = root
  for (const segment of segments) {
    if (!isObjectRecord(current) || !(segment in current)) {
      throw new UnsupportedJsonSchemaError(path, `could not resolve $ref ${ref}`)
    }
    current = current[segment]
  }

  if (!isObjectRecord(current)) {
    throw new UnsupportedJsonSchemaError(path, `$ref ${ref} does not point to a schema object`)
  }

  return current as JsonSchema
}

function isObjectRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

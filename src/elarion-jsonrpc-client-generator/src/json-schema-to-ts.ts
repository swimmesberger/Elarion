import type {JsonSchema} from './schema.js'
import {
  baseType,
  childContext,
  formatPropertyName,
  isNullable,
  isNullOnly,
  resolveSchema,
  stripNullable,
  type SchemaContext,
  unionAlternatives,
} from './json-schema.js'

/**
 * @param uploadInput emit the params form of an `x-elarion-upload` member (`File | Blob | UploadedFileRef`: the
 *   client uploads a `File`/`Blob` before the call) instead of the reference alone.
 */
export function jsonSchemaToTypeScript(schema: JsonSchema, ctx: SchemaContext, indent = 0, uploadInput = false): string {
  const {schema: resolved, ctx: resolvedCtx} = resolveSchema(schema, ctx)
  const pad = '  '.repeat(indent)
  const nullable = isNullable(resolved)
  const base = baseType(resolved)

  // The Elarion file envelope surfaces as a native File; the generated client converts to/from the
  // base64 wire shape at the call boundary, so callers never see the envelope.
  if (resolved['x-elarion-file'] === true) {
    return nullable ? 'File | null | undefined' : 'File'
  }

  // An uploaded-file reference travels as the blob id; in params the client also takes the file itself and uploads it.
  if (resolved['x-elarion-upload'] === true) {
    const upload = uploadInput ? 'File | Blob | UploadedFileRef' : 'UploadedFileRef'
    return nullable ? `${upload} | null | undefined` : upload
  }

  if (resolved.enum) {
    const values = resolved.enum
      .filter((value) => value !== null)
      .map((value) => JSON.stringify(value))
    const union = values.length > 0 ? values.join(' | ') : 'never'
    return nullable ? `(${union}) | null | undefined` : union
  }

  const alternatives = unionAlternatives(resolved)
  if (alternatives) {
    // A C# union: the type of whichever case is active. Each alternative is emitted without its own nullability
    // and `null` is added once for the union, so `Found | Missing | null | undefined` stays flat.
    let unionNullable = nullable
    const members: string[] = []
    alternatives.forEach((alternative, index) => {
      const {schema: member, ctx: memberCtx} = resolveSchema(alternative, childContext(resolvedCtx, `anyOf[${index}]`))
      unionNullable ||= isNullable(member)
      if (isNullOnly(member)) {
        return
      }
      const memberType = jsonSchemaToTypeScript(stripNullable(member), memberCtx, indent, uploadInput)
      if (!members.includes(memberType)) {
        members.push(memberType)
      }
    })
    const union = members.length > 0 ? members.join(' | ') : 'never'
    return unionNullable ? `${union} | null | undefined` : union
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
    const itemType = jsonSchemaToTypeScript(resolved.items, childContext(resolvedCtx, 'items'), indent, uploadInput)
    // A union item type must be parenthesized — `"red" | "green"[]` is `"red" | ("green"[])`.
    const element = itemType.includes('|') ? `(${itemType})` : itemType
    return nullable ? `${element}[] | null | undefined` : `${element}[]`
  }

  if (base === 'object' && resolved.properties) {
    const required = new Set(resolved.required ?? [])
    const lines = Object.entries(resolved.properties).map(([key, property]) => {
      const optional = required.has(key) ? '' : '?'
      const propertyType = jsonSchemaToTypeScript(
        property, childContext(resolvedCtx, `properties.${key}`), indent + 1, uploadInput)
      // A member the server shows only to holders of a permission ([VisibleWith]) says so where the caller reads it.
      const doc = visibleWithDoc(property['x-elarion-visible-with'], pad)
      return `${doc}${pad}  ${formatPropertyName(key)}${optional}: ${propertyType}`
    })
    const objectType = `{\n${lines.join('\n')}\n${pad}}`
    return nullable ? `${objectType} | null | undefined` : objectType
  }

  return 'unknown'
}

/** The doc line of a member shown only to holders of a permission, or of any one of several. */
function visibleWithDoc(visibleWith: string | string[] | undefined, pad: string): string {
  const permissions = typeof visibleWith === 'string'
    ? [visibleWith]
    : Array.isArray(visibleWith) ? visibleWith.filter((p): p is string => typeof p === 'string') : []
  if (permissions.length === 0) return ''

  const quoted = permissions.map(p => `\`${p}\``)
  const holding = quoted.length === 1
    ? `the ${quoted[0]} permission`
    : `any of the ${quoted.slice(0, -1).join(', ')} or ${quoted[quoted.length - 1]} permissions`
  return `${pad}  /** Present only for callers holding ${holding}. */\n`
}

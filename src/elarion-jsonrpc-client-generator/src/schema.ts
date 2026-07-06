export interface JsonSchema {
  $ref?: string
  type?: string | string[]
  properties?: Record<string, JsonSchema>
  required?: string[]
  items?: JsonSchema
  enum?: Array<string | number | boolean | null>
  format?: string
  default?: unknown
  oneOf?: unknown[]
  anyOf?: unknown[]
  allOf?: unknown[]
  /**
   * Marks the Elarion binary-file envelope (`{ contentType, fileName?, data }`, base64 `data`), emitted by the
   * server exporter for `ElarionFile` payloads. The generator maps it to a native `File`: params accept a
   * `File`, results materialize one; the client converts to/from the wire envelope at the call boundary.
   */
  'x-elarion-file'?: boolean
  // Constraint keywords (JSON Schema 2020-12). exclusiveMinimum/exclusiveMaximum are numbers, not booleans.
  minLength?: number
  maxLength?: number
  pattern?: string
  minimum?: number
  maximum?: number
  exclusiveMinimum?: number
  exclusiveMaximum?: number
  minItems?: number
  maxItems?: number
}

export interface RpcMethodSchema {
  params: JsonSchema
  result: JsonSchema
  // Set by the server exporter for [Idempotent] handlers; the generated client attaches an idempotency key
  // (params._meta) to these operations by default.
  idempotent?: boolean
}

/** One structured permission from the schema's capability vocabulary ({resource}.{verb} plus its parts). */
export interface RpcCapabilityPermission {
  permission: string
  resource: string
  verb: string
}

/**
 * The capability vocabulary block emitted by the server exporter (ADR-0032): module names with the
 * `[ClientFeatures]` each exposes, the structured permission catalog, and role names. All optional — older
 * schemas (or hosts without the session/authorization registrations) simply omit it.
 */
export interface RpcSchemaCapabilities {
  modules?: Record<string, { features?: string[] }>
  permissions?: RpcCapabilityPermission[]
  roles?: string[]
}

export interface RpcSchema {
  methods: Record<string, RpcMethodSchema>
  capabilities?: RpcSchemaCapabilities
}

export interface GenerateRpcClientOptions {
  generatedBy?: string
  sourceLabel?: string
  typesFileName?: string
  schemasFileName?: string
  clientFileName?: string
  /** Output filename for the client-capability snapshot client + OpenFeature provider (default `session-client.ts`). */
  sessionClientFileName?: string
  /** The operation name the client-capability snapshot is served under (default `elarion.session`). */
  sessionOperationName?: string
  /**
   * Emit an opt-in framework adapter alongside the neutral core client. `tanstack-start` produces
   * `start-adapter.ts` (request-scoped cookie forwarding via `@tanstack/react-start`). Omitted → no adapter,
   * so output stays byte-identical for consumers that do not opt in.
   */
  framework?: 'tanstack-start'
  /** Output filename for the framework adapter (default `start-adapter.ts`). */
  frameworkAdapterFileName?: string
}

export interface GeneratedRpcClientFiles {
  methodCount: number
  typesFileName: string
  schemasFileName: string
  clientFileName: string
  typesSource: string
  schemasSource: string
  clientSource: string
  /**
   * The client-capability snapshot client + OpenFeature provider (ADR-0020). Present only when the schema exposes
   * the session operation (default `elarion.session`); otherwise both fields are `undefined`.
   */
  sessionClientFileName?: string
  sessionClientSource?: string
  /**
   * The opt-in framework adapter. Present only when `framework` is requested (e.g. `tanstack-start`); otherwise
   * both fields are `undefined`.
   */
  frameworkAdapterFileName?: string
  frameworkAdapterSource?: string
}

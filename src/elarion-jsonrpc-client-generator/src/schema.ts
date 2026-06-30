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
}

export interface RpcMethodSchema {
  params: JsonSchema
  result: JsonSchema
}

export interface RpcSchema {
  methods: Record<string, RpcMethodSchema>
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
}

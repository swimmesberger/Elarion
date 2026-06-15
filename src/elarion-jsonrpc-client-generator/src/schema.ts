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
}

export interface GeneratedRpcClientFiles {
  methodCount: number
  typesFileName: string
  schemasFileName: string
  clientFileName: string
  typesSource: string
  schemasSource: string
  clientSource: string
}

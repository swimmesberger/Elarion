import { describe, expect, it } from 'vitest'
import { mkdtempSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { pathToFileURL } from 'node:url'
import ts from 'typescript'
import {
  generateRpcClientFiles,
  UnsupportedJsonSchemaError,
  type JsonSchema,
  type RpcSchema,
} from '../src/generate.js'

type FetchLike = (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>

interface RpcClientForTests {
  call(method: string, params: unknown, options?: { signal?: AbortSignal; headers?: HeadersInit }): Promise<unknown>
  batch(
    items: readonly { method: string; params: unknown }[],
    options?: { signal?: AbortSignal; headers?: HeadersInit }
  ): Promise<readonly BatchItemResultForTests[]>
}

interface BatchItemResultForTests {
  ok: boolean
  result?: unknown
  error?: {
    code: number
    message: string
    data?: unknown
  }
}

interface TestSpan {
  headers?: HeadersInit
  setError(error: unknown): void
  end(): void
}

interface TestInstrumentation {
  startSpan(context: { methods: readonly string[]; batch: boolean }): TestSpan | undefined
}

interface GeneratedClientModule {
  createRpcApi(options: {
    url: string
    fetch?: FetchLike
    headers?: HeadersInit | ((context: { methods: readonly string[]; batch: boolean }) => HeadersInit | Promise<HeadersInit>)
    idGenerator?: () => string | number
    validateParams?: boolean
    validateResults?: boolean
    transformResult?: (method: string, result: unknown) => unknown
    instrumentation?: TestInstrumentation
  }): RpcApiForTests
  createRpcClient(options: {
    url: string
    fetch?: FetchLike
    headers?: HeadersInit | ((context: { methods: readonly string[]; batch: boolean }) => HeadersInit | Promise<HeadersInit>)
    idGenerator?: () => string | number
    validateParams?: boolean
    validateResults?: boolean
    transformResult?: (method: string, result: unknown) => unknown
    instrumentation?: TestInstrumentation
  }): RpcClientForTests
  RpcError: new (code: number, message: string, data?: unknown) => Error & {
    code: number
    data?: unknown
    readonly isInvalidParams: boolean
    readonly isInternalError: boolean
    readonly isNotFound: boolean
    readonly isConflict: boolean
    readonly isForbidden: boolean
    readonly isBusinessRule: boolean
    readonly isUnauthorized: boolean
  }
  ElarionErrorCodes: {
    readonly notFound: number
    readonly conflict: number
    readonly forbidden: number
    readonly businessRule: number
    readonly unauthorized: number
  }
  RpcTransportError: new (status: number, statusText: string, body: string) => Error
  RpcProtocolError: new (message: string) => Error
  RpcParamsValidationError: new (method: string, cause: unknown) => Error & { method: string; cause?: unknown }
}

interface RpcApiForTests {
  math: {
    add(params: unknown, options?: { signal?: AbortSignal; headers?: HeadersInit }): Promise<unknown>
  }
  user: {
    get(params: unknown, options?: { signal?: AbortSignal; headers?: HeadersInit }): Promise<unknown>
  }
  $request: {
    math: {
      add(params: unknown): { method: string; params: unknown }
    }
    user: {
      get(params: unknown): { method: string; params: unknown }
    }
  }
  $batch(
    items: readonly { method: string; params: unknown }[],
    options?: { signal?: AbortSignal; headers?: HeadersInit }
  ): Promise<readonly BatchItemResultForTests[]>
  $client: RpcClientForTests
}

describe('JSON-RPC client generator', () => {
  it('generates stable TypeScript and Zod output for common schema shapes', () => {
    const schema = {
      methods: {
        'z.second': {
          params: {
            type: 'object',
            properties: {
              id: { type: 'string', format: 'uuid' },
            },
            required: ['id'],
          },
          result: {
            type: 'object',
            properties: {
              ok: { type: 'boolean' },
            },
            required: ['ok'],
          },
        },
        'a.first': {
          params: {
            type: 'object',
            properties: {
              name: { type: 'string' },
              nickname: { type: ['string', 'null'] },
              tags: {
                type: 'array',
                items: { type: 'string' },
              },
              status: {
                enum: ['Open', 'Closed', null],
              },
            },
            required: ['name', 'nickname', 'tags', 'status'],
          },
          result: {
            type: 'object',
            properties: {
              count: { type: 'integer' },
              maybe: { type: ['number', 'null'] },
              state: { enum: ['Open', 'Closed', null] },
            },
            required: ['count', 'maybe', 'state'],
          },
        },
      },
    } satisfies RpcSchema

    const generated = generateRpcClientFiles(schema, {
      generatedBy: 'test-generator',
      sourceLabel: 'test-schema.json',
    })

    expect(generated.typesSource).toContain('// Auto-generated by test-generator')
    expect(generated.typesSource.indexOf('"a.first"')).toBeLessThan(
      generated.typesSource.indexOf('"z.second"')
    )
    expect(generated.typesSource).toContain('nickname: string | null | undefined')
    expect(generated.typesSource).toContain('tags: string[]')
    expect(generated.typesSource).toContain('status: ("Open" | "Closed") | null | undefined')
    expect(generated.schemasSource).toContain('maybe: z.number().nullish(),')
    expect(generated.schemasSource).toContain('state: z.enum(["Open", "Closed"]).nullish(),')
    expect(generated.schemasSource).toContain('export const rpcParamsSchemas = {')
    expect(generated.schemasSource).toContain('export type RpcParamsSchemas = typeof rpcParamsSchemas')
    expect(generated.schemasSource).toContain('id: z.string().uuid(),')
    expect(generated.schemasSource).toContain('nickname: z.string().nullish(),')
    expect(generated.schemasSource.indexOf('export const rpcParamsSchemas')).toBeLessThan(
      generated.schemasSource.indexOf('export const rpcResultSchemas')
    )
    expect(generated.clientSource).toContain("import type { RpcMethods } from './rpc-types.js'")
    expect(generated.clientSource).toContain("import { rpcParamsSchemas, rpcResultSchemas } from './rpc-schemas.js'")
    expect(generated.clientSource).toContain('export interface RpcApi')
    expect(generated.clientSource).toContain('readonly "a": {')
    expect(generated.clientSource).toContain('readonly "first": RpcEndpoint<"a.first">')
    expect(generated.clientSource).toContain('export function createRpcClient')
    expect(generated.clientSource).toContain('export function createRpcApi')
  })

  it('preserves nullable array items in types and Zod schemas', () => {
    const schema = {
      methods: {
        'nullable.items': {
          params: {
            type: 'object',
            properties: {
              tags: {
                type: 'array',
                items: { type: ['string', 'null'] },
              },
            },
            required: ['tags'],
          },
          result: {
            type: 'array',
            items: { type: ['number', 'null'] },
          },
        },
      },
    } satisfies RpcSchema

    const generated = generateRpcClientFiles(schema)

    // Item-level nullability survives: a legal ["a", null] element must validate.
    expect(generated.typesSource).toContain('tags: (string | null | undefined)[]')
    expect(generated.typesSource).toContain('result: (number | null | undefined)[]')
    expect(generated.schemasSource).toContain('tags: z.array(z.string().nullish()),')
    expect(generated.schemasSource).toContain('"nullable.items": z.array(z.number().nullish()),')
  })

  it('parenthesizes union item types in arrays', () => {
    const schema = {
      methods: {
        'colors.list': {
          params: {
            type: 'object',
            properties: {
              colors: {
                type: 'array',
                items: { enum: ['red', 'green'] },
              },
            },
            required: ['colors'],
          },
          result: { type: 'boolean' },
        },
      },
    } satisfies RpcSchema

    const generated = generateRpcClientFiles(schema)

    // Without parentheses this would parse as `"red" | ("green"[])`.
    expect(generated.typesSource).toContain('colors: ("red" | "green")[]')
  })

  it('detects a property-level $ref cycle instead of overflowing the stack', () => {
    const schema = {
      methods: {
        'cyclic.tree': {
          params: { type: 'object', properties: {} },
          result: {
            type: 'object',
            properties: {
              a: { $ref: '#/properties/b' },
              b: {
                type: 'object',
                properties: {
                  back: { $ref: '#/properties/a' },
                },
              },
            },
          },
        },
      },
    } satisfies RpcSchema

    // The self-referential DTO shape STJ emits (pointer refs through properties) must raise the intended
    // unsupported-schema error, not a RangeError from unbounded recursion.
    expect(() => generateRpcClientFiles(schema)).toThrow(UnsupportedJsonSchemaError)
    expect(() => generateRpcClientFiles(schema)).toThrow(/cyclic \$ref detected/)
  })

  it('orders emitted API properties by code units, independent of host locale', () => {
    const schema = {
      methods: {
        'a.lower': {
          params: { type: 'object', properties: {} },
          result: { type: 'boolean' },
        },
        'B.upper': {
          params: { type: 'object', properties: {} },
          result: { type: 'boolean' },
        },
      },
    } satisfies RpcSchema

    const generated = generateRpcClientFiles(schema)

    // Code-unit order puts "B" (0x42) before "a" (0x61); localeCompare would flip them in most locales.
    expect(generated.clientSource.indexOf('readonly "B"')).toBeGreaterThan(-1)
    expect(generated.clientSource.indexOf('readonly "B"')).toBeLessThan(
      generated.clientSource.indexOf('readonly "a"')
    )
  })

  it('strips top-level params and result nullability', () => {
    const schema = {
      methods: {
        'nullable.topLevel': {
          params: {
            type: ['object', 'null'],
            properties: {
              id: { type: 'string' },
            },
            required: ['id'],
          },
          result: {
            type: ['number', 'null'],
          },
        },
      },
    } satisfies RpcSchema

    const generated = generateRpcClientFiles(schema)

    expect(generated.typesSource).toContain('params: {')
    expect(generated.typesSource).toContain('result: number')
    expect(generated.typesSource).not.toContain('result: number | null | undefined')
    expect(generated.schemasSource).toContain('"nullable.topLevel": z.number(),')
    expect(generated.schemasSource).toContain('"nullable.topLevel": z.object({')
  })

  it('maps constraint keywords onto deterministic Zod modifiers', () => {
    const schema = {
      methods: {
        'constraints.check': {
          params: {
            type: 'object',
            properties: {
              name: { type: 'string', minLength: 3, maxLength: 100 },
              slug: { type: 'string', pattern: '^[a-z0-9-]+$' },
              path: { type: 'string', pattern: '^/api/v\\d+/users/' },
              email: { type: 'string', format: 'email' },
              website: { type: 'string', format: 'uri' },
              age: { type: 'integer', minimum: 0, maximum: 150 },
              score: { type: 'number', exclusiveMinimum: 0, exclusiveMaximum: 1 },
              tags: {
                type: 'array',
                items: { type: 'string', minLength: 1 },
                minItems: 1,
                maxItems: 10,
              },
              note: { type: ['string', 'null'], minLength: 1, maxLength: 20 },
            },
            required: ['name', 'slug', 'path', 'email', 'website', 'age', 'score', 'tags', 'note'],
          },
          result: { type: 'integer', minimum: 1 },
        },
      },
    } satisfies RpcSchema

    const generated = generateRpcClientFiles(schema)

    expect(generated.schemasSource).toContain('name: z.string().min(3).max(100),')
    expect(generated.schemasSource).toContain('slug: z.string().regex(new RegExp("^[a-z0-9-]+$")),')
    // A slash-containing pattern survives verbatim through the JSON-stringified RegExp constructor form.
    expect(generated.schemasSource).toContain('path: z.string().regex(new RegExp("^/api/v\\\\d+/users/")),')
    expect(generated.schemasSource).toContain('email: z.string().email(),')
    expect(generated.schemasSource).toContain('website: z.string().url(),')
    expect(generated.schemasSource).toContain('age: z.number().int().gte(0).lte(150),')
    expect(generated.schemasSource).toContain('score: z.number().gt(0).lt(1),')
    expect(generated.schemasSource).toContain('tags: z.array(z.string().min(1)).min(1).max(10),')
    // Constraints apply to the inner type; .nullish() stays the outermost wrapper.
    expect(generated.schemasSource).toContain('note: z.string().min(1).max(20).nullish(),')
    expect(generated.schemasSource).toContain('"constraints.check": z.number().int().gte(1),')
    // The plain TypeScript types are intentionally unchanged by constraints.
    expect(generated.typesSource).toContain('name: string')
    expect(generated.typesSource).toContain('age: number')
    expect(generated.typesSource).toContain('tags: string[]')
  })

  it('resolves local JSON pointer references inside a method schema', () => {
    const schema = {
      methods: {
        'ea.summary': {
          params: {
            type: 'object',
            properties: {},
          },
          result: {
            type: 'object',
            properties: {
              incomeByCategory: {
                type: 'array',
                items: {
                  type: 'object',
                  properties: {
                    taxCategoryId: { type: 'string' },
                    netAmount: { type: 'number' },
                  },
                  required: ['taxCategoryId', 'netAmount'],
                },
              },
              expenseByCategory: {
                type: 'array',
                items: {
                  $ref: '#/properties/incomeByCategory/items',
                },
              },
            },
            required: ['incomeByCategory', 'expenseByCategory'],
          },
        },
      },
    } satisfies RpcSchema

    const generated = generateRpcClientFiles(schema)

    expect(generated.typesSource).toContain('expenseByCategory: {')
    expect(generated.typesSource).toContain('taxCategoryId: string')
    expect(generated.typesSource).not.toContain('expenseByCategory: unknown[]')
    expect(generated.schemasSource).toContain('expenseByCategory: z.array(z.object({')
  })

  it('fails fast for unsupported schema composition', () => {
    const schema = {
      methods: {
        'unsupported.oneOf': {
          params: { type: 'object', properties: {} },
          result: {
            oneOf: [{ type: 'string' }, { type: 'number' }],
          },
        },
      },
    } satisfies RpcSchema

    expect(() => generateRpcClientFiles(schema)).toThrow(UnsupportedJsonSchemaError)
  })

  it('generates a fetch client with headers, AbortSignal, result transform, and validation', async () => {
    const generated = generateRpcClientFiles(rpcClientTestSchema())
    const clientModule = await loadGeneratedClient(generated.clientSource)
    const abort = new AbortController()
    const seenContexts: Array<{ methods: readonly string[]; batch: boolean }> = []

    const fetchImpl: FetchLike = async (input, init) => {
      expect(input.toString()).toBe('https://example.test/rpc')
      expect(init?.method).toBe('POST')
      expect(init?.signal).toBe(abort.signal)

      const headers = new Headers(init?.headers)
      expect(headers.get('content-type')).toBe('application/json')
      expect(headers.get('authorization')).toBe('Bearer test')
      expect(headers.get('x-request')).toBe('single')

      const request = JSON.parse(String(init?.body)) as { id: string; method: string; params: unknown }
      expect(request).toMatchObject({
        id: 'request-1',
        method: 'math.add',
        params: { left: 1, right: 2 },
      })

      return jsonResponse({ jsonrpc: '2.0', id: request.id, result: '3' })
    }

    const rpc = clientModule.createRpcApi({
      url: 'https://example.test/rpc',
      fetch: fetchImpl,
      headers: (context) => {
        seenContexts.push(context)
        return { Authorization: 'Bearer test' }
      },
      idGenerator: () => 'request-1',
      transformResult: (method, result) => method === 'math.add' ? Number(result) : result,
    })

    await expect(
      rpc.math.add({ left: 1, right: 2 }, {
        signal: abort.signal,
        headers: { 'X-Request': 'single' },
      })
    ).resolves.toBe(3)
    expect(seenContexts).toEqual([{ methods: ['math.add'], batch: false }])
  })

  it('maps x-elarion-file nodes to native File in types and Zod schemas', () => {
    const generated = generateRpcClientFiles(fileClientTestSchema())

    // Callers never see the wire envelope: params take a File, results are a File.
    expect(generated.typesSource).toContain('file?: File')
    expect(generated.typesSource).toContain('required: File')
    expect(generated.typesSource).toContain('result: File')
    expect(generated.schemasSource).toContain('z.instanceof(File)')
  })

  it('emits no file-conversion runtime when the schema has no file payloads', () => {
    const generated = generateRpcClientFiles(rpcClientTestSchema())

    expect(generated.clientSource).not.toContain('encodeFileParams')
    expect(generated.clientSource).not.toContain('rpcParamsFilePaths')
  })

  it('converts File params to the base64 envelope and materializes file results as File', async () => {
    const generated = generateRpcClientFiles(fileClientTestSchema())
    const clientModule = await loadGeneratedFileClient(generated.clientSource)

    const upload = new File([new TextEncoder().encode('id;name')], 'clients.csv', { type: 'text/csv' })
    const extra = new File([new TextEncoder().encode('x')], 'extra.bin', { type: 'application/octet-stream' })

    let wireParams: Record<string, unknown> | undefined
    const fetchImpl: FetchLike = async (_input, init) => {
      const request = JSON.parse(String(init?.body)) as { id: string; params: Record<string, unknown> }
      wireParams = request.params
      return jsonResponse({
        jsonrpc: '2.0',
        id: request.id,
        result: { contentType: 'application/pdf', fileName: 'report.pdf', data: btoa('pdf-bytes') },
      })
    }

    const client = clientModule.createRpcClient({
      url: 'https://example.test/rpc',
      fetch: fetchImpl,
      idGenerator: () => 'file-1',
    })

    const result = await client.call('files.roundTrip', {
      container: 'invoices',
      required: upload,
      attachments: [extra],
    }) as File

    expect(wireParams).toMatchObject({
      container: 'invoices',
      required: { contentType: 'text/csv', fileName: 'clients.csv', data: btoa('id;name') },
    })
    expect((wireParams?.attachments as unknown[])[0]).toMatchObject({
      contentType: 'application/octet-stream',
      fileName: 'extra.bin',
      data: btoa('x'),
    })

    expect(result).toBeInstanceOf(File)
    expect(result.name).toBe('report.pdf')
    expect(result.type).toBe('application/pdf')
    expect(new TextDecoder().decode(await result.arrayBuffer())).toBe('pdf-bytes')
  })

  it('encodes File params even when params validation is disabled', async () => {
    const generated = generateRpcClientFiles(fileClientTestSchema())
    const clientModule = await loadGeneratedFileClient(generated.clientSource)

    let wireParams: Record<string, unknown> | undefined
    const fetchImpl: FetchLike = async (_input, init) => {
      const request = JSON.parse(String(init?.body)) as { id: string; params: Record<string, unknown> }
      wireParams = request.params
      return jsonResponse({
        jsonrpc: '2.0',
        id: request.id,
        result: { contentType: 'text/plain', data: btoa('ok') },
      })
    }

    const client = clientModule.createRpcClient({
      url: 'https://example.test/rpc',
      fetch: fetchImpl,
      idGenerator: () => 'file-2',
      validateParams: false,
      validateResults: false,
    })

    const result = await client.call('files.roundTrip', {
      container: 'invoices',
      required: new File([new TextEncoder().encode('raw')], 'raw.txt', { type: 'text/plain' }),
    }) as File

    // Conversion is independent of validation: the wire always carries the envelope, and the caller
    // always receives a File.
    expect(wireParams?.required).toMatchObject({ contentType: 'text/plain', data: btoa('raw') })
    expect(result).toBeInstanceOf(File)
  })

  it('throws typed errors for transport and JSON-RPC failures', async () => {
    const generated = generateRpcClientFiles(rpcClientTestSchema())
    const clientModule = await loadGeneratedClient(generated.clientSource)

    const transportClient = clientModule.createRpcClient({
      url: 'https://example.test/rpc',
      fetch: async () => new Response('temporarily unavailable', {
        status: 503,
        statusText: 'Service Unavailable',
      }),
      idGenerator: () => 'request-1',
    })

    await expect(transportClient.call('math.add', { left: 1, right: 2 }))
      .rejects.toMatchObject({
        name: 'RpcTransportError',
        status: 503,
        body: 'temporarily unavailable',
      })

    const rpcClient = clientModule.createRpcClient({
      url: 'https://example.test/rpc',
      fetch: async () => jsonResponse({
        jsonrpc: '2.0',
        id: 'request-2',
        error: { code: -32602, message: 'Invalid params', data: { field: 'left' } },
      }),
      idGenerator: () => 'request-2',
    })

    await expect(rpcClient.call('math.add', { left: 1, right: 2 }))
      .rejects.toMatchObject({
        name: 'RpcError',
        code: -32602,
        data: { field: 'left' },
      })

    const mismatchedIdClient = clientModule.createRpcClient({
      url: 'https://example.test/rpc',
      fetch: async () => jsonResponse({ jsonrpc: '2.0', id: 'different-request', result: 3 }),
      idGenerator: () => 'request-3',
    })

    await expect(mismatchedIdClient.call('math.add', { left: 1, right: 2 }))
      .rejects.toMatchObject({
        name: 'RpcProtocolError',
        message: 'JSON-RPC response id does not match request id.',
      })
  })

  it('surfaces a null-id error response as the server error, not an id mismatch', async () => {
    const generated = generateRpcClientFiles(rpcClientTestSchema())
    const clientModule = await loadGeneratedClient(generated.clientSource)

    // Per JSON-RPC 2.0, request-level failures (e.g. parse errors) respond with "id": null.
    const client = clientModule.createRpcClient({
      url: 'https://example.test/rpc',
      fetch: async () => jsonResponse({
        jsonrpc: '2.0',
        id: null,
        error: { code: -32700, message: 'Parse error' },
      }),
      idGenerator: () => 'request-1',
    })

    await expect(client.call('math.add', { left: 1, right: 2 })).rejects.toMatchObject({
      name: 'RpcError',
      code: -32700,
      message: 'Parse error',
    })
  })

  it('surfaces a whole-batch error object as the server error, not a protocol error', async () => {
    const generated = generateRpcClientFiles(rpcClientTestSchema())
    const clientModule = await loadGeneratedClient(generated.clientSource)

    // JSON-RPC 2.0: a whole-batch failure is a single error response object instead of an array
    // (Elarion's server sends this shape for "Batch too large").
    const rpc = clientModule.createRpcApi({
      url: 'https://example.test/rpc',
      fetch: async () => jsonResponse({
        jsonrpc: '2.0',
        id: null,
        error: { code: -32600, message: 'Batch too large' },
      }),
      idGenerator: () => 'request-1',
    })

    await expect(rpc.$batch([
      rpc.$request.math.add({ left: 1, right: 2 }),
    ] as const)).rejects.toMatchObject({
      name: 'RpcError',
      code: -32600,
      message: 'Batch too large',
    })
  })

  it('propagates a transform result of null instead of falling back to the raw value', async () => {
    const generated = generateRpcClientFiles(rpcClientTestSchema())
    const clientModule = await loadGeneratedClient(generated.clientSource)

    const client = clientModule.createRpcClient({
      url: 'https://example.test/rpc',
      fetch: async (_input, init) => {
        const request = JSON.parse(String(init?.body)) as { id: string }
        return jsonResponse({ jsonrpc: '2.0', id: request.id, result: 3 })
      },
      idGenerator: () => 'request-1',
      validateResults: false,
      transformResult: () => null,
    })

    await expect(client.call('math.add', { left: 1, right: 2 })).resolves.toBeNull()
  })

  it('exposes Elarion app-error code getters on RpcError, matching the server AppErrorMapper', async () => {
    const generated = generateRpcClientFiles(rpcClientTestSchema())
    const clientModule = await loadGeneratedClient(generated.clientSource)

    // The codes are a wire contract mirrored from src/Elarion.JsonRpc/AppErrorMapper.cs.
    expect(clientModule.ElarionErrorCodes).toMatchObject({
      notFound: -32001,
      conflict: -32002,
      forbidden: -32003,
      businessRule: -32004,
      unauthorized: -32005,
    })

    // End-to-end: a NotFound error response surfaces on the getter, so a consumer branches without re-wrapping.
    const notFoundClient = clientModule.createRpcClient({
      url: 'https://example.test/rpc',
      fetch: async () => jsonResponse({
        jsonrpc: '2.0',
        id: 'request-1',
        error: { code: clientModule.ElarionErrorCodes.notFound, message: 'Client not found' },
      }),
      idGenerator: () => 'request-1',
    })

    await expect(notFoundClient.call('math.add', { left: 1, right: 2 })).rejects.toMatchObject({
      name: 'RpcError',
      code: -32001,
      isNotFound: true,
      isConflict: false,
    })

    const cases = [
      { code: -32002, getter: 'isConflict' },
      { code: -32003, getter: 'isForbidden' },
      { code: -32004, getter: 'isBusinessRule' },
      { code: -32005, getter: 'isUnauthorized' },
      { code: -32602, getter: 'isInvalidParams' },
      { code: -32603, getter: 'isInternalError' },
    ] as const
    for (const { code, getter } of cases) {
      const error = new clientModule.RpcError(code, 'boom')
      expect(error[getter]).toBe(true)
      expect(error.isNotFound).toBe(false)
    }
  })

  it('generates a batch client that preserves input order and per-item errors', async () => {
    const generated = generateRpcClientFiles(rpcClientTestSchema())
    const clientModule = await loadGeneratedClient(generated.clientSource)
    const expectedIds = ['request-1', 'request-2', 'request-3']
    const ids = [...expectedIds]
    const seenContexts: Array<{ methods: readonly string[]; batch: boolean }> = []

    const rpc = clientModule.createRpcApi({
      url: 'https://example.test/rpc',
      fetch: async (_input, init) => {
        const headers = new Headers(init?.headers)
        expect(headers.get('x-batch')).toBe('true')

        const requests = JSON.parse(String(init?.body)) as Array<{ id: string; method: string }>
        expect(requests.map((request) => request.id)).toEqual(expectedIds)
        expect(requests.map((request) => request.method)).toEqual(['math.add', 'user.get', 'math.add'])

        return jsonResponse([
          { jsonrpc: '2.0', id: 'request-2', error: { code: -32601, message: 'Method not found' } },
          { jsonrpc: '2.0', id: 'request-3', result: 'not a number' },
          { jsonrpc: '2.0', id: 'request-1', result: 3 },
        ])
      },
      headers: async (context) => {
        seenContexts.push(context)
        return { 'X-Batch': String(context.batch) }
      },
      idGenerator: () => ids.shift() ?? 'unexpected',
    })

    const results = await rpc.$batch([
      rpc.$request.math.add({ left: 1, right: 2 }),
      rpc.$request.user.get({ id: 'user-1' }),
      rpc.$request.math.add({ left: 3, right: 4 }),
    ] as const)

    expect(results[0]).toMatchObject({ ok: true, result: 3 })
    expect(results[1]).toMatchObject({ ok: false, error: { code: -32601 } })
    expect(results[2]).toMatchObject({ ok: false, error: { code: -32603 } })
    expect(seenContexts).toEqual([
      { methods: ['math.add', 'user.get', 'math.add'], batch: true },
    ])

    const duplicateIdClient = clientModule.createRpcApi({
      url: 'https://example.test/rpc',
      fetch: async () => jsonResponse([
        { jsonrpc: '2.0', id: 'request-1', result: 3 },
        { jsonrpc: '2.0', id: 'request-1', result: 7 },
      ]),
      idGenerator: () => 'request-1',
    })

    await expect(duplicateIdClient.$batch([
      duplicateIdClient.$request.math.add({ left: 1, right: 2 }),
      duplicateIdClient.$request.math.add({ left: 3, right: 4 }),
    ] as const)).rejects.toMatchObject({
      name: 'RpcProtocolError',
      message: 'JSON-RPC batch response contains a duplicate id.',
    })
  })

  it('drives the instrumentation hook for single calls, injecting headers and recording outcome', async () => {
    const generated = generateRpcClientFiles(rpcClientTestSchema())
    const clientModule = await loadGeneratedClient(generated.clientSource)

    const events: string[] = []
    const seenContexts: Array<{ methods: readonly string[]; batch: boolean }> = []
    let seenTraceparent: string | null = null

    const instrumentation: TestInstrumentation = {
      startSpan(context) {
        seenContexts.push(context)
        events.push('start')
        return {
          headers: { traceparent: '00-trace-span-01' },
          setError() {
            events.push('error')
          },
          end() {
            events.push('end')
          },
        }
      },
    }

    const okClient = clientModule.createRpcClient({
      url: 'https://example.test/rpc',
      fetch: async (_input, init) => {
        seenTraceparent = new Headers(init?.headers).get('traceparent')
        return jsonResponse({ jsonrpc: '2.0', id: 'request-1', result: 3 })
      },
      idGenerator: () => 'request-1',
      instrumentation,
    })

    await expect(okClient.call('math.add', { left: 1, right: 2 })).resolves.toBe(3)
    expect(seenTraceparent).toBe('00-trace-span-01')
    expect(seenContexts).toEqual([{ methods: ['math.add'], batch: false }])
    expect(events).toEqual(['start', 'end'])

    events.length = 0
    const errorClient = clientModule.createRpcClient({
      url: 'https://example.test/rpc',
      fetch: async () => jsonResponse({
        jsonrpc: '2.0',
        id: 'request-2',
        error: { code: -32602, message: 'Invalid params' },
      }),
      idGenerator: () => 'request-2',
      instrumentation,
    })

    await expect(errorClient.call('math.add', { left: 1, right: 2 }))
      .rejects.toMatchObject({ name: 'RpcError', code: -32602 })
    expect(events).toEqual(['start', 'error', 'end'])
  })

  it('generates a self-contained session client + OpenFeature provider when the schema exposes elarion.session', async () => {
    const generated = generateRpcClientFiles(sessionSchema(), {
      generatedBy: 'test-generator',
      sourceLabel: 'session.json',
    })

    expect(generated.sessionClientFileName).toBe('session-client.ts')
    expect(generated.sessionClientSource).toBeDefined()
    const source = generated.sessionClientSource as string
    expect(source).toContain('export function createElarionOpenFeatureProvider')
    expect(source).toContain('export class SessionCapabilities')
    expect(source).toContain('export const Keys')
    // The session client is self-contained — it must not import the RPC types or the OpenFeature SDK.
    expect(source).not.toContain('import ')

    const sessionModule = await loadGeneratedSessionClient(source)
    const snapshot = {
      user: { id: 'u-1', isAuthenticated: true, roles: ['admin'], permissions: ['billing.write'] },
      modules: { Billing: true, Experiments: false },
      flags: { 'new-checkout': true },
      variants: { ForecastAlgorithm: 'neural' },
    }

    const provider = sessionModule.createElarionOpenFeatureProvider(snapshot)
    expect(provider.runsOn).toBe('client')
    expect(provider.resolveBooleanEvaluation('module.Billing', false).value).toBe(true)
    expect(provider.resolveBooleanEvaluation('module.Experiments', true).value).toBe(false)
    expect(provider.resolveBooleanEvaluation('permission.billing.write', false).value).toBe(true)
    expect(provider.resolveBooleanEvaluation('permission.billing.read', false).value).toBe(false)
    expect(provider.resolveBooleanEvaluation('role.admin', false).value).toBe(true)
    expect(provider.resolveBooleanEvaluation('new-checkout', false).value).toBe(true)
    expect(provider.resolveBooleanEvaluation('unknown-flag', false)).toMatchObject({ value: false, reason: 'DEFAULT' })
    expect(provider.resolveStringEvaluation('ForecastAlgorithm', 'control')).toMatchObject({ value: 'neural', variant: 'neural' })
    expect(provider.resolveStringEvaluation('missing', 'control')).toMatchObject({ value: 'control', reason: 'DEFAULT' })

    const caps = sessionModule.createSessionCapabilities(snapshot)
    expect(caps.isModuleEnabled('Billing')).toBe(true)
    expect(caps.hasPermission('billing.write')).toBe(true)
    expect(caps.getVariant('ForecastAlgorithm')).toBe('neural')
    expect(sessionModule.Keys.module('Billing')).toBe('module.Billing')
  })

  it('omits the session client when the schema does not expose elarion.session', () => {
    const generated = generateRpcClientFiles(rpcClientTestSchema())
    expect(generated.sessionClientSource).toBeUndefined()
    expect(generated.sessionClientFileName).toBeUndefined()
  })

  it('emits an opt-in TanStack Start adapter, keeping the core client framework-neutral', () => {
    const generated = generateRpcClientFiles(rpcClientTestSchema(), {
      generatedBy: 'test-generator',
      sourceLabel: 'rpc.json',
      framework: 'tanstack-start',
    })

    expect(generated.frameworkAdapterFileName).toBe('start-adapter.ts')
    expect(generated.frameworkAdapterSource).toBeDefined()
    const source = generated.frameworkAdapterSource as string

    // Framework-specific imports live in the adapter, never in the core client.
    expect(source).toContain("import { createIsomorphicFn } from '@tanstack/react-start'")
    expect(source).toContain("import { getRequestHeader } from '@tanstack/react-start/server'")
    expect(source).toContain("from './rpc-client.js'")
    expect(source).toContain('export const forwardRequestCookie')
    expect(source).toContain('export function createStartRpcApi')
    expect(source).toContain("getRequestHeader('cookie')")
    expect(generated.clientSource).not.toContain('@tanstack')

    // The emitted adapter is syntactically valid TypeScript (it strips to runnable JS). It can't be
    // runtime-loaded here because @tanstack/react-start is a consumer-side peer dependency.
    const transpiled = ts.transpileModule(source, {
      reportDiagnostics: true,
      compilerOptions: { module: ts.ModuleKind.ES2022, target: ts.ScriptTarget.ES2022 },
    })
    expect(transpiled.diagnostics ?? []).toHaveLength(0)
    expect(transpiled.outputText).toContain('forwardRequestCookie')
  })

  it('points the adapter at a renamed client file and honors a custom adapter filename', () => {
    const generated = generateRpcClientFiles(rpcClientTestSchema(), {
      framework: 'tanstack-start',
      clientFileName: 'client/rpc.ts',
      frameworkAdapterFileName: 'client/start.ts',
    })

    expect(generated.frameworkAdapterFileName).toBe('client/start.ts')
    expect(generated.frameworkAdapterSource).toContain("from './client/rpc.js'")
  })

  it('omits the framework adapter by default', () => {
    const generated = generateRpcClientFiles(rpcClientTestSchema())
    expect(generated.frameworkAdapterSource).toBeUndefined()
    expect(generated.frameworkAdapterFileName).toBeUndefined()
  })

  it('emits typed vocabulary constants from the schema capabilities block', async () => {
    const schema = sessionSchema()
    schema.capabilities = {
      modules: {
        Clients: { features: ['client-portal-v2', 'bulk-import'] },
        Invoicing: { features: ['late-fees'] },
      },
      permissions: [
        { permission: 'clients.read', resource: 'clients', verb: 'read' },
        { permission: 'clients.write', resource: 'clients', verb: 'write' },
        { permission: 'invoices.read', resource: 'invoices', verb: 'read' },
      ],
      roles: ['billing-admin'],
    }

    const generated = generateRpcClientFiles(schema, { generatedBy: 'test', sourceLabel: 'session.json' })
    const source = generated.sessionClientSource as string

    // Const objects with literal-union type aliases — a typo in a capability check is a compile error.
    expect(source).toContain('export const Modules = {')
    expect(source).toContain('"Clients": "Clients",')
    expect(source).toContain('export type ModuleName = (typeof Modules)[keyof typeof Modules]')
    expect(source).toContain('export const Flags = {')
    expect(source).toContain('"bulk-import": "bulk-import",')
    expect(source).toContain('export const Permissions = {')
    expect(source).toContain('"read": "clients.read",')
    expect(source).toContain(
      'export type PermissionName = "clients.read" | "clients.write" | "invoices.read"'
    )
    expect(source).toContain('export const Roles = {')
    // Still self-contained.
    expect(source).not.toContain('import ')

    // The vocabulary is real runtime data, usable as lookup constants.
    const sessionModule = (await loadGeneratedSessionClient(source)) as SessionClientModule & {
      Modules: Record<string, string>
      Permissions: Record<string, Record<string, string>>
    }
    expect(sessionModule.Modules.Clients).toBe('Clients')
    expect(sessionModule.Permissions.clients.write).toBe('clients.write')
    expect(sessionModule.Keys.permission(sessionModule.Permissions.clients.read)).toBe(
      'permission.clients.read'
    )
  })

  it('falls back to string aliases when the schema has no capabilities block', () => {
    const generated = generateRpcClientFiles(sessionSchema(), { generatedBy: 'test', sourceLabel: 'session.json' })
    const source = generated.sessionClientSource as string

    expect(source).toContain('export type ModuleName = string')
    expect(source).toContain('export type FlagName = string')
    expect(source).toContain('export type PermissionName = string')
    expect(source).toContain('export type RoleName = string')
    expect(source).not.toContain('export const Modules')
    expect(source).not.toContain('export const Permissions')
  })

  it('starts one span per batch and treats per-item errors as data, not span failures', async () => {
    const generated = generateRpcClientFiles(rpcClientTestSchema())
    const clientModule = await loadGeneratedClient(generated.clientSource)

    const events: string[] = []
    const ids = ['request-1', 'request-2']

    const rpc = clientModule.createRpcApi({
      url: 'https://example.test/rpc',
      fetch: async () => jsonResponse([
        { jsonrpc: '2.0', id: 'request-1', result: 3 },
        { jsonrpc: '2.0', id: 'request-2', error: { code: -32601, message: 'Method not found' } },
      ]),
      idGenerator: () => ids.shift() ?? 'unexpected',
      instrumentation: {
        startSpan(context) {
          events.push(`start:${context.batch}:${context.methods.length}`)
          return {
            setError() {
              events.push('error')
            },
            end() {
              events.push('end')
            },
          }
        },
      },
    })

    const results = await rpc.$batch([
      rpc.$request.math.add({ left: 1, right: 2 }),
      rpc.$request.math.add({ left: 3, right: 4 }),
    ] as const)

    expect(results[0]).toMatchObject({ ok: true, result: 3 })
    expect(results[1]).toMatchObject({ ok: false, error: { code: -32601 } })
    expect(events).toEqual(['start:true:2', 'end'])
  })

  it('attaches an idempotency key to idempotent operations by default, and honors overrides', async () => {
    const metaKey = 'dev.wimmesberger.elarion/idempotencyKey'
    const schema = rpcClientTestSchema()
    schema.methods['math.add'].idempotent = true

    const generated = generateRpcClientFiles(schema)
    expect(generated.clientSource).toContain('rpcIdempotentMethods')
    expect(generated.clientSource).toContain(metaKey)

    const clientModule = await loadGeneratedClient(generated.clientSource)
    const bodies: Array<{ method: string; params: Record<string, unknown> }> = []
    const fetchImpl: FetchLike = async (_input, init) => {
      const request = JSON.parse(String(init?.body))
      const items = Array.isArray(request) ? request : [request]
      for (const item of items) bodies.push(item)
      const respond = (item: { id: string; method: string }) =>
        ({ jsonrpc: '2.0', id: item.id, result: item.method === 'math.add' ? 3 : { id: 'u', name: 'n' } })
      return jsonResponse(Array.isArray(request) ? request.map(respond) : respond(request))
    }

    const meta = (index: number) => bodies[index].params._meta as Record<string, string> | undefined

    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const rpc = clientModule.createRpcApi({ url: 'https://example.test/rpc', fetch: fetchImpl }) as any

    await rpc.math.add({ left: 1, right: 2 })
    expect(meta(0)?.[metaKey]).toEqual(expect.any(String))
    expect((meta(0)?.[metaKey] ?? '').length).toBeGreaterThan(0)

    // A non-idempotent operation never gets a key.
    await rpc.user.get({ id: 'x' })
    expect(meta(1)).toBeUndefined()

    // A caller-supplied key (stable across a retry layer's retries) is used verbatim.
    await rpc.math.add({ left: 1, right: 2 }, { idempotencyKey: 'fixed-key' })
    expect(meta(2)?.[metaKey]).toBe('fixed-key')

    // `false` disables the key for a single call.
    await rpc.math.add({ left: 1, right: 2 }, { idempotencyKey: false })
    expect(meta(3)).toBeUndefined()

    // Each batch item gets its own key (batch-correct, per-call granularity).
    await rpc.$batch([rpc.$request.math.add({ left: 1, right: 2 }), rpc.$request.math.add({ left: 3, right: 4 })] as const)
    expect(meta(4)?.[metaKey]).toEqual(expect.any(String))
    expect(meta(5)?.[metaKey]).toEqual(expect.any(String))
    expect(meta(4)?.[metaKey]).not.toBe(meta(5)?.[metaKey])

    // Globally disabling opts every operation out.
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const off = clientModule.createRpcApi({ url: 'https://example.test/rpc', fetch: fetchImpl, idempotency: { enabled: false } }) as any
    await off.math.add({ left: 1, right: 2 })
    expect(meta(6)).toBeUndefined()
  })

  it('pre-validates request params against the generated params schemas', async () => {
    const generated = generateRpcClientFiles(rpcClientTestSchema())
    const clientModule = await loadGeneratedClient(generated.clientSource)
    let fetchCalls = 0

    const fetchImpl: FetchLike = async (_input, init) => {
      fetchCalls += 1
      const request = JSON.parse(String(init?.body)) as { id: string }
      return jsonResponse({ jsonrpc: '2.0', id: request.id, result: 3 })
    }

    const rpc = clientModule.createRpcApi({ url: 'https://example.test/rpc', fetch: fetchImpl })

    await expect(rpc.math.add({ left: 1, right: 2 })).resolves.toBe(3)
    expect(fetchCalls).toBe(1)

    // Invalid params fail locally with a descriptive error and never reach the wire.
    await expect(rpc.math.add({ left: 'one', right: 2 })).rejects.toMatchObject({
      name: 'RpcParamsValidationError',
      method: 'math.add',
      message: expect.stringContaining('math.add'),
    })
    expect(fetchCalls).toBe(1)

    // An invalid batch item fails the whole batch locally before anything is sent.
    await expect(rpc.$batch([
      rpc.$request.math.add({ left: 1, right: 2 }),
      rpc.$request.math.add({ left: 'oops', right: 4 }),
    ] as const)).rejects.toMatchObject({
      name: 'RpcParamsValidationError',
      method: 'math.add',
    })
    expect(fetchCalls).toBe(1)

    // validateParams: false opts out of pre-flight validation entirely.
    const unchecked = clientModule.createRpcApi({
      url: 'https://example.test/rpc',
      fetch: fetchImpl,
      validateParams: false,
    })
    await expect(unchecked.math.add({ left: 'one', right: 2 })).resolves.toBe(3)
    expect(fetchCalls).toBe(2)
  })
})

interface SessionClientModule {
  createElarionOpenFeatureProvider(snapshot: unknown): {
    runsOn: string
    resolveBooleanEvaluation(flagKey: string, defaultValue: boolean): { value: boolean; reason: string }
    resolveStringEvaluation(flagKey: string, defaultValue: string): { value: string; variant?: string; reason: string }
  }
  createSessionCapabilities(snapshot: unknown): {
    isModuleEnabled(name: string): boolean
    hasPermission(permission: string): boolean
    getVariant(name: string): string | undefined
  }
  Keys: { module(name: string): string; permission(permission: string): string; role(role: string): string }
}

function sessionSchema(): RpcSchema {
  return {
    methods: {
      'elarion.session': {
        params: { type: 'object', properties: {} },
        result: { type: 'object', properties: {} },
      },
    },
  }
}

async function loadGeneratedSessionClient(source: string): Promise<SessionClientModule> {
  const dir = mkdtempSync(join(tmpdir(), 'elarion-session-client-'))
  const jsSource = ts.transpileModule(source, {
    compilerOptions: {
      module: ts.ModuleKind.ES2022,
      target: ts.ScriptTarget.ES2022,
    },
  }).outputText

  writeFileSync(join(dir, 'session-client.mjs'), jsSource, 'utf-8')
  return await import(pathToFileURL(join(dir, 'session-client.mjs')).href) as SessionClientModule
}

function rpcClientTestSchema(): RpcSchema {
  return {
    methods: {
      'math.add': {
        params: {
          type: 'object',
          properties: {
            left: { type: 'number' },
            right: { type: 'number' },
          },
          required: ['left', 'right'],
        },
        result: { type: 'number' },
      },
      'user.get': {
        params: {
          type: 'object',
          properties: {
            id: { type: 'string' },
          },
          required: ['id'],
        },
        result: {
          type: 'object',
          properties: {
            id: { type: 'string' },
            name: { type: 'string' },
          },
          required: ['id', 'name'],
        },
      },
    },
  }
}

function fileClientTestSchema(): RpcSchema {
  const fileSchema = {
    type: 'object',
    'x-elarion-file': true,
    description: 'A binary file payload; data is the base64-encoded content.',
    properties: {
      contentType: { type: 'string' },
      fileName: { type: 'string' },
      data: { type: 'string', format: 'byte' },
    },
    required: ['contentType', 'data'],
  } satisfies JsonSchema

  return {
    methods: {
      'files.roundTrip': {
        params: {
          type: 'object',
          properties: {
            container: { type: 'string' },
            required: fileSchema,
            file: fileSchema,
            attachments: { type: 'array', items: fileSchema },
          },
          required: ['container', 'required'],
        },
        result: fileSchema,
      },
    },
  }
}

async function loadGeneratedFileClient(clientSource: string): Promise<GeneratedClientModule> {
  const dir = mkdtempSync(join(tmpdir(), 'elarion-rpc-file-client-'))
  const jsSource = ts.transpileModule(clientSource, {
    compilerOptions: {
      module: ts.ModuleKind.ES2022,
      target: ts.ScriptTarget.ES2022,
    },
  }).outputText

  writeFileSync(join(dir, 'rpc-client.mjs'), jsSource, 'utf-8')
  // Schemas validate the user-facing shape: a required File on params, a File as the result (the client
  // encodes params after validation and decodes results before validation).
  writeFileSync(join(dir, 'rpc-schemas.js'), [
    'export const rpcParamsSchemas = {',
    "  'files.roundTrip': {",
    '    parse(value) {',
    "      if (typeof value !== 'object' || value === null) throw new Error('Expected params object')",
    "      if (!(value.required instanceof File)) throw new Error('Expected required to be a File')",
    '      return value',
    '    }',
    '  },',
    '}',
    '',
    'export const rpcResultSchemas = {',
    "  'files.roundTrip': {",
    '    parse(value) {',
    "      if (!(value instanceof File)) throw new Error('Expected a File result')",
    '      return value',
    '    }',
    '  },',
    '}',
    '',
  ].join('\n'), 'utf-8')

  return await import(pathToFileURL(join(dir, 'rpc-client.mjs')).href) as GeneratedClientModule
}

async function loadGeneratedClient(clientSource: string): Promise<GeneratedClientModule> {
  const dir = mkdtempSync(join(tmpdir(), 'elarion-rpc-client-'))
  const jsSource = ts.transpileModule(clientSource, {
    compilerOptions: {
      module: ts.ModuleKind.ES2022,
      target: ts.ScriptTarget.ES2022,
    },
  }).outputText

  writeFileSync(join(dir, 'rpc-client.mjs'), jsSource, 'utf-8')
  writeFileSync(join(dir, 'rpc-schemas.js'), [
    'function numberSchema() {',
    '  return {',
    '    parse(value) {',
    "      if (typeof value !== 'number') throw new Error('Expected number')",
    '      return value',
    '    }',
    '  }',
    '}',
    '',
    'function userSchema() {',
    '  return {',
    '    parse(value) {',
    "      if (typeof value !== 'object' || value === null || Array.isArray(value)) throw new Error('Expected object')",
    '      return value',
    '    }',
    '  }',
    '}',
    '',
    'function mathAddParamsSchema() {',
    '  return {',
    '    parse(value) {',
    "      if (typeof value !== 'object' || value === null || Array.isArray(value)) throw new Error('Expected params object')",
    "      if (typeof value.left !== 'number' || typeof value.right !== 'number') throw new Error('Expected numeric left and right')",
    '      return value',
    '    }',
    '  }',
    '}',
    '',
    'function userGetParamsSchema() {',
    '  return {',
    '    parse(value) {',
    "      if (typeof value !== 'object' || value === null || typeof value.id !== 'string') throw new Error('Expected string id')",
    '      return value',
    '    }',
    '  }',
    '}',
    '',
    'export const rpcParamsSchemas = {',
    "  'math.add': mathAddParamsSchema(),",
    "  'user.get': userGetParamsSchema(),",
    '}',
    '',
    'export const rpcResultSchemas = {',
    "  'math.add': numberSchema(),",
    "  'user.get': userSchema(),",
    '}',
    '',
  ].join('\n'), 'utf-8')

  return await import(pathToFileURL(join(dir, 'rpc-client.mjs')).href) as GeneratedClientModule
}

function jsonResponse(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { 'Content-Type': 'application/json' },
  })
}

describe('client events (ADR-0043)', () => {
  it('emits the events client and payload schemas when the schema declares events', () => {
    const generated = generateRpcClientFiles(eventsTestSchema())

    expect(generated.eventsClientFileName).toBe('events-client.ts')
    const source = generated.eventsClientSource as string
    expect(source).toContain('ElarionEventTopicApi<"invoicing.invoiceChanged">')
    expect(source).toContain('"invoicing.invoiceChanged",')
    expect(generated.schemasSource).toContain('export const rpcEventPayloadSchemas = {')
    expect(generated.schemasSource).toContain('"invoicing.invoiceChanged"')
  })

  it('omits the events client and stays byte-identical when the schema declares no events', () => {
    const without = generateRpcClientFiles(rpcClientTestSchema())
    const withEmptyEvents = generateRpcClientFiles({ ...rpcClientTestSchema(), events: {} })

    expect(without.eventsClientSource).toBeUndefined()
    expect(withEmptyEvents.eventsClientSource).toBeUndefined()
    expect(withEmptyEvents.typesSource).toBe(without.typesSource)
    expect(withEmptyEvents.schemasSource).toBe(without.schemasSource)
    expect(withEmptyEvents.clientSource).toBe(without.clientSource)
  })

  it('subscribes over one EventSource, validates payloads, and dispatches to typed handlers', async () => {
    const generated = generateRpcClientFiles(eventsTestSchema())
    const eventsModule = await loadGeneratedEventsClient(generated.eventsClientSource as string)
    const sources: FakeEventSource[] = []
    const events = eventsModule.createElarionEvents({
      url: '/events',
      eventSource: (url) => {
        const source = new FakeEventSource(url)
        sources.push(source)
        return source
      },
    })

    const received: unknown[] = []
    let connectedCount = 0
    events.$client.onConnected(() => {
      connectedCount += 1
    })
    const unsubscribe = events['invoicing']['invoiceChanged'].subscribe((payload) => received.push(payload))

    await microtasks()
    expect(sources).toHaveLength(1)
    const decodedUrl = decodeURIComponent(sources[0].url)
    expect(decodedUrl).toContain('subscriptions=')
    expect(decodedUrl).toContain('"topic":"invoicing.invoiceChanged"')

    sources[0].emit('elarion.connected', '{}')
    expect(connectedCount).toBe(1)

    sources[0].emit('invoicing.invoiceChanged', JSON.stringify({ invoiceId: 'inv-1' }))
    expect(received).toEqual([{ invoiceId: 'inv-1' }])

    // Dropping the last subscription closes the connection without opening a new one.
    unsubscribe()
    await microtasks()
    expect(sources[0].closed).toBe(true)
    expect(sources).toHaveLength(1)
  })

  it('drops invalid payloads and reports them via onEventError', async () => {
    const generated = generateRpcClientFiles(eventsTestSchema())
    const eventsModule = await loadGeneratedEventsClient(generated.eventsClientSource as string)
    const sources: FakeEventSource[] = []
    const errors: Array<{ topic: string; error: unknown }> = []
    const events = eventsModule.createElarionEvents({
      url: '/events',
      eventSource: (url) => {
        const source = new FakeEventSource(url)
        sources.push(source)
        return source
      },
      onEventError: (topic, error) => errors.push({ topic, error }),
    })

    const received: unknown[] = []
    events['invoicing']['invoiceChanged'].subscribe((payload) => received.push(payload))
    await microtasks()

    sources[0].emit('invoicing.invoiceChanged', JSON.stringify({ wrong: true }))

    expect(received).toEqual([])
    expect(errors).toHaveLength(1)
    expect(errors[0].topic).toBe('invoicing.invoiceChanged')
  })

  it('reconnects with the resource scope when a resource subscription is added', async () => {
    const generated = generateRpcClientFiles(eventsTestSchema())
    const eventsModule = await loadGeneratedEventsClient(generated.eventsClientSource as string)
    const sources: FakeEventSource[] = []
    const events = eventsModule.createElarionEvents({
      url: '/events',
      eventSource: (url) => {
        const source = new FakeEventSource(url)
        sources.push(source)
        return source
      },
    })

    events['invoicing']['invoiceChanged'].subscribe(() => {})
    await microtasks()
    expect(sources).toHaveLength(1)

    events.$client.subscribe('invoicing.invoiceChanged', { resource: 'customer:42' }, () => {})
    await microtasks()

    expect(sources).toHaveLength(2)
    expect(sources[0].closed).toBe(true)
    expect(decodeURIComponent(sources[1].url)).toContain('"resource":"customer:42"')
  })
})

class FakeEventSource {
  readonly listeners = new Map<string, Array<(event: { data?: unknown }) => void>>()
  closed = false

  constructor(readonly url: string) {}

  addEventListener(type: string, listener: (event: { data?: unknown }) => void): void {
    const list = this.listeners.get(type) ?? []
    list.push(listener)
    this.listeners.set(type, list)
  }

  close(): void {
    this.closed = true
  }

  emit(type: string, data: string): void {
    for (const listener of this.listeners.get(type) ?? []) {
      listener({ data })
    }
  }
}

interface EventsTopicApiForTests {
  subscribe(handler: (payload: unknown) => void): () => void
  subscribe(options: { resource?: string }, handler: (payload: unknown) => void): () => void
}

interface EventsClientForTests {
  subscribe(topic: string, handler: (payload: unknown) => void): () => void
  subscribe(topic: string, options: { resource?: string }, handler: (payload: unknown) => void): () => void
  onConnected(handler: () => void): () => void
  close(): void
}

interface EventsClientModule {
  createElarionEvents(options: {
    url: string
    eventSource?: (url: string) => FakeEventSource
    validateEvents?: boolean
    onEventError?: (topic: string, error: unknown) => void
  }): { $client: EventsClientForTests } & Record<string, Record<string, EventsTopicApiForTests>>
}

function eventsTestSchema(): RpcSchema {
  return {
    methods: {
      'math.add': {
        params: {
          type: 'object',
          properties: {
            left: { type: 'number' },
            right: { type: 'number' },
          },
          required: ['left', 'right'],
        },
        result: { type: 'number' },
      },
    },
    events: {
      'invoicing.invoiceChanged': {
        payload: {
          type: 'object',
          properties: {
            invoiceId: { type: 'string' },
          },
          required: ['invoiceId'],
        },
      },
    },
  }
}

async function loadGeneratedEventsClient(eventsClientSource: string): Promise<EventsClientModule> {
  const dir = mkdtempSync(join(tmpdir(), 'elarion-events-client-'))
  const jsSource = ts.transpileModule(eventsClientSource, {
    compilerOptions: {
      module: ts.ModuleKind.ES2022,
      target: ts.ScriptTarget.ES2022,
    },
  }).outputText

  writeFileSync(join(dir, 'events-client.mjs'), jsSource, 'utf-8')
  // The payload-schema stub mirrors what the generated rpc-schemas.ts exports for the events block.
  writeFileSync(join(dir, 'rpc-schemas.js'), [
    'export const rpcEventPayloadSchemas = {',
    "  'invoicing.invoiceChanged': {",
    '    parse(value) {',
    "      if (typeof value !== 'object' || value === null || typeof value.invoiceId !== 'string') throw new Error('Expected invoiceId string')",
    '      return value',
    '    }',
    '  },',
    '}',
    '',
  ].join('\n'), 'utf-8')

  return await import(pathToFileURL(join(dir, 'events-client.mjs')).href) as EventsClientModule
}

function microtasks(): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, 0))
}

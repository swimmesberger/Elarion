import { describe, expect, it } from 'vitest'
import { mkdtempSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { pathToFileURL } from 'node:url'
import ts from 'typescript'
import {
  generateRpcClientFiles,
  UnsupportedJsonSchemaError,
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

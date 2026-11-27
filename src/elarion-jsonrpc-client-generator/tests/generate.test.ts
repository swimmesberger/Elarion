import {describe, expect, it} from 'vitest'
import {mkdtempSync, writeFileSync} from 'node:fs'
import {tmpdir} from 'node:os'
import {join} from 'node:path'
import {pathToFileURL} from 'node:url'
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
    headers?: HeadersInit | ((context: {
      methods: readonly string[];
      batch: boolean
    }) => HeadersInit | Promise<HeadersInit>)
    idGenerator?: () => string | number
    validateParams?: boolean
    validateResults?: boolean
    transformResult?: (method: string, result: unknown) => unknown
    instrumentation?: TestInstrumentation
  }): RpcApiForTests

  createRpcClient(options: {
    url: string
    fetch?: FetchLike
    headers?: HeadersInit | ((context: {
      methods: readonly string[];
      batch: boolean
    }) => HeadersInit | Promise<HeadersInit>)
    idGenerator?: () => string | number
    validateParams?: boolean
    validateResults?: boolean
    transformResult?: (method: string, result: unknown) => unknown
    instrumentation?: TestInstrumentation
  }): RpcClientForTests

  RpcError: new (rpcCode: number, message: string, data?: unknown) => Error & {
    code: string
    rpcCode: number
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
  RpcParamsValidationError: new (method: string, cause: unknown) => Error & {
    method: string
    cause?: unknown
    fieldErrors: Record<string, string[]>
  }
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
              id: {type: 'string', format: 'uuid'},
            },
            required: ['id'],
          },
          result: {
            type: 'object',
            properties: {
              ok: {type: 'boolean'},
            },
            required: ['ok'],
          },
        },
        'a.first': {
          params: {
            type: 'object',
            properties: {
              name: {type: 'string'},
              nickname: {type: ['string', 'null']},
              tags: {
                type: 'array',
                items: {type: 'string'},
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
              count: {type: 'integer'},
              maybe: {type: ['number', 'null']},
              state: {enum: ['Open', 'Closed', null]},
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
    expect(generated.clientSource).toContain("import { rpcErrorDataSchemas, rpcParamsSchemas, rpcResultSchemas } from './rpc-schemas.js'")
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
                items: {type: ['string', 'null']},
              },
            },
            required: ['tags'],
          },
          result: {
            type: 'array',
            items: {type: ['number', 'null']},
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

  it('makes an unrequired nullable enum an optional key, as the server exports C# Nullable<TEnum>', () => {
    // A string-enum converter exports a nullable enum as a bare value list with no "type" keyword; the
    // server leaves it out of "required" (ADR-0082), so callers may omit the key entirely.
    const schema = {
      methods: {
        'meters.create': {
          params: {
            type: 'object',
            properties: {
              label: {type: 'string'},
              medium: {enum: ['Water', 'Power', null]},
            },
            required: ['label'],
          },
          result: {type: 'boolean'},
        },
      },
    } satisfies RpcSchema

    const generated = generateRpcClientFiles(schema)

    expect(generated.typesSource).toContain('medium?: ("Water" | "Power") | null | undefined')
    expect(generated.schemasSource).toContain('medium: z.enum(["Water", "Power"]).nullish().optional(),')
  })

  it('maps a C# union (anyOf, as System.Text.Json 11 exports it) to a TypeScript union and a Zod union', () => {
    // The exact shape JsonSchemaExporter produces for `union ProductLookup(Found, Missing)`: object cases without
    // their own "type", the cases' JSON types (plus null) collected on the union node.
    const foundCase = {
      properties: {name: {type: 'string'}, stock: {type: 'integer'}},
      required: ['name', 'stock'],
    }
    const missingCase = {
      properties: {reason: {type: 'integer'}, retired: {type: 'boolean', default: false}},
      required: ['reason'],
    }
    const schema = {
      methods: {
        'products.lookup': {
          params: {
            type: 'object',
            properties: {
              id: {type: 'integer'},
              filter: {anyOf: [{type: 'integer'}, {type: ['string', 'null']}]},
            },
            required: ['id', 'filter'],
          },
          result: {
            type: 'object',
            properties: {
              result: {type: ['object', 'null'], anyOf: [foundCase, missingCase]},
              history: {type: 'array', items: {type: ['object', 'null'], anyOf: [foundCase, missingCase]}},
            },
            required: ['history'],
          },
        },
      },
    } satisfies RpcSchema

    const generated = generateRpcClientFiles(schema)

    expect(generated.typesSource).toContain('filter: number | string | null | undefined')
    expect(generated.typesSource).toMatch(/result\?: \{\n\s+name: string\n\s+stock: number\n\s+\} \| \{\n\s+reason: number\n\s+retired\?: boolean\n\s+\} \| null \| undefined/)
    // A union element type is parenthesized inside an array.
    expect(generated.typesSource).toMatch(/history: \(\{[\s\S]+\| null \| undefined\)\[\]/)
    expect(generated.schemasSource).toContain('filter: z.union([z.number().int(), z.string()]).nullish(),')
    // Object cases are strict, like the server's structural classifier: a payload carrying another case's
    // properties matches no case instead of being stripped into the first one.
    expect(generated.schemasSource).toMatch(/result: z\.union\(\[z\.strictObject\(\{\n\s+name: z\.string\(\),\n\s+stock: z\.number\(\)\.int\(\),\n\s+\}\), z\.strictObject\(\{\n\s+reason: z\.number\(\)\.int\(\),\n\s+retired: z\.boolean\(\)\.optional\(\),\n\s+\}\)\]\)\.nullish\(\)\.optional\(\),/)
    expect(generated.schemasSource).not.toContain('z.unknown()')
  })

  it('resolves $ref union cases and collapses a single remaining case', () => {
    // Local pointers resolve against the method schema, like the `#/properties/...` references STJ emits.
    const schema = {
      methods: {
        'products.find': {
          params: {type: 'object', properties: {}},
          result: {
            anyOf: [
              {type: 'object', properties: {name: {type: 'string'}}, required: ['name']},
              {$ref: '#/anyOf/0'},
              {type: 'null'},
            ],
          },
        },
      },
    } satisfies RpcSchema

    const generated = generateRpcClientFiles(schema)

    // The duplicate collapses and the null-only alternative only makes the union nullable.
    expect(generated.typesSource).toMatch(/"products\.find": \{\n\s+params: \{\n\s+\}\n\s+result: \{\n\s+name: string\n\s+\}/)
    expect(generated.schemasSource).toContain('z.strictObject({')
    expect(generated.schemasSource).not.toContain('z.union(')
  })

  it('rejects an x-elarion-file payload inside a union case', () => {
    const schema = {
      methods: {
        'files.pick': {
          params: {type: 'object', properties: {}},
          result: {
            anyOf: [
              {type: 'object', 'x-elarion-file': true, properties: {data: {type: 'string'}}},
              {type: 'string'},
            ],
          },
        },
      },
    } satisfies RpcSchema

    expect(() => generateRpcClientFiles(schema)).toThrow(UnsupportedJsonSchemaError)
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
                items: {enum: ['red', 'green']},
              },
            },
            required: ['colors'],
          },
          result: {type: 'boolean'},
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
          params: {type: 'object', properties: {}},
          result: {
            type: 'object',
            properties: {
              a: {$ref: '#/properties/b'},
              b: {
                type: 'object',
                properties: {
                  back: {$ref: '#/properties/a'},
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
          params: {type: 'object', properties: {}},
          result: {type: 'boolean'},
        },
        'B.upper': {
          params: {type: 'object', properties: {}},
          result: {type: 'boolean'},
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
              id: {type: 'string'},
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
              name: {type: 'string', minLength: 3, maxLength: 100},
              slug: {type: 'string', pattern: '^[a-z0-9-]+$'},
              path: {type: 'string', pattern: '^/api/v\\d+/users/'},
              email: {type: 'string', format: 'email'},
              website: {type: 'string', format: 'uri'},
              age: {type: 'integer', minimum: 0, maximum: 150},
              score: {type: 'number', exclusiveMinimum: 0, exclusiveMaximum: 1},
              tags: {
                type: 'array',
                items: {type: 'string', minLength: 1},
                minItems: 1,
                maxItems: 10,
              },
              note: {type: ['string', 'null'], minLength: 1, maxLength: 20},
            },
            required: ['name', 'slug', 'path', 'email', 'website', 'age', 'score', 'tags', 'note'],
          },
          result: {type: 'integer', minimum: 1},
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

  it('documents a result member visible only with a permission', () => {
    const schema = {
      methods: {
        'banking.preview': {
          params: {type: 'object', properties: {}},
          result: {
            type: 'object',
            properties: {
              row: {type: 'integer'},
              opening: {type: ['number', 'null'], 'x-elarion-visible-with': 'banking.read'},
              balance: {
                type: ['number', 'null'],
                'x-elarion-visible-with': ['settings.read', 'settings.write', 'banking.read'],
              },
            },
            required: ['row'],
          },
        },
      },
    } satisfies RpcSchema

    const generated = generateRpcClientFiles(schema)

    expect(generated.typesSource).toMatch(
      /\/\*\* Present only for callers holding the `banking\.read` permission\. \*\/\n\s*opening\?: number \| null \| undefined/)
    expect(generated.typesSource).toMatch(
      /\/\*\* Present only for callers holding any of the `settings\.read`, `settings\.write` or `banking\.read` permissions\. \*\/\n\s*balance\?: number \| null \| undefined/)
    expect(generated.typesSource).not.toMatch(/Present only[^\n]*\n\s*row/)
  })

  it('passes explicit constraint error messages to the matching Zod checks', () => {
    const schema = {
      methods: {
        'orders.create': {
          params: {
            type: 'object',
            properties: {
              code: {
                type: 'string',
                minLength: 3,
                maxLength: 20,
                pattern: '^[A-Z]{3}$',
                'x-elarion-error-messages': {
                  maxLength: 'Code must be at most 20 characters.',
                  pattern: 'Code must be three capital letters.',
                },
              },
              email: {type: 'string', format: 'email', 'x-elarion-error-messages': {format: 'Enter a valid address.'}},
              quantity: {
                type: 'integer',
                minimum: 1,
                exclusiveMaximum: 10,
                'x-elarion-error-messages': {minimum: 'At least one.', exclusiveMaximum: 'Fewer than "ten".'},
              },
              lines: {
                type: 'array',
                items: {type: 'string'},
                minItems: 1,
                'x-elarion-error-messages': {minItems: 'Add a line.'},
              },
              mode: {type: 'string', enum: ['fast', 'slow'], 'x-elarion-error-messages': {enum: 'Pick fast or slow.'}},
              level: {type: 'integer', enum: [1, 2], 'x-elarion-error-messages': {enum: 'Pick 1 or 2.'}},
              // [AllowedValues] on a nullable member: the exporter lists null in the enum, because the server lets it pass.
              days: {type: ['integer', 'null'], enum: [30, 90, 365, null]},
              note: {
                type: ['string', 'null'],
                maxLength: 5,
                'x-elarion-error-messages': {maxLength: 'Too long.'},
              },
            },
            required: ['code', 'email', 'quantity', 'lines', 'mode', 'level'],
          },
          result: {type: 'integer'},
        },
      },
    } satisfies RpcSchema

    const generated = generateRpcClientFiles(schema)

    // Only keywords with a message get the extra argument; the min-side bound here has none.
    expect(generated.schemasSource).toContain(
      'code: z.string().min(3).max(20, "Code must be at most 20 characters.")' +
      '.regex(new RegExp("^[A-Z]{3}$"), "Code must be three capital letters."),')
    expect(generated.schemasSource).toContain('email: z.string().email("Enter a valid address."),')
    // Messages are JSON string literals, so quotes inside a message are escaped.
    expect(generated.schemasSource).toContain(
      'quantity: z.number().int().gte(1, "At least one.").lt(10, "Fewer than \\"ten\\"."),')
    expect(generated.schemasSource).toContain('lines: z.array(z.string()).min(1, "Add a line."),')
    expect(generated.schemasSource).toContain('mode: z.enum(["fast", "slow"], { message: "Pick fast or slow." }),')
    expect(generated.schemasSource).toContain(
      'level: z.union([z.literal(1), z.literal(2)], { message: "Pick 1 or 2." }),')
    expect(generated.schemasSource).toContain('note: z.string().max(5, "Too long.").nullish().optional(),')
    expect(generated.schemasSource).toContain(
      'days: z.union([z.literal(30), z.literal(90), z.literal(365)]).nullish().optional(),')
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
                    taxCategoryId: {type: 'string'},
                    netAmount: {type: 'number'},
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
          params: {type: 'object', properties: {}},
          result: {
            oneOf: [{type: 'string'}, {type: 'number'}],
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
        params: {left: 1, right: 2},
      })

      return jsonResponse({jsonrpc: '2.0', id: request.id, result: '3'})
    }

    const rpc = clientModule.createRpcApi({
      url: 'https://example.test/rpc',
      fetch: fetchImpl,
      headers: (context) => {
        seenContexts.push(context)
        return {Authorization: 'Bearer test'}
      },
      idGenerator: () => 'request-1',
      transformResult: (method, result) => method === 'math.add' ? Number(result) : result,
    })

    await expect(
      rpc.math.add({left: 1, right: 2}, {
        signal: abort.signal,
        headers: {'X-Request': 'single'},
      })
    ).resolves.toBe(3)
    expect(seenContexts).toEqual([{methods: ['math.add'], batch: false}])
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

    const upload = new File([new TextEncoder().encode('id;name')], 'clients.csv', {type: 'text/csv'})
    const extra = new File([new TextEncoder().encode('x')], 'extra.bin', {type: 'application/octet-stream'})

    let wireParams: Record<string, unknown> | undefined
    const fetchImpl: FetchLike = async (_input, init) => {
      const request = JSON.parse(String(init?.body)) as { id: string; params: Record<string, unknown> }
      wireParams = request.params
      return jsonResponse({
        jsonrpc: '2.0',
        id: request.id,
        result: {contentType: 'application/pdf', fileName: 'report.pdf', data: btoa('pdf-bytes')},
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
      required: {contentType: 'text/csv', fileName: 'clients.csv', data: btoa('id;name')},
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
        result: {contentType: 'text/plain', data: btoa('ok')},
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
      required: new File([new TextEncoder().encode('raw')], 'raw.txt', {type: 'text/plain'}),
    }) as File

    // Conversion is independent of validation: the wire always carries the envelope, and the caller
    // always receives a File.
    expect(wireParams?.required).toMatchObject({contentType: 'text/plain', data: btoa('raw')})
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

    await expect(transportClient.call('math.add', {left: 1, right: 2}))
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
        error: {code: -32602, message: 'Invalid params', data: {code: 'validation', data: {field: 'left'}}},
      }),
      idGenerator: () => 'request-2',
    })

    await expect(rpcClient.call('math.add', {left: 1, right: 2}))
      .rejects.toMatchObject({
        name: 'RpcError',
        rpcCode: -32602,
        code: 'validation',
        data: {field: 'left'},
      })

    const mismatchedIdClient = clientModule.createRpcClient({
      url: 'https://example.test/rpc',
      fetch: async () => jsonResponse({jsonrpc: '2.0', id: 'different-request', result: 3}),
      idGenerator: () => 'request-3',
    })

    await expect(mismatchedIdClient.call('math.add', {left: 1, right: 2}))
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
        error: {code: -32700, message: 'Parse error'},
      }),
      idGenerator: () => 'request-1',
    })

    await expect(client.call('math.add', {left: 1, right: 2})).rejects.toMatchObject({
      name: 'RpcError',
      rpcCode: -32700,
      code: 'parse_error',
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
        error: {code: -32600, message: 'Batch too large'},
      }),
      idGenerator: () => 'request-1',
    })

    await expect(rpc.$batch([
      rpc.$request.math.add({left: 1, right: 2}),
    ] as const)).rejects.toMatchObject({
      name: 'RpcError',
      rpcCode: -32600,
      code: 'invalid_request',
      message: 'Batch too large',
    })
  })

  it('types declared errors per method and exposes them as discriminated RpcError variants', () => {
    const schema = rpcClientTestSchema()
    schema.methods['math.add'].errors = {
      'math.overflow': {kind: 'business_rule', data: {type: 'object', properties: {limit: {type: 'integer'}}, required: ['limit']}},
      not_found: {kind: 'not_found'},
    }
    const generated = generateRpcClientFiles(schema)

    expect(generated.typesSource).toContain('errors: {')
    expect(generated.typesSource).toContain('"math.overflow": { kind: "business_rule"; data: {')
    expect(generated.typesSource).toContain('"not_found": { kind: "not_found"; data: undefined }')
    expect(generated.typesSource).toContain('errors: {}')
    expect(generated.schemasSource).toContain('export const rpcErrorDataSchemas = {')
    expect(generated.schemasSource).toContain('"math.overflow":')
    expect(generated.clientSource).toContain('export type RpcMethodError<M extends RpcMethod>')
    expect(generated.clientSource).toContain('const rpcErrorCodes')
    expect(generated.clientSource).toContain('"math.add":["math.overflow","not_found"]')
  })

  it('reads the { code, data } wire envelope, validates declared payloads and narrows by code', async () => {
    const generated = generateRpcClientFiles(rpcClientTestSchema())
    const clientModule = await loadGeneratedClient(generated.clientSource)
    const respond = (data: unknown) => clientModule.createRpcClient({
      url: 'https://example.test/rpc',
      fetch: async () => jsonResponse({jsonrpc: '2.0', id: 'r', error: {code: -32004, message: 'too big', data}}),
      idGenerator: () => 'r',
    })

    await expect(respond({code: 'math.overflow', data: {limit: 5}}).call('math.add', {left: 1, right: 2}))
      .rejects.toMatchObject({code: 'math.overflow', rpcCode: -32004, data: {limit: 5}, isBusinessRule: true})

    // A payload that violates the declared contract is a protocol failure, never a silently mistyped error.
    await expect(respond({code: 'math.overflow', data: {limit: 'x'}}).call('math.add', {left: 1, right: 2}))
      .rejects.toMatchObject({name: 'RpcProtocolError'})

    // No envelope (a foreign server): the code falls back to the default code of the numeric kind.
    await expect(respond(undefined).call('math.add', {left: 1, right: 2}))
      .rejects.toMatchObject({code: 'business_rule', rpcCode: -32004})
  })

  it('exposes the support reference of an unexpected error, and none for other errors', async () => {
    const generated = generateRpcClientFiles(rpcClientTestSchema())
    const clientModule = await loadGeneratedClient(generated.clientSource)
    const respond = (error: unknown) => clientModule.createRpcClient({
      url: 'https://example.test/rpc',
      fetch: async () => jsonResponse({jsonrpc: '2.0', id: 'r', error}),
      idGenerator: () => 'r',
    })

    await expect(respond({code: -32603, message: 'Internal error', data: {code: 'internal', reference: 'abc123'}})
      .call('math.add', {left: 1, right: 2}))
      .rejects.toMatchObject({code: 'internal', isInternalError: true, reference: 'abc123', message: 'Internal error'})
    await expect(respond({code: -32001, message: 'missing', data: {code: 'not_found'}})
      .call('math.add', {left: 1, right: 2}))
      .rejects.toMatchObject({code: 'not_found', reference: undefined})
  })

  it('emits normalized per-method requirements, read-only flags and tags as rpcMethodInfo', async () => {
    const schema = rpcClientTestSchema()
    schema.methods['math.add'].readOnly = true
    schema.methods['math.add'].idempotent = true
    schema.methods['math.add'].tags = {viewAs: 'refused', timeout: '180s'}
    schema.methods['math.add'].requires = {
      authenticated: true,
      permissions: ['math.write', 'math.import'],
      roles: ['Clerk'],
      serverChecks: true,
    }
    schema.methods['user.get'].requires = {anonymous: true}
    const generated = generateRpcClientFiles(schema)
    const clientModule = await loadGeneratedClient(generated.clientSource) as unknown as {
      rpcMethodInfo: Record<string, unknown>
    }

    expect(clientModule.rpcMethodInfo).toEqual({
      'math.add': {
        readOnly: true,
        idempotent: true,
        requires: {
          anonymous: false,
          authenticated: true,
          permissions: ['math.import', 'math.write'],
          roles: ['Clerk'],
          serverChecks: true,
        },
        tags: {timeout: '180s', viewAs: 'refused'},
      },
      'user.get': {
        readOnly: false,
        idempotent: false,
        requires: {anonymous: true, authenticated: false, permissions: [], roles: [], serverChecks: false},
        tags: {},
      },
    })
    // Deterministic key order in the emitted literal, so regenerating an unchanged schema is a no-op.
    expect(generated.clientSource).toContain('"tags":{"timeout":"180s","viewAs":"refused"}')
  })

  it('types declared decisions and emits withDecisions only when a method declares one', () => {
    const generated = generateRpcClientFiles(decisionTestSchema())
    const without = generateRpcClientFiles(rpcClientTestSchema())

    expect(generated.typesSource).toMatch(
      /"math\.big_sum": \{ kind: "decision_required"; data: \{\s*limit: number\s*\}; options: "confirm"; perItem: false \}/)
    expect(generated.typesSource).toContain(
      '"math.per_item": { kind: "decision_required"; data: undefined; options: "keep" | "drop"; perItem: true }')
    expect(generated.clientSource).toContain('export async function withDecisions<M extends RpcDecidingMethod>(')
    expect(generated.clientSource).toContain('  client: RpcClient | RpcCall<M>,')
    expect(generated.clientSource).toContain(
      'export function decisionQuestion<M extends RpcDecidingMethod>(method: M, error: unknown): RpcMethodDecision<M> | undefined {')
    expect(generated.clientSource).toContain('export function answerDecision<')
    expect(generated.clientSource).toContain('export type RpcMethodDecisionAnswer<M extends RpcMethod> = RpcDecisionAnswerOf<RpcMethodDecision<M>>')
    expect(generated.clientSource).toContain(
      'const rpcDecisions: Partial<Record<RpcMethod, Readonly<Record<string, { readonly options: readonly string[]; readonly perItem: boolean }>>>> = '
      + '{"math.add":{"math.big_sum":{"options":["confirm"],"perItem":false},"math.per_item":{"options":["keep","drop"],"perItem":true}}}')
    for (const name of ['withDecisions', 'rpcDecisions', 'decisionQuestion', 'answerDecision', 'RpcCall']) {
      expect(without.clientSource).not.toContain(name)
    }
  })

  it('types the decisions member with the declared codes and options the server narrowed it to', () => {
    const generated = generateRpcClientFiles(decisionTestSchema())

    expect(generated.typesSource).toMatch(
      /decisions\?: \(\{\s*code: "math\.big_sum"\s*option\?: "confirm"\s*\} \| \{\s*code: "math\.per_item"\s*item: string\s*option: "keep" \| "drop"\s*\}\)\[\] \| null \| undefined/)
    expect(generated.schemasSource).toMatch(
      /decisions: z\.array\(z\.union\(\[z\.strictObject\(\{\s*code: z\.enum\(\["math\.big_sum"\]\),\s*option: z\.enum\(\["confirm"\]\)\.optional\(\),/)
  })

  it('type-checks the decision primitives against the narrowed decisions member under NodeNext', () => {
    const generated = generateRpcClientFiles(decisionTestSchema())
    const usage = [
      "import { answerDecision, createRpcClient, decisionQuestion, withDecisions } from './rpc-client.js'",
      "import type { RpcMethod, RpcMethodDecision, RpcMethodDecisionAnswer, RpcParams, RpcResult } from './rpc-client.js'",
      '',
      "const client = createRpcClient({ url: 'https://example.test/rpc' })",
      'async function seam<M extends RpcMethod>(method: M, params: RpcParams<M>): Promise<RpcResult<M>> {',
      "  try { return await client.call(method, params) } catch (error) { throw new Error('localized', { cause: error }) }",
      '}',
      '',
      'export async function run(error: unknown): Promise<number> {',
      "  let params: RpcParams<'math.add'> = { left: 1, right: 2 }",
      "  const question = decisionQuestion('math.add', error)",
      '  if (question === undefined) return 0',
      "  if (question.code === 'math.per_item') {",
      "    params = { ...params, decisions: answerDecision(params.decisions, question, { 'line-1': 'drop' }) }",
      '    // @ts-expect-error an option the decision does not declare',
      "    answerDecision(params.decisions, question, { 'line-1': 'maybe' })",
      '  } else {',
      '    const limit: number = question.data.limit',
      "    params = { ...params, decisions: answerDecision(undefined, question, 'confirm') }",
      '    void limit',
      '  }',
      "  const pending: RpcMethodDecision<'math.add'> = question",
      "  const answers: RpcMethodDecisionAnswer<'math.add'>[] = answerDecision([], pending, 'confirm')",
      "  params = { ...params, decisions: [...answers, { code: 'math.big_sum' }] }",
      '  // @ts-expect-error a code the method does not declare',
      "  params = { ...params, decisions: [{ code: 'math.unknown' }] }",
      "  const viaSeam: number = await withDecisions(seam, 'math.add', params, () => 'confirm')",
      "  const viaClient: number = await withDecisions(client, 'math.add', params, () => undefined)",
      '  // @ts-expect-error a method that declares no decision',
      "  decisionQuestion('user.get', error)",
      '  return viaSeam + viaClient',
      '}',
      '',
    ].join('\n')
    // The Zod schemas are not under test here, and zod is no dependency of this package: a stub stands in.
    const schemasStub = [
      'export const rpcParamsSchemas: Record<string, any> = {}',
      'export const rpcResultSchemas: Record<string, any> = {}',
      'export const rpcErrorDataSchemas: Record<string, Record<string, any>> = {}',
      '',
    ].join('\n')

    const diagnostics = typeCheck({
      'rpc-types.ts': generated.typesSource,
      'rpc-client.ts': generated.clientSource,
      'rpc-schemas.ts': schemasStub,
      'usage.ts': usage,
    })

    expect(diagnostics).toEqual([])
  })

  it('recognises a declared question and builds the answers for a resend', async () => {
    const generated = generateRpcClientFiles(decisionTestSchema())
    const clientModule = await loadGeneratedClient(generated.clientSource) as unknown as GeneratedClientModule & {
      decisionQuestion(method: string, error: unknown): {
        code: string; message: string; options: readonly string[]; perItem: boolean; data: unknown; error: unknown
      } | undefined
      answerDecision(decisions: unknown, question: unknown, answer: unknown): unknown[]
    }
    const {RpcError, decisionQuestion, answerDecision} = clientModule
    const bigSum = new RpcError(-32006, 'That is a big sum.', {code: 'math.big_sum', data: {limit: 10}})
    const perItem = new RpcError(-32006, 'Keep these lines?', {code: 'math.per_item'})

    const question = decisionQuestion('math.add', bigSum)
    expect(question).toEqual({
      code: 'math.big_sum', message: 'That is a big sum.', options: ['confirm'], perItem: false, data: {limit: 10}, error: bigSum,
    })
    // An application seam that wraps the error keeps it as the cause.
    expect(decisionQuestion('math.add', new Error('localized', {cause: new Error('wrapped', {cause: bigSum})}))?.error)
      .toBe(bigSum)
    // Not a declared question: another method, an undeclared code, another kind, or no RpcError at all.
    expect(decisionQuestion('user.get', bigSum)).toBeUndefined()
    expect(decisionQuestion('math.add', new RpcError(-32006, '?', {code: 'math.other'}))).toBeUndefined()
    expect(decisionQuestion('math.add', new RpcError(-32006, '?', {code: 'constructor'}))).toBeUndefined()
    expect(decisionQuestion('math.add', new RpcError(-32002, '?', {code: 'math.big_sum'}))).toBeUndefined()
    expect(decisionQuestion('math.add', new Error('offline'))).toBeUndefined()
    expect(decisionQuestion('math.add', 'math.big_sum')).toBeUndefined()

    const confirmed = answerDecision(undefined, question, 'confirm')
    expect(confirmed).toEqual([{code: 'math.big_sum', option: 'confirm'}])
    const lines = answerDecision(
      [...confirmed, {code: 'math.per_item', item: 'line-1', option: 'keep'}, {code: 'math.per_item', item: 'line-3', option: 'keep'}],
      decisionQuestion('math.add', perItem),
      {'line-1': 'drop', 'line-2': 'keep'})
    // A new answer replaces the earlier one for the same code and item; every other answer stays.
    expect(lines).toEqual([
      {code: 'math.big_sum', option: 'confirm'},
      {code: 'math.per_item', item: 'line-3', option: 'keep'},
      {code: 'math.per_item', item: 'line-1', option: 'drop'},
      {code: 'math.per_item', item: 'line-2', option: 'keep'},
    ])
    expect(answerDecision(lines, question, 'confirm')).toEqual([...lines.slice(1), {code: 'math.big_sum', option: 'confirm'}])
  })

  it('runs withDecisions over an application call function instead of the client', async () => {
    const generated = generateRpcClientFiles(decisionTestSchema())
    const clientModule = await loadGeneratedClient(generated.clientSource) as unknown as GeneratedClientModule & {
      withDecisions(
        client: unknown, method: string, params: unknown, ask: (question: { code: string }) => unknown
      ): Promise<unknown>
    }
    const client = clientModule.createRpcClient({
      url: 'https://example.test/rpc',
      fetch: async (_input, init) => {
        const request = JSON.parse(String(init?.body)) as { id: string; params: { decisions?: { code: string }[] } }
        return (request.params.decisions ?? []).some((d) => d.code === 'math.big_sum')
          ? jsonResponse({jsonrpc: '2.0', id: request.id, result: 42})
          : jsonResponse({jsonrpc: '2.0', id: request.id, error: {
            code: -32006, message: 'That is a big sum.', data: {code: 'math.big_sum', data: {limit: 10}},
          }})
      },
      validateParams: false,
      validateResults: false,
    })
    // The application's seam: its own error, with the RpcError as the cause, and no request options.
    const calls: string[] = []
    const seam = async (method: string, params: unknown) => {
      calls.push(method)
      try {
        return await client.call(method, params)
      } catch (error) {
        throw new Error('Localized failure', {cause: error})
      }
    }

    expect(await clientModule.withDecisions(seam, 'math.add', {left: 30, right: 12}, () => 'confirm')).toBe(42)
    expect(calls).toEqual(['math.add', 'math.add'])
    // Cancelling throws RpcDecisionCancelledError, with the seam's own error as its cause.
    const cancelled = await clientModule.withDecisions(seam, 'math.add', {left: 1, right: 2}, () => undefined)
      .catch((error: unknown) => error)
    expect(cancelled).toMatchObject({name: 'RpcDecisionCancelledError', question: {code: 'math.big_sum'}})
    expect((cancelled as Error).cause).toMatchObject({message: 'Localized failure'})
  })

  it('asks the application for a declared decision and resends with the answer', async () => {
    const generated = generateRpcClientFiles(decisionTestSchema())
    const clientModule = await loadGeneratedClient(generated.clientSource) as unknown as GeneratedClientModule & {
      withDecisions(
        client: unknown, method: string, params: unknown,
        ask: (question: { code: string; options: readonly string[]; perItem: boolean; data: unknown }) => unknown,
        options?: { maxQuestions?: number }
      ): Promise<unknown>
    }
    const sent: unknown[] = []
    const client = clientModule.createRpcClient({
      url: 'https://example.test/rpc',
      fetch: async (_input, init) => {
        const request = JSON.parse(String(init?.body)) as { id: string; params: { decisions?: { code: string; item?: string; option: string }[] } }
        sent.push(request.params.decisions)
        const decisions = request.params.decisions ?? []
        if (!decisions.some((d) => d.code === 'math.big_sum')) {
          return jsonResponse({jsonrpc: '2.0', id: request.id, error: {
            code: -32006, message: 'That is a big sum.', data: {code: 'math.big_sum', data: {limit: 10}},
          }})
        }
        if (!decisions.some((d) => d.code === 'math.per_item' && d.item === 'line-2')) {
          return jsonResponse({jsonrpc: '2.0', id: request.id, error: {
            code: -32006, message: 'Keep these lines?', data: {code: 'math.per_item'},
          }})
        }
        return jsonResponse({jsonrpc: '2.0', id: request.id, result: 42})
      },
      idGenerator: () => 'request-1',
      validateParams: false,
      validateResults: false,
    })
    const questions: { code: string; options: readonly string[]; perItem: boolean; data: unknown }[] = []

    const result = await clientModule.withDecisions(client, 'math.add', {left: 30, right: 12}, (question) => {
      questions.push(question)
      return question.perItem ? {'line-1': 'keep', 'line-2': 'drop'} : 'confirm'
    })

    expect(result).toBe(42)
    expect(questions.map((q) => [q.code, q.options, q.perItem, q.data])).toEqual([
      ['math.big_sum', ['confirm'], false, {limit: 10}],
      ['math.per_item', ['keep', 'drop'], true, undefined],
    ])
    // The first call sends the params as given: no empty decisions list.
    expect(sent).toEqual([
      undefined,
      [{code: 'math.big_sum', option: 'confirm'}],
      [
        {code: 'math.big_sum', option: 'confirm'},
        {code: 'math.per_item', item: 'line-1', option: 'keep'},
        {code: 'math.per_item', item: 'line-2', option: 'drop'},
      ],
    ])

    // A cancelled question throws RpcDecisionCancelledError (the decision error is its cause), so a caller tells a
    // cancel from a failure; an undeclared one is never asked.
    const cancelled = await clientModule.withDecisions(client, 'math.add', {left: 1, right: 2}, () => undefined)
      .catch((error: unknown) => error)
    expect(cancelled).toMatchObject({name: 'RpcDecisionCancelledError', question: {code: 'math.big_sum'}})
    expect((cancelled as Error).cause).toMatchObject({code: 'math.big_sum', isDecisionRequired: true})
  })

  it('aborts a request after its timeout with RpcTimeoutError, honouring the timeout tag and per-call overrides', async () => {
    const schema = rpcClientTestSchema()
    schema.methods['user.get'] = {...schema.methods['user.get'], tags: {timeout: '150ms'}}
    const generated = generateRpcClientFiles(schema)
    const clientModule = await loadGeneratedClient(generated.clientSource) as unknown as {
      createRpcClient(options: Record<string, unknown>): {
        call(method: string, params: unknown, options?: Record<string, unknown>): Promise<unknown>
      }
      isRetryable(error: unknown): boolean
    }
    const waited: number[] = []
    // A server that answers only when the request is aborted, after recording how long it waited.
    const hanging: FetchLike = (_input, init) => new Promise((_resolve, reject) => {
      const started = Date.now()
      const abort = () => {
        waited.push(Date.now() - started)
        reject(init?.signal?.reason ?? new Error('aborted'))
      }
      // Like fetch, an already aborted signal rejects at once.
      if (init?.signal?.aborted) abort()
      else init?.signal?.addEventListener('abort', abort)
    })
    const client = clientModule.createRpcClient({
      url: 'https://example.test/rpc', fetch: hanging, timeoutMs: 40, validateParams: false, validateResults: false,
    })

    const timedOut = await client.call('math.add', {left: 1, right: 2}).catch((error: unknown) => error)
    expect(timedOut).toMatchObject({name: 'RpcTimeoutError', timeoutMs: 40, methods: ['math.add']})
    expect(clientModule.isRetryable(timedOut)).toBe(true)

    // The method's timeout tag wins over the client's default.
    expect(await client.call('user.get', {id: '1'}).catch((error: unknown) => error))
      .toMatchObject({name: 'RpcTimeoutError', timeoutMs: 150})

    // A per-call timeout wins over both; the caller's own abort is not a timeout.
    expect(await client.call('user.get', {id: '1'}, {timeoutMs: 20}).catch((error: unknown) => error))
      .toMatchObject({name: 'RpcTimeoutError', timeoutMs: 20})
    const cancel = new AbortController()
    const cancelled = client.call('math.add', {left: 1, right: 2}, {signal: cancel.signal, timeoutMs: false})
      .catch((error: unknown) => error)
    cancel.abort()
    const cancelError = await cancelled
    expect(cancelError).not.toMatchObject({name: 'RpcTimeoutError'})
    expect(clientModule.isRetryable(cancelError)).toBe(false)
    expect(waited.length).toBe(4)
  })

  it('maps every thrown error through mapError, keeping the original reachable as the cause', async () => {
    const generated = generateRpcClientFiles(rpcClientTestSchema())
    const clientModule = await loadGeneratedClient(generated.clientSource) as unknown as {
      createRpcClient(options: Record<string, unknown>): {
        call(method: string, params: unknown): Promise<unknown>
        batch(items: readonly { method: string; params: unknown }[]): Promise<unknown>
      }
      isRetryable(error: unknown): boolean
    }
    const contexts: unknown[] = []
    const client = clientModule.createRpcClient({
      url: 'https://example.test/rpc',
      fetch: async (_input: unknown, init?: RequestInit) => {
        const request = JSON.parse(String(init?.body)) as { id: string } | { id: string }[]
        if (Array.isArray(request)) return new Response('busy', {status: 503, statusText: 'Service Unavailable'})
        return jsonResponse({jsonrpc: '2.0', id: request.id, error: {
          code: -32001, message: 'No such user.', data: {code: 'not_found'},
        }})
      },
      validateResults: false,
      mapError: (error: unknown, context: unknown) => {
        contexts.push(context)
        return new Error('Nicht gefunden.', {cause: error})
      },
    })

    const mapped = await client.call('user.get', {id: '1'}).catch((error: unknown) => error) as Error
    expect(mapped.message).toBe('Nicht gefunden.')
    expect(mapped.cause).toMatchObject({code: 'not_found', isNotFound: true})
    // A decided answer is final, also through the mapped error's cause.
    expect(clientModule.isRetryable(mapped)).toBe(false)

    const batchError = await client.batch([{method: 'math.add', params: {left: 1, right: 2}}])
      .catch((error: unknown) => error) as Error
    expect(batchError.cause).toMatchObject({name: 'RpcTransportError', status: 503})
    expect(clientModule.isRetryable(batchError)).toBe(true)
    expect(contexts).toEqual([{methods: ['user.get'], batch: false}, {methods: ['math.add'], batch: true}])
  })

  it('tells retryable failures from final ones', async () => {
    const generated = generateRpcClientFiles(rpcClientTestSchema())
    const clientModule = await loadGeneratedClient(generated.clientSource) as unknown as {
      isRetryable(error: unknown): boolean
      RpcError: new (rpcCode: number, message: string, data?: unknown) => Error
      RpcTransportError: new (status: number, statusText: string, body: string) => Error
      RpcProtocolError: new (message: string) => Error
    }
    const {isRetryable, RpcError, RpcTransportError, RpcProtocolError} = clientModule

    expect(isRetryable(new RpcError(-32603, 'Internal error', {code: 'internal'}))).toBe(true)
    expect(isRetryable(new RpcError(-32002, 'Busy', {code: 'idempotency.in_progress'}))).toBe(true)
    expect(isRetryable(new TypeError('Failed to fetch'))).toBe(true)
    expect(isRetryable(new RpcTransportError(502, 'Bad Gateway', ''))).toBe(true)
    expect(isRetryable(new RpcTransportError(429, 'Too Many Requests', ''))).toBe(true)

    for (const [rpcCode, code] of [
      [-32602, 'validation'], [-32001, 'not_found'], [-32002, 'conflict'], [-32003, 'forbidden'],
      [-32004, 'business_rule'], [-32005, 'unauthorized'], [-32006, 'decision_required'], [-32601, 'method_not_found'],
    ] as const) {
      expect(isRetryable(new RpcError(rpcCode, 'final', {code}))).toBe(false)
    }
    expect(isRetryable(new RpcTransportError(404, 'Not Found', ''))).toBe(false)
    expect(isRetryable(new RpcProtocolError('bad id'))).toBe(false)
    expect(isRetryable(new TypeError('x is not a function'))).toBe(false)
    expect(isRetryable(undefined)).toBe(false)
  })

  it('type-checks the timeout and error-mapping options under NodeNext', () => {
    const generated = generateRpcClientFiles(rpcClientTestSchema())
    const usage = [
      "import { createRpcClient, isRetryable, RpcTimeoutError } from './rpc-client.js'",
      '',
      'export const client = createRpcClient({',
      "  url: 'https://example.test/rpc',",
      '  timeoutMs: 30_000,',
      "  timeoutFor: (method) => method === 'user.get' ? 180_000 : undefined,",
      "  mapError: (error, context) => new Error(context.methods.join(','), { cause: error }),",
      '})',
      "export const once = client.call('math.add', { left: 1, right: 2 }, { timeoutMs: false })",
      'export const retry = (count: number, error: unknown): boolean =>',
      '  count < 3 && (isRetryable(error) || error instanceof RpcTimeoutError)',
      '',
    ].join('\n')
    const schemasStub = [
      'export const rpcParamsSchemas: Record<string, any> = {}',
      'export const rpcResultSchemas: Record<string, any> = {}',
      'export const rpcErrorDataSchemas: Record<string, Record<string, any>> = {}',
      '',
    ].join('\n')

    expect(typeCheck({
      'rpc-types.ts': generated.typesSource,
      'rpc-client.ts': generated.clientSource,
      'rpc-schemas.ts': schemasStub,
      'usage.ts': usage,
    })).toEqual([])
  })

  it('names the .ts files in its imports with importExtension ts, so plain Node loads the client', async () => {
    const generated = generateRpcClientFiles(decisionTestSchema(), {importExtension: 'ts'})

    expect(generated.clientSource).toContain("from './rpc-types.ts'")
    expect(generated.clientSource).toContain("from './rpc-schemas.ts'")
    expect(generated.clientSource).not.toContain("'./rpc-schemas.js'")
    expect(generateRpcClientFiles(decisionTestSchema()).clientSource).toContain("from './rpc-schemas.js'")

    // Node with type stripping (unflagged since Node 23.6) imports the generated .ts files themselves.
    if (!(process.features as { typescript?: unknown }).typescript) return
    const dir = mkdtempSync(join(tmpdir(), 'elarion-rpc-node-'))
    writeFileSync(join(dir, 'package.json'), JSON.stringify({type: 'module'}), 'utf-8')
    writeFileSync(join(dir, 'rpc-types.ts'), generated.typesSource, 'utf-8')
    writeFileSync(join(dir, 'rpc-client.ts'), generated.clientSource, 'utf-8')
    writeFileSync(join(dir, 'rpc-schemas.ts'), [
      'export const rpcParamsSchemas: Record<string, any> = {}',
      'export const rpcResultSchemas: Record<string, any> = {}',
      'export const rpcErrorDataSchemas: Record<string, Record<string, any>> = {}',
      '',
    ].join('\n'), 'utf-8')
    writeFileSync(join(dir, 'check.ts'), [
      "import { decisionQuestion, RpcError } from './rpc-client.ts'",
      "const error = new RpcError(-32006, 'Big sum', { code: 'math.big_sum', data: { limit: 10 } })",
      "console.log(decisionQuestion('math.add', error)?.code)",
      '',
    ].join('\n'), 'utf-8')
    const {spawnSync} = await import('node:child_process')
    const run = spawnSync(process.execPath, [join(dir, 'check.ts')], {encoding: 'utf-8'})
    expect(run.stderr).not.toContain('Error')
    expect(run.stdout.trim()).toBe('math.big_sum')
  })

  it('propagates a transform result of null instead of falling back to the raw value', async () => {
    const generated = generateRpcClientFiles(rpcClientTestSchema())
    const clientModule = await loadGeneratedClient(generated.clientSource)

    const client = clientModule.createRpcClient({
      url: 'https://example.test/rpc',
      fetch: async (_input, init) => {
        const request = JSON.parse(String(init?.body)) as { id: string }
        return jsonResponse({jsonrpc: '2.0', id: request.id, result: 3})
      },
      idGenerator: () => 'request-1',
      validateResults: false,
      transformResult: () => null,
    })

    await expect(client.call('math.add', {left: 1, right: 2})).resolves.toBeNull()
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
      decisionRequired: -32006,
    })

    // End-to-end: a NotFound error response surfaces on the getter, so a consumer branches without re-wrapping.
    const notFoundClient = clientModule.createRpcClient({
      url: 'https://example.test/rpc',
      fetch: async () => jsonResponse({
        jsonrpc: '2.0',
        id: 'request-1',
        error: {code: clientModule.ElarionErrorCodes.notFound, message: 'Client not found'},
      }),
      idGenerator: () => 'request-1',
    })

    await expect(notFoundClient.call('math.add', {left: 1, right: 2})).rejects.toMatchObject({
      name: 'RpcError',
      rpcCode: -32001,
      code: 'not_found',
      isNotFound: true,
      isConflict: false,
    })

    const cases = [
      {code: -32002, getter: 'isConflict'},
      {code: -32003, getter: 'isForbidden'},
      {code: -32004, getter: 'isBusinessRule'},
      {code: -32005, getter: 'isUnauthorized'},
      {code: -32006, getter: 'isDecisionRequired'},
      {code: -32602, getter: 'isInvalidParams'},
      {code: -32603, getter: 'isInternalError'},
    ] as const
    for (const {code, getter} of cases) {
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
          {jsonrpc: '2.0', id: 'request-2', error: {code: -32601, message: 'Method not found'}},
          {jsonrpc: '2.0', id: 'request-3', result: 'not a number'},
          {jsonrpc: '2.0', id: 'request-1', result: 3},
        ])
      },
      headers: async (context) => {
        seenContexts.push(context)
        return {'X-Batch': String(context.batch)}
      },
      idGenerator: () => ids.shift() ?? 'unexpected',
    })

    const results = await rpc.$batch([
      rpc.$request.math.add({left: 1, right: 2}),
      rpc.$request.user.get({id: 'user-1'}),
      rpc.$request.math.add({left: 3, right: 4}),
    ] as const)

    expect(results[0]).toMatchObject({ok: true, result: 3})
    expect(results[1]).toMatchObject({ok: false, error: {rpcCode: -32601}})
    expect(results[2]).toMatchObject({ok: false, error: {rpcCode: -32603}})
    expect(seenContexts).toEqual([
      {methods: ['math.add', 'user.get', 'math.add'], batch: true},
    ])

    const duplicateIdClient = clientModule.createRpcApi({
      url: 'https://example.test/rpc',
      fetch: async () => jsonResponse([
        {jsonrpc: '2.0', id: 'request-1', result: 3},
        {jsonrpc: '2.0', id: 'request-1', result: 7},
      ]),
      idGenerator: () => 'request-1',
    })

    await expect(duplicateIdClient.$batch([
      duplicateIdClient.$request.math.add({left: 1, right: 2}),
      duplicateIdClient.$request.math.add({left: 3, right: 4}),
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
          headers: {traceparent: '00-trace-span-01'},
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
        return jsonResponse({jsonrpc: '2.0', id: 'request-1', result: 3})
      },
      idGenerator: () => 'request-1',
      instrumentation,
    })

    await expect(okClient.call('math.add', {left: 1, right: 2})).resolves.toBe(3)
    expect(seenTraceparent).toBe('00-trace-span-01')
    expect(seenContexts).toEqual([{methods: ['math.add'], batch: false}])
    expect(events).toEqual(['start', 'end'])

    events.length = 0
    const errorClient = clientModule.createRpcClient({
      url: 'https://example.test/rpc',
      fetch: async () => jsonResponse({
        jsonrpc: '2.0',
        id: 'request-2',
        error: {code: -32602, message: 'Invalid params'},
      }),
      idGenerator: () => 'request-2',
      instrumentation,
    })

    await expect(errorClient.call('math.add', {left: 1, right: 2}))
      .rejects.toMatchObject({name: 'RpcError', rpcCode: -32602})
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
      user: {id: 'u-1', isAuthenticated: true, roles: ['admin'], permissions: ['billing.write']},
      modules: {Billing: true, Experiments: false},
      flags: {'new-checkout': true},
      variants: {ForecastAlgorithm: 'neural'},
    }

    const provider = sessionModule.createElarionOpenFeatureProvider(snapshot)
    expect(provider.runsOn).toBe('client')
    expect(provider.resolveBooleanEvaluation('module.Billing', false).value).toBe(true)
    expect(provider.resolveBooleanEvaluation('module.Experiments', true).value).toBe(false)
    expect(provider.resolveBooleanEvaluation('permission.billing.write', false).value).toBe(true)
    expect(provider.resolveBooleanEvaluation('permission.billing.read', false).value).toBe(false)
    expect(provider.resolveBooleanEvaluation('role.admin', false).value).toBe(true)
    expect(provider.resolveBooleanEvaluation('new-checkout', false).value).toBe(true)
    expect(provider.resolveBooleanEvaluation('unknown-flag', false)).toMatchObject({value: false, reason: 'DEFAULT'})
    expect(provider.resolveStringEvaluation('ForecastAlgorithm', 'control')).toMatchObject({
      value: 'neural',
      variant: 'neural'
    })
    expect(provider.resolveStringEvaluation('missing', 'control')).toMatchObject({value: 'control', reason: 'DEFAULT'})

    const caps = sessionModule.createSessionCapabilities(snapshot)
    expect(caps.isModuleEnabled('Billing')).toBe(true)
    expect(caps.hasPermission('billing.write')).toBe(true)
    expect(caps.getVariant('ForecastAlgorithm')).toBe('neural')
    expect(sessionModule.Keys.module('Billing')).toBe('module.Billing')
  })

  it('reads application-contributed snapshot sections, and stays undefined when absent', async () => {
    const generated = generateRpcClientFiles(sessionSchema(), {
      generatedBy: 'test-generator',
      sourceLabel: 'session.json',
    })
    const source = generated.sessionClientSource as string
    expect(source).toContain('getSection<T = unknown>(name: string): T | undefined')
    expect(source).not.toContain('import ')

    const sessionModule = await loadGeneratedSessionClient(source)
    const base = {
      user: {id: 'u-1', isAuthenticated: true, roles: [], permissions: []},
      modules: {},
      flags: {},
      variants: {},
    }

    const withSections = sessionModule.createSessionCapabilities({
      ...base,
      sections: {tenant: {name: 'Acme', theme: 'dark'}},
    })
    expect(withSections.getSection<{name: string; theme: string}>('tenant')).toEqual({
      name: 'Acme',
      theme: 'dark',
    })
    // A section the backend did not contribute reads as undefined, not as an error.
    expect(withSections.getSection('missing')).toBeUndefined()

    // A host with no contributors omits `sections` entirely — every read is undefined.
    expect(sessionModule.createSessionCapabilities(base).getSection('tenant')).toBeUndefined()
  })

  it('emits a refreshable session capabilities store that fails closed before the first refresh', async () => {
    const generated = generateRpcClientFiles(sessionSchema(), {
      generatedBy: 'test-generator',
      sourceLabel: 'session.json',
    })
    const source = generated.sessionClientSource as string
    expect(source).toContain('export function createSessionCapabilitiesStore')
    // The store must stay structurally compatible with the contributions package's CapabilitySource — the
    // four reader methods plus subscribe — without either package importing the other.
    expect(source).toContain('subscribe(listener: () => void): () => void')
    expect(source).not.toContain('import ')

    const sessionModule = await loadGeneratedSessionClient(source)
    const snapshots = [
      {
        user: {id: 'u-1', isAuthenticated: true, roles: [], permissions: []},
        modules: {Billing: false},
        flags: {},
        variants: {},
      },
      {
        user: {id: 'u-1', isAuthenticated: true, roles: ['admin'], permissions: ['billing.write']},
        modules: {Billing: true},
        flags: {'new-checkout': true},
        variants: {ForecastAlgorithm: 'neural'},
        sections: {tenant: {name: 'Acme'}},
      },
    ]
    let fetches = 0
    const store = sessionModule.createSessionCapabilitiesStore(async () => snapshots[fetches++]!)

    // Before the first refresh every read denies — UI gated on a capability stays hidden while the
    // snapshot is in flight instead of flashing and retracting.
    expect(store.current).toBeUndefined()
    expect(store.isModuleEnabled('Billing')).toBe(false)
    expect(store.hasPermission('billing.write')).toBe(false)
    expect(store.hasRole('admin')).toBe(false)
    expect(store.isFlagEnabled('new-checkout')).toBe(false)
    expect(store.getVariant('ForecastAlgorithm')).toBeUndefined()
    expect(store.getSection('tenant')).toBeUndefined()

    let notifications = 0
    const unsubscribe = store.subscribe(() => {
      notifications++
    })

    await store.refresh()
    expect(notifications).toBe(1)
    expect(store.isModuleEnabled('Billing')).toBe(false)

    await store.refresh()
    expect(notifications).toBe(2)
    expect(store.current).toBeDefined()
    expect(store.isModuleEnabled('Billing')).toBe(true)
    expect(store.hasPermission('billing.write')).toBe(true)
    expect(store.hasRole('admin')).toBe(true)
    expect(store.isFlagEnabled('new-checkout')).toBe(true)
    expect(store.getVariant('ForecastAlgorithm')).toBe('neural')
    expect(store.getSection<{name: string}>('tenant')).toEqual({name: 'Acme'})

    // Unsubscribing stops notifications; the store keeps answering.
    unsubscribe()
    snapshots.push(snapshots[1]!)
    await store.refresh()
    expect(notifications).toBe(2)
    expect(store.isModuleEnabled('Billing')).toBe(true)
  })

  it('applies overlapping refreshes in call order, so a slow earlier fetch cannot win', async () => {
    const generated = generateRpcClientFiles(sessionSchema(), {
      generatedBy: 'test-generator',
      sourceLabel: 'session.json',
    })
    const sessionModule = await loadGeneratedSessionClient(generated.sessionClientSource as string)

    const base = {user: {id: 'u-1', isAuthenticated: true, roles: [], permissions: []}, flags: {}, variants: {}}
    const gates: Array<(snapshot: unknown) => void> = []
    const store = sessionModule.createSessionCapabilitiesStore(
      () => new Promise((resolve) => gates.push(resolve))
    )

    // Two refreshes in flight; the first (stale) one settles last.
    const first = store.refresh()
    const second = store.refresh()
    gates[1]!({...base, modules: {Billing: true}})
    await second
    expect(store.isModuleEnabled('Billing')).toBe(true)

    gates[0]!({...base, modules: {Billing: false}})
    const firstCapabilities = await first
    // The superseded fetch neither replaced `current` nor notified...
    expect(store.isModuleEnabled('Billing')).toBe(true)
    // ...but its own caller still receives its own snapshot's capabilities.
    expect(firstCapabilities.isModuleEnabled('Billing')).toBe(false)
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
    expect(source).toContain('export const forwardRequestHeaders = createIsomorphicFn()')
    expect(source).toContain('export function createStartRpcApi')
    expect(source).toContain('readonly forwardHeaders?: readonly string[]')
    expect(generated.clientSource).not.toContain('@tanstack')

    // The emitted adapter is syntactically valid TypeScript (it strips to runnable JS). It can't be
    // runtime-loaded here because @tanstack/react-start is a consumer-side peer dependency.
    const transpiled = ts.transpileModule(source, {
      reportDiagnostics: true,
      compilerOptions: {module: ts.ModuleKind.ES2022, target: ts.ScriptTarget.ES2022},
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

  it('forwards only the request cookie during SSR by default', async () => {
    const adapter = await loadGeneratedStartAdapter({
      cookie: 'session=abc',
      authorization: 'Bearer incoming',
    })

    const headers = await adapter.module.createStartRpcApi({url: '/rpc'}).headersFor()

    expect(headers).toEqual({cookie: 'session=abc'})
    expect(adapter.module.forwardRequestCookie()).toEqual({cookie: 'session=abc'})
  })

  it('forwards the configured request headers during SSR, skipping absent ones', async () => {
    const adapter = await loadGeneratedStartAdapter({
      cookie: 'session=abc',
      'x-forwarded-identity': 'token-1',
    })

    const headers = await adapter.module.createStartRpcApi({
      url: '/rpc',
      forwardHeaders: ['X-Forwarded-Identity', 'cookie', 'x-correlation-id'],
    }).headersFor()

    expect(headers).toEqual({'x-forwarded-identity': 'token-1', cookie: 'session=abc'})
    expect(adapter.receivedOptions().forwardHeaders).toBeUndefined()
  })

  it('forwards nothing for an empty forwardHeaders list and lets explicit headers win', async () => {
    const adapter = await loadGeneratedStartAdapter({cookie: 'session=abc', 'x-tenant': 'from-request'})

    expect(await adapter.module.createStartRpcApi({url: '/rpc', forwardHeaders: []}).headersFor()).toEqual({})

    const headers = await adapter.module.createStartRpcApi({
      url: '/rpc',
      forwardHeaders: ['x-tenant'],
      headers: () => ({'x-tenant': 'explicit'}),
    }).headersFor()
    expect(headers).toEqual({'x-tenant': 'explicit'})
  })

  it('forwards no request headers in the browser', async () => {
    const adapter = await loadGeneratedStartAdapter({cookie: 'session=abc', 'x-tenant': 't1'}, 'client')

    expect(await adapter.module.createStartRpcApi({url: '/rpc', forwardHeaders: ['x-tenant']}).headersFor())
      .toEqual({})
    expect(adapter.module.forwardRequestHeaders(['cookie'])).toEqual({})
  })

  it('maps an SSR failure to its response status and rewrites the rendered response', async () => {
    const adapter = await loadGeneratedStartAdapter({})
    // The errors come from the core client instance the adapter imported (the test runner may load a module twice).
    const {RpcError, RpcTransportError} = (globalThis as unknown as Record<string, {
      RpcError: new (rpcCode: number, message: string, data?: unknown) => Error
      RpcTransportError: new (status: number, statusText: string, body: string) => Error
    }>)[`${adapter.dir}:core`]
    const {ssrStatusFor, withResponseStatus} = adapter.module

    expect(ssrStatusFor(new RpcError(-32003, 'No.', {code: 'forbidden'}))).toBe(403)
    expect(ssrStatusFor(new RpcError(-32005, 'Sign in.', {code: 'unauthorized'}))).toBe(401)
    expect(ssrStatusFor(new RpcError(-32001, 'Gone.', {code: 'not_found'}))).toBe(404)
    const maintenance = new RpcTransportError(503, 'Service Unavailable', '{"code":"unavailable"}')
    expect(ssrStatusFor(maintenance)).toBe(503)
    expect(ssrStatusFor(new Error('Wartung.', {cause: maintenance}))).toBe(503)
    expect(ssrStatusFor(new RpcError(-32602, 'Bad.', {code: 'validation'}))).toBeUndefined()
    expect(ssrStatusFor(new TypeError('boom'))).toBeUndefined()

    const rendered = new Response('<html>maintenance</html>', {status: 200, headers: {'content-type': 'text/html'}})
    const rewritten = withResponseStatus(rendered, 503)
    expect(rewritten.status).toBe(503)
    expect(rewritten.headers.get('content-type')).toBe('text/html')
    expect(rewritten.headers.get('cache-control')).toBe('no-store')
    expect(await rewritten.text()).toBe('<html>maintenance</html>')
  })

  it('recognises an unavailable service in the core client, through a mapped error', async () => {
    const generated = generateRpcClientFiles(rpcClientTestSchema())
    const clientModule = await loadGeneratedClient(generated.clientSource) as unknown as {
      isServiceUnavailable(error: unknown): boolean
      RpcTransportError: new (status: number, statusText: string, body: string) => Error
    }
    const unavailable = new clientModule.RpcTransportError(503, 'Service Unavailable', '')

    expect(clientModule.isServiceUnavailable(unavailable)).toBe(true)
    expect(clientModule.isServiceUnavailable(new Error('mapped', {cause: unavailable}))).toBe(true)
    expect(clientModule.isServiceUnavailable(new clientModule.RpcTransportError(500, 'Internal', ''))).toBe(false)
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
        Clients: {features: ['client-portal-v2', 'bulk-import']},
        Invoicing: {features: ['late-fees']},
      },
      permissions: [
        {permission: 'clients.read', resource: 'clients', verb: 'read'},
        {permission: 'clients.write', resource: 'clients', verb: 'write'},
        {permission: 'invoices.read', resource: 'invoices', verb: 'read'},
      ],
      roles: ['billing-admin'],
    }

    const generated = generateRpcClientFiles(schema, {generatedBy: 'test', sourceLabel: 'session.json'})
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
    const generated = generateRpcClientFiles(sessionSchema(), {generatedBy: 'test', sourceLabel: 'session.json'})
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
        {jsonrpc: '2.0', id: 'request-1', result: 3},
        {jsonrpc: '2.0', id: 'request-2', error: {code: -32601, message: 'Method not found'}},
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
      rpc.$request.math.add({left: 1, right: 2}),
      rpc.$request.math.add({left: 3, right: 4}),
    ] as const)

    expect(results[0]).toMatchObject({ok: true, result: 3})
    expect(results[1]).toMatchObject({ok: false, error: {rpcCode: -32601}})
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
        ({jsonrpc: '2.0', id: item.id, result: item.method === 'math.add' ? 3 : {id: 'u', name: 'n'}})
      return jsonResponse(Array.isArray(request) ? request.map(respond) : respond(request))
    }

    const meta = (index: number) => bodies[index].params._meta as Record<string, string> | undefined

    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const rpc = clientModule.createRpcApi({url: 'https://example.test/rpc', fetch: fetchImpl}) as any

    await rpc.math.add({left: 1, right: 2})
    expect(meta(0)?.[metaKey]).toEqual(expect.any(String))
    expect((meta(0)?.[metaKey] ?? '').length).toBeGreaterThan(0)

    // A non-idempotent operation never gets a key.
    await rpc.user.get({id: 'x'})
    expect(meta(1)).toBeUndefined()

    // A caller-supplied key (stable across a retry layer's retries) is used verbatim.
    await rpc.math.add({left: 1, right: 2}, {idempotencyKey: 'fixed-key'})
    expect(meta(2)?.[metaKey]).toBe('fixed-key')

    // `false` disables the key for a single call.
    await rpc.math.add({left: 1, right: 2}, {idempotencyKey: false})
    expect(meta(3)).toBeUndefined()

    // Each batch item gets its own key (batch-correct, per-call granularity).
    await rpc.$batch([rpc.$request.math.add({left: 1, right: 2}), rpc.$request.math.add({left: 3, right: 4})] as const)
    expect(meta(4)?.[metaKey]).toEqual(expect.any(String))
    expect(meta(5)?.[metaKey]).toEqual(expect.any(String))
    expect(meta(4)?.[metaKey]).not.toBe(meta(5)?.[metaKey])

    // Globally disabling opts every operation out.
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const off = clientModule.createRpcApi({
      url: 'https://example.test/rpc',
      fetch: fetchImpl,
      idempotency: {enabled: false}
    }) as any
    await off.math.add({left: 1, right: 2})
    expect(meta(6)).toBeUndefined()
  })

  it('pre-validates request params against the generated params schemas', async () => {
    const generated = generateRpcClientFiles(rpcClientTestSchema())
    const clientModule = await loadGeneratedClient(generated.clientSource)
    let fetchCalls = 0

    const fetchImpl: FetchLike = async (_input, init) => {
      fetchCalls += 1
      const request = JSON.parse(String(init?.body)) as { id: string }
      return jsonResponse({jsonrpc: '2.0', id: request.id, result: 3})
    }

    const rpc = clientModule.createRpcApi({url: 'https://example.test/rpc', fetch: fetchImpl})

    await expect(rpc.math.add({left: 1, right: 2})).resolves.toBe(3)
    expect(fetchCalls).toBe(1)

    // Invalid params fail locally with a descriptive error and never reach the wire.
    await expect(rpc.math.add({left: 'one', right: 2})).rejects.toMatchObject({
      name: 'RpcParamsValidationError',
      method: 'math.add',
      message: expect.stringContaining('math.add'),
    })
    expect(fetchCalls).toBe(1)

    // An invalid batch item fails the whole batch locally before anything is sent.
    await expect(rpc.$batch([
      rpc.$request.math.add({left: 1, right: 2}),
      rpc.$request.math.add({left: 'oops', right: 4}),
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
    await expect(unchecked.math.add({left: 'one', right: 2})).resolves.toBe(3)
    expect(fetchCalls).toBe(2)
  })

  it('exposes params validation issues as field errors keyed like the server', async () => {
    const generated = generateRpcClientFiles(rpcClientTestSchema())
    const clientModule = await loadGeneratedClient(generated.clientSource)
    const rpc = clientModule.createRpcApi({url: 'https://example.test/rpc', fetch: async () => jsonResponse({})})

    // Through the call path: the stub params schema throws a Zod-shaped error (an `issues` array).
    await expect(rpc.user.get({id: 7})).rejects.toMatchObject({
      name: 'RpcParamsValidationError',
      fieldErrors: {id: ['Expected string id']},
    })

    // Zod issue paths become the server's ValidationErrorData.fieldErrors keys (MicrosoftRequestValidator):
    // member names joined with ".", indexes as "[i]", "" for the params object itself; messages grouped per key.
    const cause = {
      issues: [
        {path: ['code'], message: 'Code must be three capital letters.'},
        {path: ['address', 'street'], message: 'Street is too long.'},
        {path: ['deliveries', 1, 'street'], message: 'Street is too long.'},
        {path: ['matrix', 0, 2], message: 'Too small.'},
        {path: [], message: 'Expected object.'},
        {path: ['code'], message: 'Code must be at most 20 characters.'},
        {path: ['constructor'], message: 'Required.'},
      ],
    }
    const error = new clientModule.RpcParamsValidationError('user.get', cause)

    expect(error.cause).toBe(cause)
    expect(error.fieldErrors).toEqual({
      code: ['Code must be three capital letters.', 'Code must be at most 20 characters.'],
      'address.street': ['Street is too long.'],
      'deliveries[1].street': ['Street is too long.'],
      'matrix[0][2]': ['Too small.'],
      '': ['Expected object.'],
      constructor: ['Required.'],
    })
    expect(Object.hasOwn(error.fieldErrors, 'constructor')).toBe(true)

    // A cause without Zod issues still yields an (empty) field-error map.
    expect(new clientModule.RpcParamsValidationError('user.get', new Error('boom')).fieldErrors).toEqual({})
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
    hasRole(role: string): boolean
    isFlagEnabled(name: string): boolean
    getVariant(name: string): string | undefined
    getSection<T = unknown>(name: string): T | undefined
  }

  createSessionCapabilitiesStore(fetchSnapshot: () => Promise<unknown>): {
    readonly current: {isModuleEnabled(name: string): boolean} | undefined
    refresh(): Promise<{isModuleEnabled(name: string): boolean}>
    subscribe(listener: () => void): () => void
    isModuleEnabled(name: string): boolean
    hasPermission(permission: string): boolean
    hasRole(role: string): boolean
    isFlagEnabled(name: string): boolean
    getVariant(name: string): string | undefined
    getSection<T = unknown>(name: string): T | undefined
  }

  Keys: { module(name: string): string; permission(permission: string): string; role(role: string): string }
}

function sessionSchema(): RpcSchema {
  return {
    methods: {
      'elarion.session': {
        params: {type: 'object', properties: {}},
        result: {type: 'object', properties: {}},
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
            left: {type: 'number'},
            right: {type: 'number'},
          },
          required: ['left', 'right'],
        },
        result: {type: 'number'},
      },
      'user.get': {
        params: {
          type: 'object',
          properties: {
            id: {type: 'string'},
          },
          required: ['id'],
        },
        result: {
          type: 'object',
          properties: {
            id: {type: 'string'},
            name: {type: 'string'},
          },
          required: ['id', 'name'],
        },
      },
    },
  }
}

function decisionTestSchema(): RpcSchema {
  const schema = rpcClientTestSchema()
  const add = schema.methods['math.add']
  return {
    ...schema,
    methods: {
      ...schema.methods,
      'math.add': {
        ...add,
        params: {
          type: 'object',
          properties: {
            left: {type: 'number'},
            right: {type: 'number'},
            // The server narrows the decisions member to the declared decisions: one answer shape per code.
            decisions: {
              type: ['array', 'null'],
              items: {
                anyOf: [
                  {
                    type: 'object',
                    properties: {
                      code: {type: 'string', enum: ['math.big_sum']},
                      option: {type: 'string', enum: ['confirm']},
                    },
                    required: ['code'],
                  },
                  {
                    type: 'object',
                    properties: {
                      code: {type: 'string', enum: ['math.per_item']},
                      item: {type: 'string'},
                      option: {type: 'string', enum: ['keep', 'drop']},
                    },
                    required: ['code', 'item', 'option'],
                  },
                ],
              },
            },
          },
          required: ['left', 'right'],
        },
        errors: {
          'math.big_sum': {
            kind: 'decision_required',
            data: {type: 'object', properties: {limit: {type: 'number'}}, required: ['limit']},
            decision: {options: ['confirm']},
          },
          'math.per_item': {kind: 'decision_required', decision: {options: ['keep', 'drop'], perItem: true}},
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
      contentType: {type: 'string'},
      fileName: {type: 'string'},
      data: {type: 'string', format: 'byte'},
    },
    required: ['contentType', 'data'],
  } satisfies JsonSchema

  return {
    methods: {
      'files.roundTrip': {
        params: {
          type: 'object',
          properties: {
            container: {type: 'string'},
            required: fileSchema,
            file: fileSchema,
            attachments: {type: 'array', items: fileSchema},
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
    'export const rpcErrorDataSchemas = {}',
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

interface StartAdapterModule {
  createStartRpcApi(options: {
    url: string
    forwardHeaders?: readonly string[]
    headers?: HeadersInit | (() => HeadersInit | Promise<HeadersInit>)
  }): { headersFor(): Promise<HeadersInit> }

  forwardRequestHeaders(names: readonly string[]): HeadersInit

  forwardRequestCookie(): HeadersInit

  ssrStatusFor(error: unknown): number | undefined

  withResponseStatus(response: Response, status: number): Response
}

// Loads the generated start-adapter.ts against stand-ins for its imports: `@tanstack/react-start` runs the
// `.server(...)` or `.client(...)` implementation for the given environment (what the Start compiler leaves in
// that bundle), `@tanstack/react-start/server` serves `requestHeaders`, and the core client's `createRpcApi`
// records its options so a test can resolve the headers a call would send.
async function loadGeneratedStartAdapter(
  requestHeaders: Record<string, string>,
  environment: 'server' | 'client' = 'server'
): Promise<{ module: StartAdapterModule; dir: string; receivedOptions(): Record<string, unknown> }> {
  const generated = generateRpcClientFiles(rpcClientTestSchema(), {framework: 'tanstack-start'})
  const dir = mkdtempSync(join(tmpdir(), 'elarion-start-adapter-'))
  const jsSource = ts.transpileModule(generated.frameworkAdapterSource as string, {
    compilerOptions: {
      module: ts.ModuleKind.ES2022,
      target: ts.ScriptTarget.ES2022,
    },
  }).outputText
    .replace("from '@tanstack/react-start/server'", "from './react-start-server.mjs'")
    .replace("from '@tanstack/react-start'", "from './react-start.mjs'")
    .replace("from './rpc-client.js'", "from './rpc-client.mjs'")

  writeFileSync(join(dir, 'start-adapter.mjs'), jsSource, 'utf-8')
  writeFileSync(join(dir, 'react-start.mjs'), [
    'export function createIsomorphicFn() {',
    '  return {',
    '    server(serverImpl) {',
    '      return {',
    `        client: (clientImpl) => ${environment === 'server' ? 'serverImpl' : 'clientImpl'},`,
    '      }',
    '    },',
    '  }',
    '}',
    '',
  ].join('\n'), 'utf-8')
  writeFileSync(join(dir, 'react-start-server.mjs'), [
    `const requestHeaders = ${JSON.stringify(requestHeaders)}`,
    'export function getRequestHeader(name) {',
    '  return requestHeaders[name.toLowerCase()]',
    '}',
    '',
  ].join('\n'), 'utf-8')
  writeFileSync(join(dir, 'rpc-client.mjs'), [
    // Kept on a per-directory global: the test runner may load this module once for the adapter and once for the
    // test, and a module-level binding would then be a different instance.
    // The error types and their recognisers are the real generated ones.
    "export { isServiceUnavailable, RpcError, RpcTransportError } from './rpc-client-real.mjs'",
    "import * as real from './rpc-client-real.mjs'",
    `globalThis[${JSON.stringify(dir + ':core')}] = real`,
    `export function createRpcApi(options) {`,
    `  globalThis[${JSON.stringify(dir)}] = options`,
    "  return { headersFor: () => options.headers({ methods: ['math.add'], batch: false }) }",
    '}',
    '',
  ].join('\n'), 'utf-8')

  writeFileSync(join(dir, 'rpc-client-real.mjs'), ts.transpileModule(generated.clientSource, {
    compilerOptions: {module: ts.ModuleKind.ES2022, target: ts.ScriptTarget.ES2022},
  }).outputText.replace("from './rpc-schemas.js'", "from './rpc-schemas.mjs'"), 'utf-8')
  writeFileSync(join(dir, 'rpc-schemas.mjs'),
    'export const rpcParamsSchemas = {}\nexport const rpcResultSchemas = {}\nexport const rpcErrorDataSchemas = {}\n', 'utf-8')

  const module = await import(pathToFileURL(join(dir, 'start-adapter.mjs')).href) as StartAdapterModule
  return {
    module,
    dir,
    receivedOptions: () => (globalThis as unknown as Record<string, Record<string, unknown>>)[dir],
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
    "      if (typeof value !== 'object' || value === null || typeof value.id !== 'string') throw Object.assign(new Error('Expected string id'), { issues: [{ path: ['id'], message: 'Expected string id' }] })",
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
    'export const rpcErrorDataSchemas = {',
    "  'math.add': { 'math.overflow': { safeParse: (v) => typeof v === 'object' && v !== null && typeof v.limit === 'number' ? { success: true } : { success: false, error: { message: 'limit must be a number' } } } },",
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

// Type-checks generated files as an ES module package under NodeNext with strict settings; returns the formatted
// diagnostics, so an unused @ts-expect-error is reported as well.
function typeCheck(files: Record<string, string>): string[] {
  const dir = mkdtempSync(join(tmpdir(), 'elarion-rpc-typecheck-'))
  writeFileSync(join(dir, 'package.json'), JSON.stringify({type: 'module'}), 'utf-8')
  for (const [name, source] of Object.entries(files)) {
    writeFileSync(join(dir, name), source, 'utf-8')
  }

  const {options, errors} = ts.convertCompilerOptionsFromJson({
    target: 'ES2022',
    module: 'NodeNext',
    moduleResolution: 'NodeNext',
    lib: ['ES2022', 'DOM', 'DOM.Iterable'],
    strict: true,
    noEmit: true,
    skipLibCheck: true,
    types: [],
  }, dir)
  expect(errors).toEqual([])
  const program = ts.createProgram(Object.keys(files).map((name) => join(dir, name)), options)
  return ts.getPreEmitDiagnostics(program).map((diagnostic) => {
    const message = ts.flattenDiagnosticMessageText(diagnostic.messageText, '\n')
    if (diagnostic.file === undefined || diagnostic.start === undefined) return message
    const {line} = diagnostic.file.getLineAndCharacterOfPosition(diagnostic.start)
    return `${diagnostic.file.fileName.slice(dir.length + 1)}:${line + 1}: ${message}`
  })
}

function jsonResponse(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: {'Content-Type': 'application/json'},
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
    const withEmptyEvents = generateRpcClientFiles({...rpcClientTestSchema(), events: {}})

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

    sources[0].emit('invoicing.invoiceChanged', JSON.stringify({invoiceId: 'inv-1'}))
    expect(received).toEqual([{invoiceId: 'inv-1'}])

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
      onEventError: (topic, error) => errors.push({topic, error}),
    })

    const received: unknown[] = []
    events['invoicing']['invoiceChanged'].subscribe((payload) => received.push(payload))
    await microtasks()

    sources[0].emit('invoicing.invoiceChanged', JSON.stringify({wrong: true}))

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

    events['invoicing']['invoiceChanged'].subscribe(() => {
    })
    await microtasks()
    expect(sources).toHaveLength(1)

    events.$client.subscribe('invoicing.invoiceChanged', {resource: 'customer:42'}, () => {
    })
    await microtasks()

    expect(sources).toHaveLength(2)
    expect(sources[0].closed).toBe(true)
    expect(decodeURIComponent(sources[1].url)).toContain('"resource":"customer:42"')
  })

  it('reopens a connection the EventSource gave up on, backing off until it connects again', async () => {
    const generated = generateRpcClientFiles(eventsTestSchema())
    const eventsModule = await loadGeneratedEventsClient(generated.eventsClientSource as string)
    const sources: FakeEventSource[] = []
    const attempts: number[] = []
    const events = eventsModule.createElarionEvents({
      url: '/events',
      eventSource: (url) => {
        const source = new FakeEventSource(url)
        sources.push(source)
        return source
      },
      reconnectDelay: (attempt) => {
        attempts.push(attempt)
        return 0
      },
    })
    let connectedCount = 0
    events.$client.onConnected(() => {
      connectedCount += 1
    })
    events['invoicing']['invoiceChanged'].subscribe(() => {
    })
    await microtasks()

    // A 401, a login page or a 502 fails the connection for good: readyState CLOSED, no platform retry.
    sources[0].fail()
    await microtasks()
    expect(sources).toHaveLength(2)
    expect(sources[1].url).toBe(sources[0].url)

    sources[1].fail()
    await microtasks()
    expect(sources).toHaveLength(3)
    expect(attempts).toEqual([0, 1])

    // Connecting again resets the backoff and tells the app to re-query.
    sources[2].emit('elarion.connected', '{}')
    expect(connectedCount).toBe(1)
    sources[2].fail()
    await microtasks()
    expect(attempts).toEqual([0, 1, 0])
  })

  it('leaves a dropped connection the EventSource is still retrying to the EventSource', async () => {
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
      reconnectDelay: () => 0,
    })
    events['invoicing']['invoiceChanged'].subscribe(() => {
    })
    await microtasks()

    sources[0].readyState = 0
    sources[0].emit('error', '')
    await microtasks()

    expect(sources).toHaveLength(1)
    expect(sources[0].closed).toBe(false)
  })

  it('cancels a pending reconnect when the client is closed', async () => {
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
      reconnectDelay: () => 0,
    })
    events['invoicing']['invoiceChanged'].subscribe(() => {
    })
    await microtasks()

    sources[0].fail()
    events.$client.close()
    await microtasks()

    expect(sources).toHaveLength(1)
  })
})

class FakeEventSource {
  readonly listeners = new Map<string, Array<(event: { data?: unknown }) => void>>()
  closed = false
  readyState = 1

  constructor(readonly url: string) {
  }

  addEventListener(type: string, listener: (event: { data?: unknown }) => void): void {
    const list = this.listeners.get(type) ?? []
    list.push(listener)
    this.listeners.set(type, list)
  }

  close(): void {
    this.closed = true
    this.readyState = 2
  }

  // What the platform EventSource does on a non-200 or non-event-stream answer.
  fail(): void {
    this.readyState = 2
    this.emit('error', '')
  }

  emit(type: string, data: string): void {
    for (const listener of this.listeners.get(type) ?? []) {
      listener({data})
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
    reconnectDelay?: (attempt: number) => number
  }): { $client: EventsClientForTests } & Record<string, Record<string, EventsTopicApiForTests>>
}

function eventsTestSchema(): RpcSchema {
  return {
    methods: {
      'math.add': {
        params: {
          type: 'object',
          properties: {
            left: {type: 'number'},
            right: {type: 'number'},
          },
          required: ['left', 'right'],
        },
        result: {type: 'number'},
      },
    },
    events: {
      'invoicing.invoiceChanged': {
        payload: {
          type: 'object',
          properties: {
            invoiceId: {type: 'string'},
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

describe('uploaded files (ADR-0105)', () => {
  it('types upload members as File | Blob | UploadedFileRef in params and as the reference in results', () => {
    const generated = generateRpcClientFiles(uploadClientTestSchema())

    expect(generated.typesSource).toContain('export type UploadedFileRef = string')
    expect(generated.typesSource).toContain('statement: File | Blob | UploadedFileRef\n')
    expect(generated.typesSource).toContain('signature?: File | Blob | UploadedFileRef | null | undefined')
    expect(generated.typesSource).toContain('attachments: (File | Blob | UploadedFileRef)[]')
    expect(generated.typesSource).toContain('echo: UploadedFileRef\n')
    expect(generated.schemasSource).toContain('statement: z.union([z.instanceof(Blob), z.string()]),')
    expect(generated.schemasSource).toContain('signature: z.union([z.instanceof(Blob), z.string()]).nullish().optional(),')
    expect(generated.schemasSource).toContain('attachments: z.array(z.union([z.instanceof(Blob), z.string()])),')
    expect(generated.schemasSource).toContain('echo: z.string(),')
    expect(generated.clientSource).toContain("import type { RpcMethods, UploadedFileRef } from './rpc-types.js'")
    expect(generated.clientSource).toContain(
      "import { rpcErrorDataSchemas, rpcParamsSchemas, rpcResultSchemas, rpcUploadLimits } from './rpc-schemas.js'")
    expect(generated.clientSource).toContain('uploadFile(file: Blob, options?: RpcUploadFileOptions): Promise<UploadedFileRef>')
    expect(generated.clientSource).toContain("readonly $uploadFile: RpcClient['uploadFile']")
    // Every upload member, with or without declared limits, so the method/member pair of uploadFile is typed.
    expect(generated.schemasSource).toContain([
      'export const rpcUploadLimits = {',
      '  "statements.analyze": {',
      '    statement: {},',
      '    signature: {},',
      '    attachments: {},',
      '  },',
      '  "statements.commit": {',
      '    statement: {},',
      '  },',
      '} as const',
    ].join('\n'))
  })

  it('emits no upload runtime and stays byte-identical when the schema has no upload members', () => {
    const without = generateRpcClientFiles(fileClientTestSchema())
    const unmarked = fileClientTestSchema()
    const container = unmarked.methods['files.roundTrip'].params.properties?.container as JsonSchema
    container['x-elarion-upload'] = false
    const withUnmarked = generateRpcClientFiles(unmarked)

    expect(withUnmarked.typesSource).toBe(without.typesSource)
    expect(withUnmarked.schemasSource).toBe(without.schemasSource)
    expect(withUnmarked.clientSource).toBe(without.clientSource)
    expect(without.typesSource).not.toContain('UploadedFileRef')
    expect(without.clientSource).not.toContain('UploadedFileRef')
    expect(without.clientSource).not.toContain('stageUploadParams')
    expect(without.clientSource).not.toContain('uploadFile')
    expect(without.clientSource).not.toContain('RpcUploadLimitError')
    expect(without.schemasSource).not.toContain('rpcUploadLimits')
  })

  it('rejects an upload member inside a union case', () => {
    const schema = {
      methods: {
        'a.b': {
          params: {
            type: 'object',
            properties: {
              value: {anyOf: [{type: 'string', 'x-elarion-upload': true}, {type: 'integer'}]},
            },
          },
          result: {type: 'string'},
        },
      },
    } satisfies RpcSchema

    expect(() => generateRpcClientFiles(schema)).toThrow(/x-elarion-upload payloads inside an anyOf union/)
  })

  it('uploads File and Blob params to the direct-upload endpoint before the call and sends the references', async () => {
    const generated = generateRpcClientFiles(uploadClientTestSchema())
    const clientModule = await loadGeneratedUploadClient(generated.clientSource)
    const server = uploadServer()
    const rpc = clientModule.createRpcApi({
      url: 'https://example.test/rpc',
      fetch: server.fetch,
      headers: {Authorization: 'Bearer test'},
      idGenerator: () => 'call-1',
    })

    const statement = new File(['date;amount'], 'statement.csv', {type: 'text/csv'})
    const signature = new Blob([new Uint8Array([1, 2])])
    const result = await rpc.statements.analyze({
      statement,
      signature,
      attachments: [new File(['x'], 'a.pdf', {type: 'application/pdf'}), 'blob-earlier'],
      account: 'main',
    })

    expect(server.uploads.map((upload) => upload.url)).toEqual([
      'https://example.test/_elarion/blobs',
      'https://example.test/_elarion/blobs',
      'https://example.test/_elarion/blobs',
    ])
    expect(server.uploads[0]).toMatchObject({
      method: 'POST',
      contentType: 'text/csv',
      fileName: 'statement.csv',
      authorization: 'Bearer test',
      body: 'date;amount',
    })
    // A bare Blob has neither a name nor a type.
    expect(server.uploads[1]).toMatchObject({contentType: 'application/octet-stream', fileName: null})
    expect(server.uploads[2]).toMatchObject({contentType: 'application/pdf', fileName: 'a.pdf'})
    expect(server.calls[0].params).toEqual({
      statement: 'blob-1',
      signature: 'blob-2',
      attachments: ['blob-3', 'blob-earlier'],
      account: 'main',
    })
    expect(result).toEqual({echo: 'blob-1'})
  })

  it('exposes uploadFile, so a multi-step flow uploads once and passes the reference to every step', async () => {
    const generated = generateRpcClientFiles(uploadClientTestSchema())
    const clientModule = await loadGeneratedUploadClient(generated.clientSource)
    const server = uploadServer()
    const rpc = clientModule.createRpcApi({url: 'https://example.test/rpc', fetch: server.fetch})

    const reference = await rpc.$uploadFile(new Blob(['rows']), {fileName: 'named.csv'})
    await rpc.statements.analyze({statement: reference, attachments: [], account: 'main'})
    await rpc.statements.analyze({statement: reference, attachments: [], account: 'main'})

    expect(reference).toBe('blob-1')
    expect(server.uploads).toHaveLength(1)
    expect(server.uploads[0].fileName).toBe('named.csv')
    expect(server.calls.map((call) => (call.params as {statement: string}).statement)).toEqual(['blob-1', 'blob-1'])
  })

  it('sends a file name outside printable ASCII percent-encoded in the query instead of a header', async () => {
    const generated = generateRpcClientFiles(uploadClientTestSchema())
    const clientModule = await loadGeneratedUploadClient(generated.clientSource)
    const server = uploadServer()
    const absolute = clientModule.createRpcApi({url: 'https://example.test/rpc', fetch: server.fetch})
    const relative = clientModule.createRpcApi({url: '/rpc', fetch: server.fetch})

    await absolute.$uploadFile(new File(['x'], 'Kontoauszug März.csv', {type: 'text/csv'}))
    await relative.$uploadFile(new File(['x'], 'Kontoauszug März.csv', {type: 'text/csv'}))

    // Both are the form-encoded UTF-8 name ASP.NET Core decodes from the query.
    expect(server.uploads.map((upload) => upload.url)).toEqual([
      'https://example.test/_elarion/blobs?fileName=Kontoauszug+M%C3%A4rz.csv',
      '/_elarion/blobs?fileName=Kontoauszug%20M%C3%A4rz.csv',
    ])
    expect(server.uploads.map((upload) => upload.fileName)).toEqual([null, null])
  })

  it('names the upload policy and the record it is for in the query, after the file name', async () => {
    const generated = generateRpcClientFiles(uploadClientTestSchema())
    const clientModule = await loadGeneratedUploadClient(generated.clientSource)
    const server = uploadServer()
    const absolute = clientModule.createRpcApi({url: 'https://example.test/rpc', fetch: server.fetch})
    const relative = clientModule.createRpcApi({url: '/rpc', fetch: server.fetch})

    await absolute.$uploadFile(new File(['x'], 'März.eml'), {policy: 'mail.eml', resourceId: 'draft 1'})
    await relative.$uploadFile(new File(['x'], 'message.eml'), {policy: 'mail.eml'})

    expect(server.uploads.map((upload) => upload.url)).toEqual([
      'https://example.test/_elarion/blobs?fileName=M%C3%A4rz.eml&policy=mail.eml&resourceId=draft+1',
      '/_elarion/blobs?policy=mail.eml',
    ])
    expect(server.uploads[1].fileName).toBe('message.eml')
  })

  it('type-checks the upload policy options under NodeNext', () => {
    const generated = generateRpcClientFiles(uploadClientTestSchema())
    const usage = [
      "import { createRpcClient } from './rpc-client.js'",
      "import type { RpcUploadOptions } from './rpc-client.js'",
      '',
      "const client = createRpcClient({ url: 'https://example.test/rpc' })",
      'export async function run(file: File): Promise<string> {',
      "  const options: RpcUploadOptions = { policy: 'mail.eml', resourceId: 'draft-1' }",
      "  const reference = await client.uploadFile(file, { ...options, method: 'statements.analyze', member: 'statement' })",
      '  // @ts-expect-error a policy is named by its string name',
      '  await client.uploadFile(file, { policy: 1 })',
      '  return reference',
      '}',
      '',
    ].join('\n')
    // zod is no dependency of this package: a stub stands in for the schemas, with the upload members.
    const schemasStub = [
      'export const rpcParamsSchemas: Record<string, any> = {}',
      'export const rpcResultSchemas: Record<string, any> = {}',
      'export const rpcErrorDataSchemas: Record<string, Record<string, any>> = {}',
      "export const rpcUploadLimits = { 'statements.analyze': { statement: {} } } as const",
      '',
    ].join('\n')

    const diagnostics = typeCheck({
      'rpc-types.ts': generated.typesSource,
      'rpc-client.ts': generated.clientSource,
      'rpc-schemas.ts': schemasStub,
      'usage.ts': usage,
    })

    expect(diagnostics).toEqual([])
  })

  it('stays relative to the page for a relative RPC url, and honors a configured upload url', async () => {
    const generated = generateRpcClientFiles(uploadClientTestSchema())
    const clientModule = await loadGeneratedUploadClient(generated.clientSource)
    const server = uploadServer()

    await clientModule.createRpcApi({url: '/rpc', fetch: server.fetch}).$uploadFile(new Blob(['a']))
    await clientModule.createRpcApi({url: '/rpc', fetch: server.fetch, uploadUrl: '/app/_elarion/blobs'})
      .$uploadFile(new Blob(['b']))

    expect(server.uploads.map((upload) => upload.url)).toEqual(['/_elarion/blobs', '/app/_elarion/blobs'])
  })

  it('stages through a configured uploader with the call signal', async () => {
    const generated = generateRpcClientFiles(uploadClientTestSchema())
    const clientModule = await loadGeneratedUploadClient(generated.clientSource)
    const server = uploadServer()
    const abort = new AbortController()
    const seen: Array<{size: number; signal: AbortSignal | undefined}> = []
    const rpc = clientModule.createRpcApi({
      url: 'https://example.test/rpc',
      fetch: server.fetch,
      uploadFile: async (file, options) => {
        seen.push({size: file.size, signal: options.signal})
        return `custom-${seen.length}`
      },
    })

    await rpc.statements.analyze({statement: new Blob(['abc']), attachments: [], account: 'main'}, {signal: abort.signal})

    expect(seen).toEqual([{size: 3, signal: abort.signal}])
    expect(server.uploads).toHaveLength(0)
    expect((server.calls[0].params as {statement: string}).statement).toBe('custom-1')
  })

  it('fails the call with a transport error, without calling the method, when the upload is refused', async () => {
    const generated = generateRpcClientFiles(uploadClientTestSchema())
    const clientModule = await loadGeneratedUploadClient(generated.clientSource)
    const server = uploadServer({status: 413, body: 'too large'})
    const rpc = clientModule.createRpcApi({url: 'https://example.test/rpc', fetch: server.fetch})

    await expect(rpc.statements.analyze({statement: new Blob(['abc']), attachments: [], account: 'main'}))
      .rejects.toMatchObject({name: 'RpcTransportError', status: 413})
    expect(server.calls).toHaveLength(0)
  })

  it('stages uploads per batch item and composes with file payloads', async () => {
    const generated = generateRpcClientFiles(uploadClientTestSchema())
    const clientModule = await loadGeneratedUploadClient(generated.clientSource)
    const server = uploadServer()
    let nextId = 0
    const rpc = clientModule.createRpcApi({
      url: 'https://example.test/rpc',
      fetch: server.fetch,
      idGenerator: () => `item-${++nextId}`,
    })

    const results = await rpc.$batch([
      rpc.$request.statements.analyze({statement: new Blob(['a']), attachments: [], account: 'main'}),
      rpc.$request.statements.commit({
        statement: new Blob(['b']),
        logo: new File(['png'], 'logo.png', {type: 'image/png'}),
      }),
    ])

    expect(results.map((item) => item.ok)).toEqual([true, true])
    expect(server.calls[0].params).toMatchObject({statement: 'blob-1'})
    expect(server.calls[1].params).toEqual({
      statement: 'blob-2',
      logo: {contentType: 'image/png', fileName: 'logo.png', data: btoa('png')},
    })
  })
})

interface UploadClientModule {
  createRpcApi(options: {
    url: string | URL
    fetch?: FetchLike
    headers?: HeadersInit
    idGenerator?: () => string | number
    validateParams?: boolean
    uploadFile?: (file: Blob, options: {fileName?: string; signal?: AbortSignal}) => Promise<string>
    uploadUrl?: string | URL
  }): UploadApiForTests

  RpcError: abstract new (...args: never[]) => Error
  RpcUploadLimitError: abstract new (...args: never[]) => Error
}

interface UploadApiForTests {
  statements: {
    analyze(params: unknown, options?: {signal?: AbortSignal}): Promise<unknown>
    commit(params: unknown): Promise<unknown>
  }
  $request: {
    statements: {
      analyze(params: unknown): {method: string; params: unknown}
      commit(params: unknown): {method: string; params: unknown}
    }
  }
  $batch(items: readonly {method: string; params: unknown}[]): Promise<readonly BatchItemResultForTests[]>
  $uploadFile(
    file: Blob,
    options?: {
      fileName?: string; signal?: AbortSignal; method?: string; member?: string; policy?: string; resourceId?: string
    }
  ): Promise<string>
}

interface RecordedUpload {
  url: string
  method: string | undefined
  contentType: string | null
  fileName: string | null
  authorization: string | null
  body: string
}

// A stand-in for the host: the direct-upload endpoint answers blob-1, blob-2, ... as text/plain (or the given
// failure), and the RPC endpoint echoes the first upload member of the params.
function uploadServer(failure?: {status: number; body: string}): {
  fetch: FetchLike
  uploads: RecordedUpload[]
  calls: Array<{method: string; params: unknown}>
} {
  const uploads: RecordedUpload[] = []
  const calls: Array<{method: string; params: unknown}> = []
  const fetch: FetchLike = async (input, init) => {
    const url = input.toString()
    if (url.includes('_elarion/blobs')) {
      const headers = new Headers(init?.headers)
      uploads.push({
        url,
        method: init?.method,
        contentType: headers.get('content-type'),
        fileName: headers.get('x-elarion-file-name'),
        authorization: headers.get('authorization'),
        body: await new Response(init?.body).text(),
      })
      return failure === undefined
        ? new Response(`blob-${uploads.length}\n`, {status: 200, headers: {'Content-Type': 'text/plain'}})
        : new Response(failure.body, {status: failure.status})
    }

    const payload = JSON.parse(String(init?.body)) as
      | {id: string; method: string; params: {statement?: unknown}}
      | Array<{id: string; method: string; params: {statement?: unknown}}>
    const requests = Array.isArray(payload) ? payload : [payload]
    calls.push(...requests.map((request) => ({method: request.method, params: request.params})))
    const responses = requests.map((request) => ({
      jsonrpc: '2.0',
      id: request.id,
      result: request.method === 'statements.analyze' ? {echo: request.params.statement} : 'ok',
    }))
    return jsonResponse(Array.isArray(payload) ? responses : responses[0])
  }

  return {fetch, uploads, calls}
}

function uploadClientTestSchema(): RpcSchema {
  const upload = {type: 'string', 'x-elarion-upload': true} satisfies JsonSchema
  return {
    methods: {
      'statements.analyze': {
        params: {
          type: 'object',
          properties: {
            statement: upload,
            signature: {type: ['string', 'null'], 'x-elarion-upload': true},
            attachments: {type: 'array', items: upload},
            account: {type: 'string'},
          },
          required: ['statement', 'attachments', 'account'],
        },
        result: {
          type: 'object',
          properties: {echo: upload},
          required: ['echo'],
        },
      },
      'statements.commit': {
        params: {
          type: 'object',
          properties: {
            statement: upload,
            logo: {
              type: 'object',
              'x-elarion-file': true,
              properties: {
                contentType: {type: 'string'},
                fileName: {type: 'string'},
                data: {type: 'string', format: 'byte'},
              },
              required: ['contentType', 'data'],
            },
          },
          required: ['statement'],
        },
        result: {type: 'string'},
      },
    },
  }
}

// Loads the generated client over pass-through Zod schemas (params validation runs before the upload, so it sees the
// File/Blob values) and the generated rpcUploadLimits of `schemasSource`, the one block of it that needs no Zod.
async function loadGeneratedUploadClient(clientSource: string, schemasSource?: string): Promise<UploadClientModule> {
  const dir = mkdtempSync(join(tmpdir(), 'elarion-rpc-upload-client-'))
  const transpile = (source: string) => ts.transpileModule(source, {
    compilerOptions: {
      module: ts.ModuleKind.ES2022,
      target: ts.ScriptTarget.ES2022,
    },
  }).outputText

  const uploadLimits = schemasSource === undefined
    ? 'export const rpcUploadLimits = {}'
    : transpile(/export const rpcUploadLimits = [\s\S]*?\n} as const\n/.exec(schemasSource)?.[0] ?? '')
  writeFileSync(join(dir, 'rpc-client.mjs'), transpile(clientSource), 'utf-8')
  writeFileSync(join(dir, 'rpc-schemas.js'), [
    'const passThrough = { parse: (value) => value }',
    'const methods = new Proxy({}, { get: () => passThrough })',
    'export const rpcParamsSchemas = methods',
    'export const rpcResultSchemas = methods',
    'export const rpcErrorDataSchemas = {}',
    uploadLimits,
    '',
  ].join('\n'), 'utf-8')

  return await import(pathToFileURL(join(dir, 'rpc-client.mjs')).href) as UploadClientModule
}

describe('client exports (ADR-0100)', () => {
  function exportsSchema(): RpcSchema {
    return {
      methods: {
        'invoices.list': {
          params: {
            type: 'object',
            properties: {
              toTransfer: {type: ['boolean', 'null']},
              status: {enum: ['Draft', 'Approved', null]},
              size: {type: 'integer', minimum: 1, maximum: 100},
              search: {type: ['string', 'null'], maxLength: 200},
              address: {
                type: 'object',
                properties: {street: {type: 'string', minLength: 1, maxLength: 80}, city: {type: 'string'}},
                required: ['street', 'city'],
              },
            },
          },
          result: {type: 'integer'},
          presets: {
            toTransfer: {toTransfer: true},
            notExported: {status: 'Approved', size: 50},
          },
          sortKeys: [
            {key: 'createdAt', direction: 'descending', default: true},
            {key: 'number', direction: 'ascending'},
          ],
        },
        'documents.upload': {
          params: {
            type: 'object',
            properties: {
              statement: {
                type: 'string',
                'x-elarion-upload': true,
                'x-elarion-max-bytes': 1024,
                'x-elarion-content-types': ['text/csv', 'Application/XML; charset=utf-8'],
              },
              receipts: {
                type: 'array',
                maxItems: 10,
                items: {type: 'string', 'x-elarion-upload': true, 'x-elarion-max-bytes': 2048},
              },
              workbook: {type: ['string', 'null'], 'x-elarion-upload': true, 'x-elarion-file-extensions': ['.xlsx', '.XLS']},
            },
            required: ['statement', 'receipts'],
          },
          result: {type: 'integer'},
        },
        'math.ping': {
          params: {type: 'object', properties: {}},
          result: {type: 'string'},
        },
      },
      constants: {
        billing: {maxReceipts: 10, currencies: ['EUR', 'CHF'], defaultStatus: 'Approved'},
        archive: {enabled: true},
      },
    }
  }

  it('exports every params limit by member path, with upload limits on the upload member', () => {
    const generated = generateRpcClientFiles(exportsSchema())

    expect(generated.schemasSource).toContain([
      'export const rpcLimits = {',
      '  "documents.upload": {',
      '    statement: {',
      '      maxBytes: 1024,',
      '      contentTypes: ["text/csv", "Application/XML; charset=utf-8"],',
      '    },',
      '    receipts: {',
      '      maxItems: 10,',
      '      items: {',
      '        maxBytes: 2048,',
      '      },',
      '    },',
      '    workbook: {',
      '      fileExtensions: [".xlsx", ".XLS"],',
      '    },',
      '  },',
      '  "invoices.list": {',
      '    size: {',
      '      minimum: 1,',
      '      maximum: 100,',
      '    },',
      '    search: {',
      '      maxLength: 200,',
      '    },',
      '    address: {',
      '      street: {',
      '        minLength: 1,',
      '        maxLength: 80,',
      '      },',
      '    },',
      '  },',
      '} as const',
      '',
      'export type RpcLimits = typeof rpcLimits',
    ].join('\n'))
    expect(generated.schemasSource).not.toContain('"math.ping": {\n    ')
    // The upload members alone, a list by its own path, for uploadFile(file, { method, member }).
    expect(generated.schemasSource).toContain([
      'export const rpcUploadLimits = {',
      '  "documents.upload": {',
      '    statement: {',
      '      maxBytes: 1024,',
      '      contentTypes: ["text/csv", "Application/XML; charset=utf-8"],',
      '    },',
      '    receipts: {',
      '      maxBytes: 2048,',
      '    },',
      '    workbook: {',
      '      fileExtensions: [".xlsx", ".XLS"],',
      '    },',
      '  },',
      '} as const',
      '',
      'export type RpcUploadLimits = typeof rpcUploadLimits',
    ].join('\n'))
  })

  it('exports presets typed as the method params, sort keys and module constants', () => {
    const generated = generateRpcClientFiles(exportsSchema())

    expect(generated.schemasSource).toContain("import { z } from 'zod'\nimport type { RpcMethods } from './rpc-types.js'\n")
    expect(generated.schemasSource).toContain([
      'export const rpcPresets: {',
      '  readonly "invoices.list": {',
      '    readonly notExported: RpcMethods["invoices.list"][\'params\']',
      '    readonly toTransfer: RpcMethods["invoices.list"][\'params\']',
      '  }',
      '} = {',
      '  "invoices.list": {',
      '    notExported: {',
      '      status: "Approved",',
      '      size: 50,',
      '    },',
      '    toTransfer: {',
      '      toTransfer: true,',
      '    },',
      '  },',
      '}',
    ].join('\n'))
    expect(generated.schemasSource).toContain([
      'export const rpcSortKeys = {',
      '  "invoices.list": [',
      '    {',
      '      key: "createdAt",',
      '      direction: "descending",',
      '      default: true,',
      '    },',
      '    {',
      '      key: "number",',
      '      direction: "ascending",',
      '      default: false,',
      '    },',
      '  ],',
      '} as const',
    ].join('\n'))
    expect(generated.schemasSource).toContain([
      'export const rpcConstants = {',
      '  archive: {',
      '    enabled: true,',
      '  },',
      '  billing: {',
      '    currencies: ["EUR", "CHF"],',
    ].join('\n'))
    expect(generated.schemasSource).toContain('    defaultStatus: "Approved",\n    maxReceipts: 10,\n  },\n} as const')
  })

  it('rejects a file that breaks the declared upload limits before uploading it', () => {
    const generated = generateRpcClientFiles(exportsSchema())

    // The issue params carry the server's error code and the limit, for a Zod error map in the application's language.
    expect(generated.schemasSource).toContain(
      'statement: z.union([z.instanceof(Blob)'
      + '.refine((file) => file.size <= 1024, { message: "The file is larger than the accepted 1024 bytes.", '
      + 'params: {"code":"upload.too_large","maxBytes":1024} })'
      + '.refine((file) => ["text/csv","application/xml"].includes((file.type || \'application/octet-stream\')'
      + '.split(\';\')[0].trim().toLowerCase()), { message: "Files of this type are not accepted here; expected text/csv, '
      + 'Application/XML; charset=utf-8.", '
      + 'params: {"code":"upload.content_type","contentTypes":["text/csv","Application/XML; charset=utf-8"]} }), z.string()]),')
    expect(generated.schemasSource).toContain(
      'receipts: z.array(z.union([z.instanceof(Blob).refine((file) => file.size <= 2048, '
      + '{ message: "The file is larger than the accepted 2048 bytes.", '
      + 'params: {"code":"upload.too_large","maxBytes":2048} }), z.string()])).max(10),')
    // Extensions are compared lower-cased against the end of the name the uploader sends; a Blob has none.
    expect(generated.schemasSource).toContain(
      'workbook: z.union([z.instanceof(Blob).refine((file) => { const name = (file instanceof File ? file.name : \'\')'
      + '.toLowerCase(); return [".xlsx",".xls"].some((extension) => name.length > extension.length '
      + '&& name.endsWith(extension)) }, { message: "The file does not have an accepted extension; expected .xlsx, .XLS.", '
      + 'params: {"code":"upload.file_extension","fileExtensions":[".xlsx",".XLS"]} }), z.string()]).nullish().optional(),')
  })

  it('checks uploadFile against the declared limits of the method member it names, before uploading', async () => {
    const generated = generateRpcClientFiles(exportsSchema())
    const clientModule = await loadGeneratedUploadClient(generated.clientSource, generated.schemasSource)
    const server = uploadServer()
    const rpc = clientModule.createRpcApi({url: 'https://example.test/rpc', fetch: server.fetch})
    const refusal = async (upload: Promise<string>) => {
      const error = await upload.then(() => undefined, (rejected: unknown) => rejected)
      expect(error).toBeInstanceOf(clientModule.RpcUploadLimitError)
      expect(error).toBeInstanceOf(clientModule.RpcError)
      return error as {code: string; data: unknown; message: string; method: string; member: string; isInvalidParams: boolean}
    }

    const tooLarge = await refusal(rpc.$uploadFile(new File(['x'.repeat(1025)], 'a.csv', {type: 'text/csv'}),
      {method: 'documents.upload', member: 'statement'}))
    expect(tooLarge).toMatchObject({
      code: 'upload.too_large',
      data: {maxBytes: 1024, length: 1025},
      message: 'The file is larger than the accepted 1024 bytes.',
      method: 'documents.upload',
      member: 'statement',
      isInvalidParams: true,
    })
    // An untyped Blob is the application/octet-stream the uploader sends.
    const untyped = await refusal(rpc.$uploadFile(new Blob(['a']), {method: 'documents.upload', member: 'statement'}))
    expect(untyped).toMatchObject({
      code: 'upload.content_type',
      data: {contentTypes: ['text/csv', 'Application/XML; charset=utf-8'], contentType: 'application/octet-stream'},
    })
    // A list member's limits apply to each of its files.
    expect(await refusal(rpc.$uploadFile(new Blob(['x'.repeat(2049)]), {method: 'documents.upload', member: 'receipts'})))
      .toMatchObject({code: 'upload.too_large', data: {maxBytes: 2048, length: 2049}})
    // The extension of the name the endpoint will store: the fileName option, else File.name, else "upload".
    expect(await refusal(rpc.$uploadFile(new File(['x'], 'book.csv'), {method: 'documents.upload', member: 'workbook'})))
      .toMatchObject({
        code: 'upload.file_extension',
        data: {fileExtensions: ['.xlsx', '.XLS'], fileName: 'book.csv'},
        message: 'The file \'book.csv\' does not have an accepted extension; expected .xlsx, .XLS.',
      })
    expect(await refusal(rpc.$uploadFile(new Blob(['x']), {method: 'documents.upload', member: 'workbook'})))
      .toMatchObject({code: 'upload.file_extension', data: {fileName: 'upload'}})
    expect(server.uploads).toHaveLength(0)

    // Fitting files upload: the media type case-insensitive with parameters ignored, the extension case-insensitive.
    await rpc.$uploadFile(new File(['a'], 'a.xml', {type: 'application/xml;charset=utf-8'}),
      {method: 'documents.upload', member: 'statement'})
    await rpc.$uploadFile(new File(['a'], 'Q3.XLSX'), {method: 'documents.upload', member: 'workbook'})
    await rpc.$uploadFile(new Blob(['a']), {method: 'documents.upload', member: 'workbook', fileName: 'named.xls'})
    // Without a method nothing is checked; validateParams: false turns the check off.
    await rpc.$uploadFile(new Blob(['x'.repeat(4096)]))
    await clientModule.createRpcApi({url: 'https://example.test/rpc', fetch: server.fetch, validateParams: false})
      .$uploadFile(new Blob(['x'.repeat(4096)]), {method: 'documents.upload', member: 'statement'})
    expect(server.uploads.map((upload) => upload.fileName)).toEqual(['a.xml', 'Q3.XLSX', 'named.xls', null, null])
  })

  it('leaves the types and the client byte-identical, and emits no block for a schema without any', () => {
    const declared = generateRpcClientFiles(exportsSchema())
    const plain = exportsSchema()
    for (const method of Object.values(plain.methods)) {
      delete method.presets
      delete method.sortKeys
    }
    delete plain.constants
    const statement = plain.methods['documents.upload'].params.properties?.statement as JsonSchema
    delete statement['x-elarion-max-bytes']
    delete statement['x-elarion-content-types']
    const undeclared = generateRpcClientFiles(plain)

    expect(declared.typesSource).toBe(undeclared.typesSource)
    expect(declared.clientSource).toBe(undeclared.clientSource)
    expect(undeclared.schemasSource).not.toContain('rpcPresets')
    expect(undeclared.schemasSource).not.toContain('rpcSortKeys')
    expect(undeclared.schemasSource).not.toContain('rpcConstants')
    expect(undeclared.schemasSource).not.toContain('import type')
    expect(undeclared.schemasSource).toContain('statement: z.union([z.instanceof(Blob), z.string()]),')

    const limitFree = generateRpcClientFiles({
      methods: {'math.ping': {params: {type: 'object', properties: {name: {type: 'string'}}}, result: {type: 'string'}}},
    })
    expect(limitFree.schemasSource).not.toContain('rpcLimits')
    expect(limitFree.schemasSource.endsWith('export type RpcErrorDataSchemas = typeof rpcErrorDataSchemas\n')).toBe(true)
  })
})

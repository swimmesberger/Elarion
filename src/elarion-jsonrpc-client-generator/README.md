# @swimmesberger/elarion-jsonrpc-client-generator

Generate TypeScript RPC method contracts, Zod result schemas, and a portable fetch-based JSON-RPC client from an Elarion `rpc-schema.json` export.

```bash
npm install --save-dev @swimmesberger/elarion-jsonrpc-client-generator
npx elarion-jsonrpc-client-generator --schema rpc-schema.json --out src/generated
```

The generated files are:

| File | Purpose |
| --- | --- |
| `rpc-types.ts` | `RpcMethods` interface mapping method names to params/result types. |
| `rpc-schemas.ts` | `rpcResultSchemas` Zod map for runtime result validation. |
| `rpc-client.ts` | Browser/Node.js fetch client with typed single calls, batching, headers, `AbortSignal`, JSON-RPC errors, and Zod-backed result validation. |

The generated schema and client files import `zod`, so consuming applications should install `zod` as a runtime dependency.

## Generated API client

```ts
import { createRpcApi } from './generated/rpc-client'

const rpc = createRpcApi({
  url: '/rpc',
  headers: { Authorization: `Bearer ${token}` },
})

const abort = new AbortController()
const client = await rpc.clients.get({ id: clientId }, { signal: abort.signal })
```

The generated API mirrors dotted JSON-RPC method names as nested properties, so `clients.get` becomes `rpc.clients.get(...)`. The file also exports the lower-level `createRpcClient(...)` generic transport for advanced cases.

The API client uses `globalThis.fetch` in browsers and modern Node.js. Pass `fetch` explicitly for tests, older Node.js runtimes, server-function forwarding, or framework-specific transport wrappers:

```ts
const rpc = createRpcApi({
  url: process.env.API_INTERNAL_URL + '/rpc',
  fetch,
  headers: async ({ batch, methods }) => ({
    'X-RPC-Batch': String(batch),
    'X-RPC-Methods': methods.join(','),
  }),
})
```

Batching uses generated request builders, so params and results stay tied to each RPC method. Batch results preserve input order even when the server returns JSON-RPC responses out of order. Each item returns either `{ ok: true, result }` or `{ ok: false, error }`, so one method failure does not reject the whole batch:

```ts
const [clientResult, projectsResult] = await rpc.$batch([
  rpc.$request.clients.get({ id: clientId }),
  rpc.$request.projects.list({ clientId }),
] as const)
```

Result validation is enabled by default through `rpcResultSchemas`. Use `transformResult` for app-specific normalization before validation, or set `validateResults: false` when another layer validates responses.

## Client-side tracing

The generated client never imports an OpenTelemetry SDK. Instead it exposes an optional `instrumentation` hook so client-side tracing stays a host decision and adds zero dependencies. The client calls `startSpan` once per request (and once per batch), reads the returned span's `headers` to inject trace context into the outgoing request, then calls `setError`/`end` as the request settles:

```ts
interface RpcInstrumentation {
  startSpan(context: { methods: readonly string[]; batch: boolean }): RpcClientSpan | undefined
}

interface RpcClientSpan {
  readonly headers?: HeadersInit // e.g. { traceparent } — merged in last, so it stays authoritative
  setError(error: unknown): void
  end(): void
}
```

Minimal, dependency-free W3C context propagation (continues the server trace; ASP.NET Core reads `traceparent` automatically):

```ts
const rpc = createRpcApi({
  url: '/rpc',
  instrumentation: {
    startSpan() {
      const traceId = crypto.getRandomValues(new Uint8Array(16))
      const spanId = crypto.getRandomValues(new Uint8Array(8))
      const hex = (bytes: Uint8Array) => [...bytes].map((b) => b.toString(16).padStart(2, '0')).join('')
      const traceparent = `00-${hex(traceId)}-${hex(spanId)}-01`
      return { headers: { traceparent }, setError() {}, end() {} }
    },
  },
})
```

Hosts that already run `@opentelemetry/api` pass a small adapter that starts a real `CLIENT` span and injects context via the API's propagator — still no SDK in the generated client. Per-item application errors in a batch are returned as data (`{ ok: false, error }`), so the batch span ends without `setError`; only transport/protocol failures mark the span as errored.

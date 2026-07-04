# @swimmesberger/elarion-jsonrpc-client-generator

Generate TypeScript RPC method contracts, Zod params/result schemas, and a portable fetch-based JSON-RPC client from an Elarion `rpc-schema.json` export.

```bash
npm install --save-dev @swimmesberger/elarion-jsonrpc-client-generator
npx elarion-jsonrpc-client-generator --schema rpc-schema.json --out src/generated
```

Add `--watch` for a tight dev loop — the generator regenerates whenever `rpc-schema.json` changes (and survives the partial/invalid states a build tool leaves the file in mid-write). Pair it with a server that re-exports the schema on save (e.g. `dotnet watch`) so an edit to a handler flows straight to the typed client:

```bash
npx elarion-jsonrpc-client-generator --schema rpc-schema.json --out src/generated --watch
```

The generated files are:

| File | Purpose |
| --- | --- |
| `rpc-types.ts` | `RpcMethods` interface mapping method names to params/result types. |
| `rpc-schemas.ts` | `rpcParamsSchemas` and `rpcResultSchemas` Zod maps for runtime params/result validation. |
| `rpc-client.ts` | Browser/Node.js fetch client with typed single calls, batching, headers, `AbortSignal`, JSON-RPC errors, and Zod-backed params/result validation. |

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

Params validation is also enabled by default through `rpcParamsSchemas`: single calls and batch items are checked against the exported schema's constraints (lengths, ranges, patterns, formats) before the request is sent, and a failure throws `RpcParamsValidationError` locally — naming the method, with the underlying Zod error as `cause` — so tier-1 contract violations never reach the wire. Set `validateParams: false` to opt out.

## Error handling

A JSON-RPC error from the server is thrown as a typed `RpcError` (`code`, `message`, optional `data`). Elarion maps its `AppError` kinds onto the JSON-RPC server-reserved range, so the generated `RpcError` exposes a getter per kind — branch on the kind directly instead of re-wrapping:

```ts
import { RpcError, ElarionErrorCodes } from './generated/rpc-client'

try {
  await rpc.clients.get({ id })
} catch (error) {
  if (error instanceof RpcError && error.isNotFound) return renderNotFound()
  throw error
}
```

`isNotFound` (`-32001`), `isConflict` (`-32002`), `isForbidden` (`-32003`), `isBusinessRule` (`-32004`), and `isUnauthorized` (`-32005`) cover the Elarion application kinds; `isInvalidParams` (`-32602`) and `isInternalError` (`-32603`) cover `Validation`/`Internal`; and the standard `isParseError`/`isInvalidRequest`/`isMethodNotFound` getters stay available. The application codes are also exported as `ElarionErrorCodes` for `switch` statements.

## Framework adapters

The core client stays framework-neutral, but you can opt into a framework adapter emitted as a separate file. Pass `--framework tanstack-start` to also emit `start-adapter.ts` (needs the `@tanstack/react-start` peer dependency):

```bash
npx elarion-jsonrpc-client-generator --schema rpc-schema.json --out src/generated --framework tanstack-start
```

It exports `createStartRpcApi` — a `createRpcApi` with request-scoped cookie forwarding pre-wired for SSR — and `forwardRequestCookie`, the isomorphic headers function on its own. The core client never imports the adapter, so consumers that don't opt in stay framework-neutral and their output is byte-identical.

```ts
import { createStartRpcApi } from './generated/start-adapter'

export const rpc = createStartRpcApi({ url: '/rpc' })
```

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

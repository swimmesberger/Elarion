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
